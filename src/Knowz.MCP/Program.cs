using Knowz.MCP.Config;
using Knowz.MCP.Endpoints;
using Knowz.MCP.Middleware;
using Knowz.MCP.Services;
using Knowz.MCP.Services.Session;
using Knowz.MCP.Tools;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.Extensibility;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
    McpRequestBodyLimits.Configure(options, builder.Configuration));

// Load optional local settings (gitignored, for development secrets)
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Add Application Insights
builder.Services.AddApplicationInsightsTelemetry();

// Tag App Insights telemetry with a stable cloud role name so MCP is
// distinguishable from API/Functions in cross-service views. MCP does not
// reference Knowz.Infrastructure (where the shared CloudRoleNameInitializer
// lives), so a tiny local initializer is registered here instead.
builder.Services.AddSingleton<ITelemetryInitializer, KnowzMcpRoleNameInitializer>();

// Add Sentry (optional - empty DSN disables Sentry)
var sentryDsn = builder.Configuration["Sentry:Dsn"] ?? "";
builder.WebHost.UseSentry(options =>
{
    options.Dsn = sentryDsn;
    options.Environment = builder.Environment.EnvironmentName;
    options.Release = typeof(Program).Assembly.GetName().Version?.ToString();
    options.TracesSampleRate = 1.0;
});

// Add HttpContextAccessor for tool access to API key
builder.Services.AddHttpContextAccessor();

var mcpApiTimeoutSeconds = builder.Configuration.GetValue<int?>("MCP_API_TIMEOUT_SECONDS")
                           ?? builder.Configuration.GetValue<int?>("MCP:ApiTimeoutSeconds")
                           ?? 300;
mcpApiTimeoutSeconds = Math.Clamp(mcpApiTimeoutSeconds, 30, 600);

// Add services
builder.Services.AddHttpClient("McpApiClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(mcpApiTimeoutSeconds);
    client.DefaultRequestHeaders.Add("User-Agent", "Knowz-MCP-Server");
});

builder.Services.AddSingleton<ISseConnectionManager, SseConnectionManager>();
builder.Services.AddSingleton<IOAuthService, OAuthService>();
builder.Services.AddSingleton<IMcpSSOService, McpSSOService>();

// Redis-backed session store (falls back to in-memory if Redis unavailable).
// Matches the API/Infrastructure pattern: env var `Redis__ConnectionString` is
// translated to `Redis:ConnectionString` by ASP.NET Core config; the literal
// underscore-form lookup is kept as a final safety net for legacy local configs.
var redisConnection = builder.Configuration["Redis:ConnectionString"]
                      ?? builder.Configuration["ConnectionStrings:Redis"]
                      ?? builder.Configuration["Redis__ConnectionString"];
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnection;
        options.InstanceName = "mcp:";
    });
    builder.Services.AddSingleton<IMcpSessionStore, RedisMcpSessionStore>();
}
else
{
    builder.Services.AddDistributedMemoryCache();
    builder.Services.AddSingleton<IMcpSessionStore, RedisMcpSessionStore>();
}

builder.Services.AddSingleton<IActiveSessionTracker, ActiveSessionTracker>();
builder.Services.AddHostedService<SessionCleanupBackgroundService>();

// Register proxy backend services
builder.Services.AddMcpBackend(builder.Configuration);

// Register SDK tools
builder.Services.AddScoped<KnowzProxyTools>();

// Add MCP Server with HTTP Transport using official SDK
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "knowz-mcp",
            Version = "2.0.0"
        };
    })
    .WithHttpTransport(options =>
    {
        // Idle timeout before MCP SDK closes the session (7 days — survives weekends)
        options.IdleTimeout = TimeSpan.FromDays(7);

        // Session handler - stores API key in session store for access during tool invocations
        options.RunSessionHandler = async (context, server, cancellationToken) =>
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
            var sessionStore = context.RequestServices.GetRequiredService<IMcpSessionStore>();
            var sessionTracker = context.RequestServices.GetRequiredService<IActiveSessionTracker>();
            var apiKey = context.Items["ApiKey"] as string;

            // Get or generate session ID
            var sessionId = context.Request.Headers["Mcp-Session-Id"].FirstOrDefault()
                           ?? context.Response.Headers["Mcp-Session-Id"].FirstOrDefault()
                           ?? Guid.NewGuid().ToString();

            // Track this session so the middleware knows the SDK recognizes it
            // (prevents 404 after container deployments)
            sessionTracker.Track(sessionId);

            // Store session ID in HttpContext for tool access
            context.Items["McpSessionId"] = sessionId;

            // Store API key in session store for cross-request access
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                sessionStore.StoreApiKey(sessionId, apiKey);

                // Cookie fallback for clients that don't send Mcp-Session-Id on subsequent requests
                context.Response.Cookies.Append("mcp_session", sessionId, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.None, // Required for cross-origin MCP clients
                    MaxAge = OAuthService.SessionCookieMaxAge
                });

                logger.LogInformation("MCP session {SessionId} started with authenticated user", sessionId);
            }
            else
            {
                logger.LogWarning("MCP session {SessionId} started without authentication", sessionId);
            }

            try
            {
                await server.RunAsync(cancellationToken);
            }
            finally
            {
                sessionTracker.Remove(sessionId);
                logger.LogInformation("MCP session {SessionId} ended", sessionId);
            }
        };
    })
    .WithTools<KnowzProxyTools>();

// MCP_SelfHostedToolContract R1/R3: in self-hosted mode the advertised tool list is filtered to
// what Knowz.SelfHosted.API can actually run, and three descriptions are replaced with SH-true
// text. Registered AFTER AddMcpServer/WithTools so the SDK's own options setup has already
// populated ToolCollection, and it mutates that collection because McpServerHandlers.ListToolsHandler
// AUGMENTS the collection (it cannot remove) - WithListToolsHandler is deliberately NOT used here.
// Inert in proxy mode: api.knowz.io ships this same binary and must keep all 36 tools.
builder.Services.AddSelfHostedToolVisibility(builder.Configuration);

// Add CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(
            "https://knowz.io", "https://dev.knowz.io",
            "https://mcp.knowz.io", "https://mcp.dev.knowz.io",
            "http://localhost:3000", "http://localhost:5173", "http://localhost:8080"
        ).AllowAnyMethod().AllowAnyHeader().AllowCredentials();
    });
});

var app = builder.Build();

app.UseCors();

// Auth middleware extracts API key and stores in HttpContext.Items["ApiKey"]
app.UseMiddleware<McpAuthMiddleware>();

// Defensive: ensure Mcp-Session-Id is in response headers for clients that need it.
// The SDK should set this, but some versions/response types may not include it.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        if (context.Items.TryGetValue("McpSessionId", out var sid) && sid != null)
        {
            if (!context.Response.Headers.ContainsKey("Mcp-Session-Id"))
                context.Response.Headers["Mcp-Session-Id"] = sid.ToString();
        }
        return Task.CompletedTask;
    });
    await next();
});

// Map MCP endpoint using official SDK (replaces manual JSON-RPC handling)
app.MapMcp("/mcp");

// Map endpoints from organized endpoint classes
app.MapHealthEndpoints();
app.MapOAuthEndpoints();
app.MapLegacySseEndpoints();

app.Run();

/// <summary>
/// Sets a stable Application Insights cloud role name for the MCP server so its
/// telemetry is filterable separately from the API and Functions roles. MCP does
/// not reference Knowz.Infrastructure, so this small local initializer stands in
/// for the shared CloudRoleNameInitializer.
/// </summary>
internal sealed class KnowzMcpRoleNameInitializer : ITelemetryInitializer
{
    public void Initialize(ITelemetry telemetry)
    {
        telemetry.Context.Cloud.RoleName = "knowz-mcp";
    }
}

public static class McpRequestBodyLimits
{
    public const long DefaultMaxRequestBodyBytes = 64L * 1024 * 1024;

    public static void Configure(
        Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions options,
        IConfiguration configuration)
    {
        options.Limits.MaxRequestBodySize =
            configuration.GetValue<long?>("MCP_MAX_REQUEST_BODY_BYTES") ?? DefaultMaxRequestBodyBytes;
    }
}
