using System.Threading.Channels;
using System.Threading.RateLimiting;
using Azure.Core;
using Azure.Identity;
using Knowz.Core.Configuration;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.API.Endpoints;
using Knowz.SelfHosted.API.Middleware;
using Knowz.SelfHosted.API.Services;
using Knowz.SelfHosted.Application.Extensions;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Application.Options;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Extensions;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Load optional local settings (gitignored, for development secrets)
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Re-add environment variables so Aspire-injected values (ConnectionStrings, AI config)
// take precedence over appsettings.Local.json when running under Aspire AppHost.
builder.Configuration.AddEnvironmentVariables();

// Optional: Azure Key Vault configuration (enterprise secret store)
var kvEnabled = builder.Configuration.GetValue<bool>("AzureKeyVault:Enabled");
var kvUri = builder.Configuration["AzureKeyVault:VaultUri"];
var keyVaultConfigured = false;
if (kvEnabled && !string.IsNullOrWhiteSpace(kvUri))
{
    builder.Configuration.AddAzureKeyVault(new Uri(kvUri), new DefaultAzureCredential());
    keyVaultConfigured = true;
}

// Prepare one persistent key ring before loading encrypted database overrides. The same
// provider is registered for runtime writes, so restarts can decrypt the saved settings.
var configurationProtection = ConfigurationBootstrap.CreateDataProtection(builder.Configuration);
builder.Services.AddSingleton<IDataProtectionProvider>(configurationProtection);

// Database-backed configuration provider (added last so DB values override file-based config)
var dbConnectionString = builder.Configuration.GetConnectionString("McpDb");
var dbConfigSource = new DatabaseConfigurationSource
{
    ConnectionString = dbConnectionString ?? string.Empty,
    DataProtectionProvider = configurationProtection,
    HostAiConfigurationUpdatedAt = AiRuntimePolicy.ProviderRevision(builder.Configuration)
};
((IConfigurationBuilder)builder.Configuration).Add(dbConfigSource);
builder.Services.AddSingleton(new RunningConfiguration(builder.Configuration));

// Bind SelfHostedOptions with startup-time validation (SEC_P0Triage Item 4).
// SelfHostedOptionsValidator crashes the app at boot when JWT auth is enabled
// but the signing secret is missing or too short, instead of letting
// AuthenticationMiddleware silently reject requests or fall back to a literal.
builder.Services.AddSingleton<IValidateOptions<SelfHostedOptions>, SelfHostedOptionsValidator>();
builder.Services.AddOptions<SelfHostedOptions>()
    .BindConfiguration(SelfHostedOptions.SectionName)
    .ValidateOnStart();

// Tenant provider (must be registered before database so DbContext can resolve it)
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantProvider, HttpTenantProvider>();

// Azure TokenCredential singleton (SH_ENTERPRISE_MI_SWAP §2.1):
// single source of truth for Azure SDK auth across the self-hosted stack. Infra-layer
// extensions (Storage / OpenAI / Search / AttachmentAI) consume this via DI so tests
// can substitute AzureCliCredential or a mock without touching every Add* extension.
// Dev: AzureCliCredential (fast path). Non-dev: DefaultAzureCredential with the slow
// interactive sources excluded.
builder.Services.AddSingleton<TokenCredential>(_ =>
{
    var useAzureCli = builder.Environment.IsDevelopment()
        && builder.Configuration.GetValue("Azure:UseAzureCliCredential", true);
    return useAzureCli
        ? new AzureCliCredential()
        : new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeInteractiveBrowserCredential = true,
            ExcludeSharedTokenCacheCredential = true,
            ExcludeVisualStudioCredential = true,
            ManagedIdentityClientId = builder.Configuration["Azure:ManagedIdentityClientId"]
        });
});

// Shared library DI extensions (identical to MCP Direct mode)
builder.Services.AddSelfHostedDatabase(builder.Configuration);
builder.Services.AddSelfHostedSearch(builder.Configuration);
builder.Services.AddSelfHostedOpenAI(builder.Configuration);
// SelfHostedAbstractionSeams: register IEmbeddingService + IVectorStore adapters
// AFTER the underlying tiers so the adapters resolve the chosen implementations.
builder.Services.AddSelfHostedEmbeddingService();
builder.Services.AddSelfHostedVectorStore();
builder.Services.AddSelfHostedFileStorage(builder.Configuration);
builder.Services.AddDocumentIntelligence(builder.Configuration);
builder.Services.AddAttachmentAI(builder.Configuration);
builder.Services.AddSelfHostedApplication();

// Enrichment pipeline: bounded channel + background service
builder.Services.AddSingleton(Channel.CreateBounded<EnrichmentWorkItem>(
    new BoundedChannelOptions(100)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true
    }));
builder.Services.AddHostedService<EnrichmentBackgroundService>();

// Git sync pipeline: bounded channel + background service
builder.Services.AddSingleton(Channel.CreateBounded<GitSyncWorkItem>(
    new BoundedChannelOptions(10)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true
    }));
builder.Services.AddHostedService<GitSyncBackgroundService>();

// One-shot data-copy migration: backfills PlatformConnection rows from the legacy per-link
// columns on VaultSyncLink. Idempotent — safe to run on every boot.
builder.Services.AddHostedService<PlatformConnectionMigrationService>();

// SH_ENTERPRISE_CREDENTIAL_BOOTSTRAP §2.1: first-run bootstrap hosted service.
// Seeds SuperAdmin + mints bootstrap API key to Key Vault (idempotent).
builder.Services.AddHostedService<BootstrapService>();

// Upload size ceiling: 1 GB. Raise BOTH the MVC multipart form limit and
// Kestrel's global MaxRequestBodySize (default 30 MB). Kestrel rejects the
// request with 413 before the form binder ever sees it, so the two limits
// must be set together.
const long MaxUploadBytes = 1024L * 1024 * 1024; // 1 GB

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = MaxUploadBytes;
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = MaxUploadBytes;
});

// Register DatabaseConfigurationProvider as singleton for reload support
// Nullable: the provider may not exist if DB config source was not added
builder.Services.AddSingleton(sp =>
{
    var configRoot = sp.GetRequiredService<IConfiguration>() as IConfigurationRoot;
    return configRoot?.Providers
        .OfType<DatabaseConfigurationProvider>()
        .FirstOrDefault();
});

// Auth and admin services
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ITenantManagementService, TenantManagementService>();
builder.Services.AddScoped<IUserManagementService, UserManagementService>();

// SSO service
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("selfhosted-config-probe").ConfigurePrimaryHttpMessageHandler(() =>
    new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddScoped<ISelfHostedSSOService, SelfHostedSSOService>();

// Platform sync HTTP client (NodeID PlatformSyncConnection) — used by PlatformSyncClient
// and PlatformConnectionService. 30s timeout, 50 MB hard response cap, no redirects (SSRF).
builder.Services.AddHttpClient("KnowzPlatformSync", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.MaxResponseContentBufferSize = 50 * 1024 * 1024; // 50 MB
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Knowz-SelfHosted/1.0");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false
});

// JSON enum serialization: accept both "User" and 2 for all enum properties
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter());
});

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Knowz Self-Hosted API", Version = "v1" });

    // API Key auth scheme
    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = "X-Api-Key",
        Description = "API key authentication (legacy global key or per-user ksh_ key)"
    });

    // JWT Bearer auth scheme
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "JWT Bearer token from /api/v1/auth/login"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" }
            },
            Array.Empty<string>()
        },
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

// CORS
var allowedOrigins = builder.Configuration
    .GetSection("SelfHosted:AllowedOrigins")
    .Get<string[]>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins is { Length: > 0 })
            policy.WithOrigins(allowedOrigins).AllowAnyMethod().AllowAnyHeader();
        else
            policy.WithOrigins("https://localhost", "http://localhost").AllowAnyMethod().AllowAnyHeader();
    });
});

// Rate limiting
var rateLimitingEnabled = builder.Configuration.GetValue("SelfHosted:RateLimiting:Enabled", true);
if (rateLimitingEnabled)
{
    var globalPermitLimit = builder.Configuration.GetValue("SelfHosted:RateLimiting:Global:PermitLimit", 100);
    var globalWindowSeconds = builder.Configuration.GetValue("SelfHosted:RateLimiting:Global:WindowSeconds", 60);
    var authPermitLimit = builder.Configuration.GetValue("SelfHosted:RateLimiting:Auth:PermitLimit", 5);
    var authWindowSeconds = builder.Configuration.GetValue("SelfHosted:RateLimiting:Auth:WindowSeconds", 15);

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.OnRejected = async (context, cancellationToken) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                context.HttpContext.Response.Headers.RetryAfter =
                    ((int)retryAfter.TotalSeconds).ToString();
            }

            context.HttpContext.Response.ContentType = "application/json";
            await context.HttpContext.Response.WriteAsJsonAsync(
                new { error = "Too many requests. Please try again later." },
                cancellationToken);
        };

        // Auth policy: sliding window per IP (stricter, for login endpoint)
        options.AddPolicy("auth", httpContext =>
            RateLimitPartition.GetSlidingWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = authPermitLimit,
                    Window = TimeSpan.FromSeconds(authWindowSeconds),
                    SegmentsPerWindow = 3,
                    QueueLimit = 0
                }));

        // Global limiter as default for all endpoints
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            // Exclude health endpoints from rate limiting
            var path = httpContext.Request.Path.Value ?? "";
            if (path.StartsWith("/healthz", StringComparison.OrdinalIgnoreCase))
            {
                return RateLimitPartition.GetNoLimiter("health");
            }

            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = globalPermitLimit,
                    Window = TimeSpan.FromSeconds(globalWindowSeconds),
                    QueueLimit = 0
                });
        });
    });
}

var app = builder.Build();

if (keyVaultConfigured)
{
    app.Logger.LogInformation("Azure Key Vault configuration enabled: {VaultUri}", kvUri);
}

// Warn if CORS origins not configured
if (allowedOrigins is not { Length: > 0 })
{
    app.Logger.LogWarning(
        "SelfHosted:AllowedOrigins not configured — CORS restricted to localhost only. Set origins for production access.");
}

// Auto-migrate database when configured (for Aspire local dev).
// SH_ENTERPRISE_RUNTIME_RESILIENCE §Rule 3: fail-closed on exhausted retries via
// MigrationRunner helper. Orchestrator restarts the container on throw, which is
// the correct action when the DB just wasn't ready yet.
var migrationSucceeded = false;
if (builder.Configuration.GetValue<bool>("Database:AutoMigrate"))
{
    await MigrationRunner.RunWithRetryAsync(
        migrateAsync: async attempt =>
        {
            using var scope = app.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            await dbContext.Database.MigrateAsync();
            await SelfHostedPgvectorDdl.ApplyAsync(dbContext, builder.Configuration);
            app.Logger.LogInformation("Database migration completed successfully on attempt {Attempt}.", attempt);
        },
        maxRetries: 10,
        delay: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), // 1s, 2s, 4s, 8s, 16s...
        onRetry: (attempt, ex) =>
            app.Logger.LogWarning(ex,
                "Database migration attempt {Attempt}/10 failed: {Message}. Retrying with backoff...",
                attempt, ex.Message),
        onFailure: ex =>
            app.Logger.LogCritical(ex,
                "Database migration failed after 10 attempts — aborting startup."));
    migrationSucceeded = true;
}
else
{
    // Not using auto-migrate — assume tables exist (production with pre-applied migrations)
    migrationSucceeded = true;
}

// Seed SuperAdmin and config ONLY if migration succeeded
if (migrationSucceeded)
{
    // Seed SuperAdmin on startup
    using (var scope = app.Services.CreateScope())
    {
        var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();
        try
        {
            await authService.EnsureSuperAdminExistsAsync();
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Failed to seed SuperAdmin user.");
        }
    }

    // Seed configuration from appsettings into DB (first run only)
    using (var scope = app.Services.CreateScope())
    {
        try
        {
            var configService = scope.ServiceProvider.GetRequiredService<IConfigurationManagementService>();
            await configService.SeedFromConfigurationAsync(builder.Configuration);

            // Update the DatabaseConfigurationSource with Data Protection provider for reload
            var dpProvider = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
            dbConfigSource.DataProtectionProvider = dpProvider;

            // SEC_P0Triage §Rule 4: wire a real logger so denied-secret-tier keys
            // (LogWarning) and decrypt failures (LogError) surface in telemetry.
            // The source was built before DI existed; attach now so Reload() calls
            // after config edits get the same treatment as the initial Load.
            dbConfigSource.Logger = scope.ServiceProvider
                .GetRequiredService<ILogger<DatabaseConfigurationProvider>>();
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Failed to seed configuration from appsettings.");
        }
    }
}

// Warn if API authentication is disabled
var selfHostedOptions = app.Services.GetRequiredService<IOptions<SelfHostedOptions>>().Value;
if (string.IsNullOrWhiteSpace(selfHostedOptions.ApiKey) && string.IsNullOrWhiteSpace(selfHostedOptions.JwtSecret))
{
    app.Logger.LogCritical("No authentication configured (JwtSecret and ApiKey are both empty). " +
        "All API requests will be rejected with 401. Set 'SelfHosted:JwtSecret' or 'SelfHosted:ApiKey' to enable authentication.");
}

// Log active AI/Search provider
var platformEnabled = string.Equals(builder.Configuration["KnowzPlatform:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
var aiDisabledByHost = AiRuntimePolicy.IsDisabled(builder.Configuration);
if (aiDisabledByHost)
{
    app.Logger.LogInformation("AI Provider: None (disabled by runtime host)");
    app.Logger.LogInformation("Search Provider: Database keyword search");
}
else if (platformEnabled)
{
    var platformUrl = builder.Configuration["KnowzPlatform:BaseUrl"] ?? "(not set)";
    app.Logger.LogInformation("AI Provider: Knowz Platform ({PlatformUrl})", platformUrl);
    app.Logger.LogInformation("Search Provider: Local Text Search");
}
else
{
    var compatibleConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["OpenAiCompatible:Endpoint"]);
    var openAiConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["AzureOpenAI:Endpoint"]);
    var searchConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["AzureAISearch:Endpoint"]);

    if (compatibleConfigured)
        app.Logger.LogInformation("AI Provider: OpenAI-compatible ({Endpoint})", builder.Configuration["OpenAiCompatible:Endpoint"]);
    else if (openAiConfigured)
        app.Logger.LogInformation("AI Provider: Azure OpenAI ({Endpoint})", builder.Configuration["AzureOpenAI:Endpoint"]);
    else
        app.Logger.LogWarning("AI Provider: None (NoOp) — AI features disabled");

    if (searchConfigured)
        app.Logger.LogInformation("Search Provider: Azure AI Search ({Endpoint})", builder.Configuration["AzureAISearch:Endpoint"]);
    else if (compatibleConfigured || openAiConfigured)
        app.Logger.LogInformation("Search Provider: Local vector search");
    else
        app.Logger.LogInformation("Search Provider: Database (SQL keyword search — no vector/semantic)");
}

// MI swap SH_ENTERPRISE_MI_SWAP §2.5: attachment AI capabilities are now endpoint-only;
// the MI-issued token is validated on first Azure SDK call.
var attachmentVisionConfigured = !aiDisabledByHost &&
    !string.IsNullOrWhiteSpace(builder.Configuration["AzureAIVision:Endpoint"]);
var attachmentDocumentConfigured = !aiDisabledByHost &&
    !string.IsNullOrWhiteSpace(builder.Configuration["AzureDocumentIntelligence:Endpoint"]);
var attachmentSynthesisConfigured = !aiDisabledByHost &&
    !string.IsNullOrWhiteSpace(builder.Configuration["AzureOpenAI:Endpoint"]) &&
    !string.IsNullOrWhiteSpace(builder.Configuration["AzureOpenAI:DeploymentName"]);

app.Logger.LogInformation(
    "Attachment AI: Vision={VisionConfigured}, DocumentIntelligence={DocumentConfigured}, ModelSynthesis={SynthesisConfigured}",
    attachmentVisionConfigured,
    attachmentDocumentConfigured,
    attachmentSynthesisConfigured);

// SH_ENTERPRISE_RUNTIME_RESILIENCE §Rule 1-2: validate the DI surface at startup.
// Required services throw; optional services warn. Strict mode (throw) is on in
// every non-Development environment unless overridden via Knowz:StrictDIValidation.
{
    var validatorLogger = app.Services.GetRequiredService<ILogger<Program>>();
    validatorLogger.LogInformation("Validating self-hosted DI dependencies…");

    var validationResult = StartupDependencyValidator.ValidateSelfHostedDependencies(
        app.Services,
        requiredServices: new[]
        {
            typeof(SelfHostedDbContext),
            typeof(IOpenAIService),
            typeof(ISearchService),
            typeof(IFileStorageProvider),
        },
        optionalServices: SelfHostedOptionalList.Default,
        services: builder.Services);

    var strictDiValidation = builder.Configuration.GetValue<bool?>("Knowz:StrictDIValidation")
        ?? !builder.Environment.IsDevelopment();

    foreach (var warning in validationResult.Warnings)
    {
        validatorLogger.LogWarning("[Optional] {Service}: {Error}. Fix: {Fix}",
            warning.ServiceTypeName, warning.ErrorMessage, warning.ConfigurationHint);
    }

    if (!validationResult.IsValid)
    {
        if (strictDiValidation)
        {
            validatorLogger.LogCritical(
                "DI validation FAILED with {Count} error(s) (strict mode; Env={Env}). Aborting startup. Override with Knowz:StrictDIValidation=false.\n{Report}",
                validationResult.Errors.Count, builder.Environment.EnvironmentName, validationResult.GetDetailedReport());
            validationResult.ThrowIfInvalid();
        }
        else
        {
            validatorLogger.LogWarning(
                "DI validation has {Count} error(s) in {Env} — continuing anyway (lenient mode). Enable strict mode with Knowz:StrictDIValidation=true.\n{Report}",
                validationResult.Errors.Count, builder.Environment.EnvironmentName, validationResult.GetDetailedReport());
        }
    }
    else
    {
        validatorLogger.LogInformation(
            "DI validation PASSED ({Successes} required services, {Warnings} optional warnings)",
            validationResult.Successes.Count, validationResult.Warnings.Count);
    }
}

// Honor X-Forwarded-* headers from any upstream proxy so HttpRequest.Scheme
// and HttpRequest.Host reflect the public origin, not the internal hop.
// Required for correct SSO redirect_uri construction (see SelfHostedSSOService
// and AuthEndpoints /sso/authorize). KnownNetworks/Proxies are cleared because
// proxy IPs in container/ingress topologies are not fixed.
var forwardedHeaderOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor
                     | ForwardedHeaders.XForwardedProto
                     | ForwardedHeaders.XForwardedHost,
};
forwardedHeaderOptions.KnownNetworks.Clear();
forwardedHeaderOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeaderOptions);

app.UseCors();

// Rate limiting (after CORS, before auth)
if (rateLimitingEnabled)
{
    app.UseRateLimiter();
}

// Auth middleware (replaces legacy ApiKeyAuthMiddleware)
app.UseMiddleware<AuthenticationMiddleware>();

// Swagger (configurable)
// Config-gated only. appsettings.Development.json sets EnableSwagger=true for
// local debug; compose / Production images stay off unless ENABLE_SWAGGER=true.
var enableSwagger = builder.Configuration.GetValue("SelfHosted:EnableSwagger", false);
if (enableSwagger)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Static files (for embedded Web UI)
app.UseDefaultFiles();
app.UseStaticFiles();

// Map API endpoints
app.MapHealthEndpoints();
app.MapKnowledgeEndpoints();
app.MapVaultKnowledgeCommitHistoryEndpoints();
app.MapVaultEndpoints();
app.MapSearchEndpoints();
app.MapTopicEndpoints();
app.MapEntityEndpoints();
app.MapTagEndpoints();
app.MapInboxEndpoints();
app.MapAuthEndpoints();
app.MapAccountEndpoints();
app.MapAdminEndpoints();
app.MapAdminEnrichmentEndpoints();
app.MapBootstrapEndpoints();
app.MapConfigurationEndpoints();
app.MapPortabilityEndpoints();
app.MapSyncEndpoints();
app.MapApiKeyEndpoints();
app.MapChatEndpoints();
app.MapFileEndpoints();
app.MapAttachmentAIAdminEndpoints();
app.MapCommentEndpoints();
app.MapVersionEndpoints();
app.MapAuditEndpoints();
app.MapGitSyncEndpoints();
app.MapSSOConfigEndpoints();
app.MapVaultAccessEndpoints();
app.MapInternalMcpEndpoints();
app.MapKnowledgeItemTypesEndpoints();

// SPA fallback (must be last)
app.MapFallbackToFile("index.html");

app.Run();
