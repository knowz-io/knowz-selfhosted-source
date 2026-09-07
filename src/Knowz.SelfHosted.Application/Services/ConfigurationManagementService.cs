using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Azure.Core;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Application.Services;

public class ConfigurationManagementService : IConfigurationManagementService
{
    private readonly SelfHostedDbContext _db;
    private readonly IDataProtector _dataProtector;
    private readonly DatabaseConfigurationProvider? _configProvider;
    private readonly ILogger<ConfigurationManagementService> _logger;
    private readonly IConfiguration _configuration;
    private readonly RunningConfiguration _running;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly TokenCredential? _credential;

    // Track restart-required changes since startup
    private static readonly DateTime _startupTime = DateTime.UtcNow;

    public ConfigurationManagementService(
        SelfHostedDbContext db,
        IDataProtectionProvider dataProtectionProvider,
        DatabaseConfigurationProvider? configProvider,
        ILogger<ConfigurationManagementService> logger,
        IConfiguration configuration, IHttpClientFactory? httpClientFactory = null, TokenCredential? credential = null, RunningConfiguration? running = null)
    {
        _db = db;
        _dataProtector = dataProtectionProvider.CreateProtector("Knowz.SelfHosted.SystemConfiguration");
        _configProvider = configProvider;
        _logger = logger;
        _running = running ?? new RunningConfiguration(configuration);
        _configuration = _running.Values;
        _httpClientFactory = httpClientFactory;
        _credential = credential;
    }

    public async Task<List<ConfigCategoryDto>> GetAllCategoriesAsync()
    {
        var dbEntries = await _db.SystemConfigurations.ToListAsync();
        var result = new List<ConfigCategoryDto>();

        foreach (var (categoryName, schema) in CategorySchemas)
        {
            result.Add(BuildCategoryDto(categoryName, schema, dbEntries));
        }

        return result;
    }

    public async Task<ConfigCategoryDto?> GetCategoryAsync(string category)
    {
        if (!CategorySchemas.TryGetValue(category, out var schema))
            return null;

        var dbEntries = await _db.SystemConfigurations
            .Where(sc => sc.Category == category)
            .ToListAsync();

        return BuildCategoryDto(category, schema, dbEntries);
    }

    public async Task<ConfigUpdateResult> UpdateCategoryAsync(
        string category, List<ConfigEntryUpdateDto> entries, string modifiedBy)
    {
        if (!CategorySchemas.TryGetValue(category, out var schema))
        {
            return new ConfigUpdateResult
            {
                Success = false,
                Errors = new List<string> { $"Unknown category: {category}" }
            };
        }

        if (AiRuntimePolicy.IsDisabled(_configuration) && AiRuntimePolicy.IsAiCategory(category))
            return new ConfigUpdateResult { Success = false, Errors = [AiRuntimePolicy.Guidance] };

        var errors = new List<string>();
        var fieldErrors = new Dictionary<string, string>();
        foreach (var entry in entries)
        {
            if (!schema.Keys.ContainsKey(entry.Key))
            {
                errors.Add($"Unknown key '{entry.Key}' in category '{category}'");
            }
            else if (ConfigurationAuthority.IsExternallyManaged($"{category}:{entry.Key}") && !IsKeepExistingSentinel(entry.Value))
            {
                fieldErrors[entry.Key] = "Managed setting; use knowz setup on the host or your managed secret store.";
                errors.Add($"{entry.Key}: {fieldErrors[entry.Key]}");
            }
            else if (!IsKeepExistingSentinel(entry.Value) && ValidateValue(entry.Key, entry.Value) is { } validation)
            {
                fieldErrors[entry.Key] = validation;
                errors.Add($"{entry.Key}: {validation}");
            }
        }
        if (entries.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count() != entries.Count)
            errors.Add("Duplicate setting keys are not allowed.");

        if (errors.Count > 0)
        {
            return new ConfigUpdateResult { Success = false, Errors = errors, FieldErrors = fieldErrors };
        }

        var dbEntries = await _db.SystemConfigurations
            .Where(sc => sc.Category == category)
            .ToListAsync();

        var restartRequired = false;
        var entriesUpdated = 0;

        foreach (var entry in entries)
        {
            var keySchema = schema.Keys[entry.Key];

            // Sentinel detection: if value is all asterisks, keep existing
            if (IsKeepExistingSentinel(entry.Value))
                continue;

            var existing = dbEntries.FirstOrDefault(e => e.Key == entry.Key);
            var hasOverride = existing?.EncryptedValue is not null && existing.LastModifiedBy != "system-seed" && !IsSuperseded(existing);
            var current = hasOverride ? TryReadOverride(existing!) : _configuration[$"{category}:{entry.Key}"];
            // An unreadable override must be replaceable even when re-entering the fallback.
            if ((!hasOverride || current is not null) && string.Equals(current ?? "", entry.Value ?? "", StringComparison.Ordinal)) continue;

            // Empty is an explicit clear, distinct from an omitted override.
            var encryptedValue = _dataProtector.Protect(entry.Value ?? "");

            if (existing is not null)
            {
                existing.EncryptedValue = encryptedValue;
                existing.IsSecret = keySchema.IsSecret;
                existing.RequiresRestart = true;
                existing.Description = keySchema.Description;
                existing.LastModifiedAt = DateTime.UtcNow;
                existing.LastModifiedBy = modifiedBy;
            }
            else
            {
                _db.SystemConfigurations.Add(new SystemConfiguration
                {
                    Category = category,
                    Key = entry.Key,
                    EncryptedValue = encryptedValue,
                    IsSecret = keySchema.IsSecret,
                    RequiresRestart = true,
                    Description = keySchema.Description,
                    LastModifiedAt = DateTime.UtcNow,
                    LastModifiedBy = modifiedBy
                });
            }

            restartRequired = true;

            entriesUpdated++;
        }

        await _db.SaveChangesAsync();

        // Provider instances/options are fixed for this process. Applying only part of
        // a pending category would misreport effective state; apply atomically on restart.

        return new ConfigUpdateResult
        {
            Success = true,
            RestartRequired = restartRequired,
            EntriesUpdated = entriesUpdated
        };
    }

    public async Task<ServiceHealthResult> TestConnectionAsync(string category, CancellationToken cancellationToken = default)
    {
        if (!CategorySchemas.TryGetValue(category, out var schema))
            return new ServiceHealthResult { Category = category, DisplayName = category, Status = "Unknown category", ProbeStatus = "unsupported" };
        using var client = _httpClientFactory?.CreateClient("selfhosted-config-probe")
            ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        var result = await ConfigurationProbe.RunAsync(category, _configuration, client, _credential, cancellationToken);
        result.DisplayName = schema.DisplayName;
        return result;
    }

    public async Task<List<ServiceHealthResult>> TestAllConnectionsAsync()
    {
        var results = new List<ServiceHealthResult>();
        foreach (var category in CategorySchemas.Keys)
        {
            results.Add(await TestConnectionAsync(category));
        }
        return results;
    }

    // Configuration defaults and secrets remain with their original authority. Only explicit
    // admin changes create overrides; legacy system-seed rows are ignored on load/read.
    public Task SeedFromConfigurationAsync(IConfiguration configuration) => Task.CompletedTask;

    public DeploymentStatusDto GetDeploymentStatus()
    {
        var reasons = new List<string>();
        var errors = new Dictionary<string, string>();
        foreach (var entry in _db.SystemConfigurations.AsNoTracking().ToList()
            .Where(e => e.LastModifiedBy != "system-seed" && !IsSuperseded(e) && !ConfigurationAuthority.IsExternallyManaged($"{e.Category}:{e.Key}") && e.EncryptedValue is not null))
        {
            var key = $"{entry.Category}:{entry.Key}";
            var requested = TryReadOverride(entry);
            if (requested is null) errors[key] = UnappliedOverrideMessage;
            if (requested is null || !string.Equals(requested, _configuration[key] ?? "", StringComparison.Ordinal)) reasons.Add(key);
        }

        return new DeploymentStatusDto
        {
            Mode = "Direct",
            ActiveProvider = _running.ActiveProvider,
            Capabilities = _running.ActiveProvider == "offline" ? ["capture", "text-search"] : ["capture", "text-search", "ask", "embeddings"],
            Version = typeof(ConfigurationManagementService).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            StartupTime = _startupTime,
            RestartRequired = reasons.Count > 0,
            RestartReasons = reasons,
            ConfigurationErrors = errors
        };
    }

    // --- Private helpers ---

    private bool IsSuperseded(SystemConfiguration entry) => AiRuntimePolicy.IsSuperseded(
        entry.Category, entry.LastModifiedAt, AiRuntimePolicy.ProviderRevision(_configuration));

    private const string UnappliedOverrideMessage = "Saved override could not be decrypted and is not active. Restore the original protection keys, or re-enter this setting and save, then restart.";
    private string? TryReadOverride(SystemConfiguration entry)
    {
        try { return _dataProtector.Unprotect(entry.EncryptedValue!); }
        catch (CryptographicException) { return null; }
    }

    private ConfigCategoryDto BuildCategoryDto(string categoryName, CategorySchema schema, List<SystemConfiguration> dbEntries)
    {
        var categoryRequiresRestart = schema.Keys.Values.Any(k => k.RequiresRestart);
        var kvEnabled = _configuration.GetValue<bool>("AzureKeyVault:Enabled");
        var kvUri = _configuration["AzureKeyVault:VaultUri"];
        var isKeyVaultConfigured = kvEnabled && !string.IsNullOrWhiteSpace(kvUri);

        var entries = schema.Keys.Select(kvp =>
        {
            var dbEntry = dbEntries.FirstOrDefault(e => e.Category == categoryName && e.Key == kvp.Key && !IsSuperseded(e));
            string? decryptedValue = null;
            string? applicationError = null;
            var externallyManaged = ConfigurationAuthority.IsExternallyManaged($"{categoryName}:{kvp.Key}");
            if (!externallyManaged && dbEntry?.LastModifiedBy != "system-seed" && dbEntry?.EncryptedValue is not null)
            {
                decryptedValue = TryReadOverride(dbEntry);
                if (decryptedValue is null) applicationError = UnappliedOverrideMessage;
            }

            var configKey = $"{categoryName}:{kvp.Key}";
            var effectiveValue = _configuration[configKey];
            var source = decryptedValue is not null ? "database" : _running.Authority(configKey);
            var displayedValue = decryptedValue ?? effectiveValue;

            return new ConfigEntryDto
            {
                Key = kvp.Key,
                Value = MaskValue(displayedValue, kvp.Value.IsSecret),
                IsSecret = kvp.Value.IsSecret,
                RequiresRestart = !kvp.Value.IsSecret || kvp.Value.RequiresRestart,
                Description = kvp.Value.Description,
                IsSet = !string.IsNullOrEmpty(displayedValue),
                Editable = !externallyManaged && !(AiRuntimePolicy.IsDisabled(_configuration) && AiRuntimePolicy.IsAiCategory(categoryName)),
                Authority = source,
                EffectiveIsSet = !string.IsNullOrEmpty(effectiveValue),
                PendingRestart = applicationError is not null || decryptedValue is not null && !string.Equals(decryptedValue, effectiveValue ?? "", StringComparison.Ordinal),
                ApplicationError = applicationError,
                LastModifiedAt = dbEntry?.LastModifiedAt,
                LastModifiedBy = dbEntry?.LastModifiedBy,
                Source = source
            };
        }).ToList();

        return new ConfigCategoryDto
        {
            Category = categoryName,
            DisplayName = schema.DisplayName,
            Description = AiRuntimePolicy.IsDisabled(_configuration) && AiRuntimePolicy.IsAiCategory(categoryName) ? AiRuntimePolicy.Guidance : schema.Description,
            RequiresRestart = categoryRequiresRestart,
            Entries = entries,
            ProbeKind = ConfigurationProbe.Kind(categoryName),
            ConfigurationStatus = AiRuntimePolicy.IsDisabled(_configuration) && AiRuntimePolicy.IsAiCategory(categoryName) ? "disabled"
                : _running.ActiveProvider == categoryName ? "active"
                : entries.Any(e => e.EffectiveIsSet) ? "incomplete" : "unconfigured"
        };
    }

    internal static string? MaskValue(string? value, bool isSecret)
    {
        if (!isSecret)
            return value;

        if (string.IsNullOrEmpty(value))
            return null;

        if (value.Length <= 4)
            return "****";

        return "****" + value[^4..];
    }

    private static string? ValidateValue(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (key is "Endpoint" or "BaseUrl" && (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)))
            return "Use an absolute HTTP or HTTPS URL without credentials or fragment.";
        if (key == "Enabled" && !bool.TryParse(value, out _)) return "Use true or false.";
        if (key == "JwtExpirationMinutes" && (!int.TryParse(value, out var minutes) || minutes < 1)) return "Use a positive number of minutes.";
        return null;
    }

    internal static bool IsKeepExistingSentinel(string? value)
    {
        return value is not null && value.Length > 0 && value.All(c => c == '*');
    }

    internal class CategorySchema
    {
        public string DisplayName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public Dictionary<string, KeySchema> Keys { get; set; } = new();
    }
    internal class KeySchema
    {
        public bool IsSecret { get; set; }
        public bool RequiresRestart { get; set; }
        public string Description { get; set; } = string.Empty;
    }

    internal static readonly Dictionary<string, CategorySchema> CategorySchemas = new()
    {
        ["ConnectionStrings"] = new CategorySchema
        {
            DisplayName = "Database",
            Description = "PostgreSQL connection configuration",
            Keys = new Dictionary<string, KeySchema>
            {
                ["McpDb"] = new() { IsSecret = true, RequiresRestart = true, Description = "PostgreSQL connection string" }
            }
        },
        ["OpenAiCompatible"] = new CategorySchema
        {
            DisplayName = "OpenAI-compatible",
            Description = "OpenAI, Ollama, LM Studio or another compatible endpoint",
            Keys = new Dictionary<string, KeySchema>
            {
                ["Endpoint"] = new() { RequiresRestart = true, Description = "API base URL including /v1" },
                ["ChatModel"] = new() { RequiresRestart = true, Description = "Chat model name" },
                ["EmbeddingModel"] = new() { RequiresRestart = true, Description = "Embedding model name" },
                ["ApiKey"] = new() { IsSecret = true, RequiresRestart = true, Description = "Externally managed API key; optional for local providers" }
            }
        },
        ["AzureOpenAI"] = new CategorySchema
        {
            DisplayName = "Azure OpenAI",
            Description = "AI chat and embedding service configuration",
            Keys = new Dictionary<string, KeySchema>
            {
                ["Endpoint"] = new() { IsSecret = false, RequiresRestart = true, Description = "Azure OpenAI endpoint URL" },
                ["ApiKey"] = new() { IsSecret = true, RequiresRestart = true, Description = "Azure OpenAI API key" },
                ["DeploymentName"] = new() { IsSecret = false, RequiresRestart = true, Description = "Chat model deployment name (e.g., gpt-4o)" },
                ["EmbeddingDeploymentName"] = new() { IsSecret = false, RequiresRestart = true, Description = "Embedding model deployment name (e.g., text-embedding-3-small)" }
            }
        },
        ["AzureAIVision"] = new CategorySchema
        {
            DisplayName = "Azure AI Vision",
            Description = "Image, diagram, OCR, and object detection configuration for self-hosted attachment intelligence",
            Keys = new Dictionary<string, KeySchema>
            {
                ["Endpoint"] = new() { IsSecret = false, RequiresRestart = true, Description = "Azure AI Vision endpoint URL" },
                ["ApiKey"] = new() { IsSecret = true, RequiresRestart = true, Description = "Azure AI Vision API key" }
            }
        },
        ["AzureDocumentIntelligence"] = new CategorySchema
        {
            DisplayName = "Azure Document Intelligence",
            Description = "PDF and document extraction configuration for self-hosted attachment intelligence",
            Keys = new Dictionary<string, KeySchema>
            {
                ["Endpoint"] = new() { IsSecret = false, RequiresRestart = true, Description = "Azure Document Intelligence endpoint URL" },
                ["ApiKey"] = new() { IsSecret = true, RequiresRestart = true, Description = "Azure Document Intelligence API key" }
            }
        },
        ["AzureAISearch"] = new CategorySchema
        {
            DisplayName = "Azure AI Search",
            Description = "Vector search and knowledge indexing configuration",
            Keys = new Dictionary<string, KeySchema>
            {
                ["Endpoint"] = new() { IsSecret = false, RequiresRestart = true, Description = "Azure AI Search endpoint URL" },
                ["ApiKey"] = new() { IsSecret = true, RequiresRestart = true, Description = "Azure AI Search API key" },
                ["IndexName"] = new() { IsSecret = false, RequiresRestart = true, Description = "Search index name (e.g., knowledge)" }
            }
        },
        ["Storage"] = new CategorySchema
        {
            DisplayName = "File Storage",
            Description = "File upload and storage configuration",
            Keys = new Dictionary<string, KeySchema>
            {
                ["Provider"] = new() { IsSecret = false, RequiresRestart = true, Description = "Storage provider: AzureBlob or LocalFileSystem" },
                ["Azure:ConnectionString"] = new() { IsSecret = true, RequiresRestart = true, Description = "Azure Blob Storage connection string" },
                ["Azure:ContainerName"] = new() { IsSecret = false, RequiresRestart = true, Description = "Azure Blob container name" },
                ["Local:RootPath"] = new() { IsSecret = false, RequiresRestart = true, Description = "Local file storage root directory path" }
            }
        },
        ["SelfHosted"] = new CategorySchema
        {
            DisplayName = "Authentication & Application",
            Description = "JWT authentication, API keys, and application settings",
            Keys = new Dictionary<string, KeySchema>
            {
                ["JwtSecret"] = new() { IsSecret = true, RequiresRestart = false, Description = "JWT signing secret (min 32 characters)" },
                ["JwtExpirationMinutes"] = new() { IsSecret = false, RequiresRestart = false, Description = "JWT token expiration in minutes" },
                ["JwtIssuer"] = new() { IsSecret = false, RequiresRestart = false, Description = "JWT issuer claim value" },
                ["ApiKey"] = new() { IsSecret = true, RequiresRestart = false, Description = "Legacy global API key (optional)" },
                ["EnableSwagger"] = new() { IsSecret = false, RequiresRestart = true, Description = "Enable Swagger UI" },
                ["ServerName"] = new() { IsSecret = false, RequiresRestart = false, Description = "MCP server name" },
                ["AllowedOrigins"] = new() { IsSecret = false, RequiresRestart = true, Description = "CORS allowed origins (comma-separated)" }
            }
        },
        // Note: Logging is excluded from DB config because DatabaseConfigurationProvider.Load()
        // runs before Data Protection is initialized, causing encrypted log levels to crash the logger.
        ["AzureKeyVault"] = new CategorySchema
        {
            DisplayName = "Azure Key Vault",
            Description = "Optional enterprise secret store. Secrets stored here provide defaults; values set via this admin UI take precedence.",
            Keys = new Dictionary<string, KeySchema>
            {
                ["VaultUri"] = new() { IsSecret = false, RequiresRestart = true, Description = "Key Vault URI (e.g., https://my-vault.vault.azure.net/)" },
                ["Enabled"] = new() { IsSecret = false, RequiresRestart = true, Description = "Enable Key Vault integration (true/false)" }
            }
        },
        ["KnowzPlatform"] = new CategorySchema
        {
            DisplayName = "Knowz Platform",
            Description = "Connect to Knowz platform for AI services (alternative to Azure OpenAI + Azure AI Search)",
            Keys = new Dictionary<string, KeySchema>
            {
                ["Enabled"] = new() { IsSecret = false, RequiresRestart = true, Description = "Enable platform AI proxy (true/false)" },
                ["BaseUrl"] = new() { IsSecret = false, RequiresRestart = true, Description = "Platform API base URL (e.g., https://api.knowz.io)" },
                ["ApiKey"] = new() { IsSecret = true, RequiresRestart = true, Description = "Platform API key (starts with ukz_)" }
            }
        },
        ["Inbox"] = new CategorySchema
        {
            DisplayName = "Inbox",
            Description = "Inbox visibility and behavior settings",
            Keys = new Dictionary<string, KeySchema>
            {
                ["VisibilityScope"] = new()
                {
                    IsSecret = false,
                    RequiresRestart = false,
                    Description = "Inbox visibility scope: Shared (all users see all items) or PerUser (users see only their own items)"
                }
            }
        },
        ["SSO"] = new CategorySchema
        {
            DisplayName = "Single Sign-On (SSO)",
            Description = "Microsoft and Google SSO configuration for passwordless login",
            Keys = new Dictionary<string, KeySchema>
            {
                ["Enabled"] = new()
                {
                    IsSecret = false, RequiresRestart = false,
                    Description = "Enable SSO login buttons (true/false)"
                },
                ["AutoProvisionUsers"] = new()
                {
                    IsSecret = false, RequiresRestart = false,
                    Description = "Automatically create accounts for new SSO users (true/false)"
                },
                ["DefaultRole"] = new()
                {
                    IsSecret = false, RequiresRestart = false,
                    Description = "Default role for auto-provisioned SSO users (User, Admin, SuperAdmin)"
                },
                ["Microsoft:ClientId"] = new()
                {
                    IsSecret = false, RequiresRestart = false,
                    Description = "Microsoft OAuth App Client ID (from Azure Portal)"
                },
                ["Microsoft:ClientSecret"] = new()
                {
                    IsSecret = true, RequiresRestart = false,
                    Description = "Microsoft OAuth App Client Secret"
                },
                ["Microsoft:DirectoryTenantId"] = new()
                {
                    IsSecret = false, RequiresRestart = false,
                    Description = "Entra Directory Tenant ID(s). Single GUID for one org, or comma-separated GUIDs for multi-org (e.g., 'guid1,guid2'). Required for PKCE mode."
                },
                ["Google:ClientId"] = new()
                {
                    IsSecret = false, RequiresRestart = false,
                    Description = "Google OAuth Client ID (from Google Cloud Console)"
                },
                ["Google:ClientSecret"] = new()
                {
                    IsSecret = true, RequiresRestart = false,
                    Description = "Google OAuth Client Secret"
                },
            }
        }
    };
}
