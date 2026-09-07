using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Knowz.MCP.Services;

/// <summary>
/// MCP SSO callback handler.
///
/// <para>
/// <b>Single-replica constraint (review S1):</b> the OIDC state store
/// (<c>_ssoStateStore</c>) and OIDC discovery cache (<c>_oidcConfigCache</c>) are
/// process-static <see cref="ConcurrentDictionary{TKey,TValue}"/>s. SSO callbacks
/// will FAIL if the MCP container scales to more than one replica because the
/// callback may land on a different pod than the one that issued the state token.
/// </para>
/// <para>
/// MCP must run with <c>maxReplicas: 1</c> until the state store is migrated to
/// the existing Redis backplane (see <c>selfhosted/src/Knowz.MCP/CLAUDE.md</c>
/// "Real-time backplane / session store" and the BACKLOG entry
/// <c>REFACTOR_McpSsoStateRedisBackplane</c>).
/// </para>
/// </summary>
public class McpSSOService : IMcpSSOService
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<McpSSOService> _logger;
    private readonly bool _isSelfHosted;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // In-memory SSO state store (keyed by OIDC state parameter).
    // SINGLE-REPLICA ONLY — see class-level remarks. Callback on a different
    // replica than the start request will return "Invalid or expired SSO state".
    private static readonly ConcurrentDictionary<string, (McpSSOState Data, DateTime ExpiresAt)>
        _ssoStateStore = new();
    private const int StateExpirationMinutes = 10;

    // OIDC discovery cache (keyed by authority)
    private static readonly ConcurrentDictionary<string, (OpenIdConnectConfiguration Config, DateTime FetchedAt)>
        _oidcConfigCache = new();
    private const int OidcCacheTtlMinutes = 60;

    // Self-hosted SSO config cache (fetched from self-hosted API)
    private static (List<SelfHostedSSOProviderConfig> Providers, DateTime FetchedAt)? _selfHostedSSOConfigCache;
    private const int SelfHostedConfigCacheTtlMinutes = 5;

    // Platform MCP auth config cache (fetched from platform API)
    private (PlatformMcpSsoConfig Config, DateTime FetchedAt)? _platformMcpSsoConfigCache;
    private const int PlatformConfigCacheTtlSeconds = 60;

    public McpSSOService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<McpSSOService> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _isSelfHosted = (_configuration["MCP:BackendMode"] ?? "proxy")
            .Equals("selfhosted", StringComparison.OrdinalIgnoreCase);
    }

    public List<McpSSOProvider> GetEnabledProviders()
    {
        return GetAuthPageOptionsAsync().GetAwaiter().GetResult().SsoProviders;
    }

    public async Task<McpAuthPageOptions> GetAuthPageOptionsAsync(CancellationToken ct = default)
    {
        if (_isSelfHosted)
        {
            return new McpAuthPageOptions
            {
                ShowPasswordLogin = true,
                ShowApiKeyLogin = true,
                SsoProviders = GetSelfHostedProviders()
            };
        }

        var platformConfig = await FetchPlatformMcpSsoConfigAsync(ct);
        if (platformConfig is not null)
            return MapPlatformAuthPageOptions(platformConfig);

        if (IsPlatformMcpConfigEndpointConfigured())
            return PlatformConfigUnavailableOptions();

        return BuildLocalPlatformAuthPageOptions();
    }

    private McpAuthPageOptions BuildLocalPlatformAuthPageOptions()
    {
        var isEnabled = _configuration.GetValue<bool>("PlatformSSO:Enabled");
        var isForced = _configuration.GetValue<bool>("PlatformSSO:Forced") ||
            _configuration.GetValue<bool>("PlatformSSO__Forced");
        if (!isEnabled && !isForced)
        {
            return new McpAuthPageOptions
            {
                ShowPasswordLogin = true,
                ShowApiKeyLogin = true
            };
        }

        var providers = new List<McpSSOProvider>();

        if (!string.IsNullOrEmpty(_configuration["PlatformSSO:Microsoft:ClientId"]))
        {
            providers.Add(new McpSSOProvider
            {
                Provider = "Microsoft",
                DisplayName = isForced
                    ? BuildForcedDisplayName()
                    : "Sign in with Microsoft",
                IsForcedDefault = isForced
            });
        }

        if (!isForced && !string.IsNullOrEmpty(_configuration["PlatformSSO:Google:ClientId"]))
            providers.Add(new McpSSOProvider { Provider = "Google", DisplayName = "Sign in with Google" });

        return new McpAuthPageOptions
        {
            ForcedSso = isForced,
            ShowPasswordLogin = !isForced,
            ShowApiKeyLogin = true,
            ConfigurationStatus = isForced && providers.Count == 0 ? "misconfigured" : isForced ? "available" : "disabled",
            OrganizationName = _configuration["PlatformSSO:OrganizationName"] ?? _configuration["PlatformSSO__OrganizationName"],
            SsoProviders = providers
        };
    }

    private static McpAuthPageOptions MapPlatformAuthPageOptions(PlatformMcpSsoConfig config)
    {
        return new McpAuthPageOptions
        {
            ForcedSso = config.Forced,
            ShowPasswordLogin = config.ShowPasswordLogin,
            ShowApiKeyLogin = config.ShowApiKeyLogin,
            ConfigurationStatus = config.ConfigurationStatus,
            ErrorCode = config.ErrorCode,
            OrganizationName = config.OrganizationName,
            SsoProviders = config.Providers.Select(p => new McpSSOProvider
            {
                Provider = p.Provider,
                DisplayName = p.DisplayName,
                IsForcedDefault = p.IsForcedDefault
            }).ToList()
        };
    }

    private static McpAuthPageOptions PlatformConfigUnavailableOptions()
    {
        return new McpAuthPageOptions
        {
            ForcedSso = true,
            ShowPasswordLogin = false,
            ShowApiKeyLogin = true,
            ConfigurationStatus = "unavailable",
            ErrorCode = "PLATFORM_SSO_CONFIG_UNAVAILABLE"
        };
    }

    private string BuildForcedDisplayName()
    {
        var orgName = _configuration["PlatformSSO:OrganizationName"] ?? _configuration["PlatformSSO__OrganizationName"];
        return string.IsNullOrWhiteSpace(orgName)
            ? "Sign in with your organization"
            : $"Sign in to {orgName}";
    }

    public async Task<McpSSOStartResult> StartSSOFlowAsync(string provider, string requestId, string callbackUrl)
    {
        var providerConfig = await ResolveProviderConfigAsync(provider);
        if (providerConfig is null ||
            string.IsNullOrEmpty(providerConfig.ClientId) ||
            string.IsNullOrEmpty(providerConfig.Authority))
        {
            return new McpSSOStartResult { Success = false, ErrorMessage = $"SSO not configured for {provider}" };
        }

        // Generate PKCE
        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = GenerateCodeChallenge(codeVerifier);
        var state = GenerateSecureRandomString(32);
        var nonce = GenerateSecureRandomString(32);

        // Store state linking OIDC state to MCP requestId
        CleanupExpiredStates();
        _ssoStateStore[state] = (new McpSSOState
        {
            Provider = provider,
            RequestId = requestId,
            CodeVerifier = codeVerifier,
            Nonce = nonce,
            CallbackUrl = callbackUrl,
        }, DateTime.UtcNow.AddMinutes(StateExpirationMinutes));

        // Fetch OIDC discovery
        var oidcConfig = await GetOidcConfigurationAsync(providerConfig.Authority);

        var authUrl = $"{oidcConfig.AuthorizationEndpoint}" +
            $"?client_id={Uri.EscapeDataString(providerConfig.ClientId)}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(callbackUrl)}" +
            $"&scope={Uri.EscapeDataString("openid email profile")}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&nonce={Uri.EscapeDataString(nonce)}" +
            $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
            $"&code_challenge_method=S256" +
            $"&response_mode=query";

        return new McpSSOStartResult { Success = true, AuthorizationUrl = authUrl };
    }

    public async Task<McpSSOCallbackResult> HandleSSOCallbackAsync(string code, string state)
    {
        // 1. Retrieve and remove state (single-use)
        if (!_ssoStateStore.TryRemove(state, out var stateEntry) || stateEntry.ExpiresAt < DateTime.UtcNow)
            return new McpSSOCallbackResult { Success = false, ErrorMessage = "Invalid or expired SSO state" };

        var ssoState = stateEntry.Data;
        var providerConfig = await ResolveProviderConfigAsync(ssoState.Provider);

        if (providerConfig is null ||
            string.IsNullOrEmpty(providerConfig.ClientId) ||
            string.IsNullOrEmpty(providerConfig.Authority) ||
            (providerConfig.RequiresClientSecret && string.IsNullOrEmpty(providerConfig.ClientSecret)))
        {
            ClearPlatformMcpSsoConfigCache();
            return new McpSSOCallbackResult { Success = false, ErrorMessage = "SSO configuration incomplete" };
        }

        // 2. Exchange authorization code for tokens
        var oidcConfig = await GetOidcConfigurationAsync(providerConfig.Authority);
        var httpClient = _httpClientFactory.CreateClient();

        var tokenRequestBody = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = ssoState.CallbackUrl,
            ["client_id"] = providerConfig.ClientId,
            ["code_verifier"] = ssoState.CodeVerifier,
        };

        if (providerConfig.RequiresClientSecret && !string.IsNullOrEmpty(providerConfig.ClientSecret))
            tokenRequestBody["client_secret"] = providerConfig.ClientSecret;

        var tokenResponse = await httpClient.PostAsync(
            oidcConfig.TokenEndpoint,
            new FormUrlEncodedContent(tokenRequestBody));

        if (!tokenResponse.IsSuccessStatusCode)
        {
            var error = await tokenResponse.Content.ReadAsStringAsync();
            _logger.LogWarning("MCP SSO token exchange failed for provider {Provider}: {Error}",
                ssoState.Provider, error);
            ClearPlatformMcpSsoConfigCache();
            return new McpSSOCallbackResult { Success = false, ErrorMessage = "Authentication failed" };
        }

        var tokenJson = await tokenResponse.Content.ReadAsStringAsync();
        var tokenDoc = JsonDocument.Parse(tokenJson);

        if (!tokenDoc.RootElement.TryGetProperty("id_token", out var idTokenEl))
        {
            ClearPlatformMcpSsoConfigCache();
            return new McpSSOCallbackResult { Success = false, ErrorMessage = "No ID token returned" };
        }

        // 3. Validate ID token and extract email
        var email = await ValidateAndExtractEmailAsync(
            idTokenEl.GetString()!, providerConfig, ssoState.Nonce);

        if (string.IsNullOrEmpty(email))
            return new McpSSOCallbackResult { Success = false, ErrorMessage = "No email found in ID token" };

        // 4. Resolve email to API key via platform internal endpoint
        var apiKey = await ResolveEmailToApiKeyAsync(email, ssoState.Provider);

        if (string.IsNullOrEmpty(apiKey))
            return new McpSSOCallbackResult
            {
                Success = false,
                ErrorMessage = "No Knowz account found for this email. Please register first."
            };

        _logger.LogInformation("MCP SSO completed for email {Email} via {Provider}", email, ssoState.Provider);

        return new McpSSOCallbackResult
        {
            Success = true,
            RequestId = ssoState.RequestId,
            ApiKey = apiKey,
            Email = email,
        };
    }

    // ==================== Private Helpers ====================

    private async Task<PlatformMcpSsoProviderConfig?> ResolveProviderConfigAsync(string provider)
    {
        if (_isSelfHosted)
        {
            var selfHostedConfig = GetCachedSelfHostedConfig(provider);
            return selfHostedConfig is null
                ? null
                : new PlatformMcpSsoProviderConfig
                {
                    Provider = selfHostedConfig.Provider,
                    DisplayName = selfHostedConfig.DisplayName,
                    ClientId = selfHostedConfig.ClientId,
                    ClientSecret = selfHostedConfig.ClientSecret,
                    RequiresClientSecret = selfHostedConfig.RequiresClientSecret,
                    Authority = selfHostedConfig.Authority
                };
        }

        var platformConfig = await FetchPlatformMcpSsoConfigAsync();
        var platformProvider = platformConfig?.Providers.FirstOrDefault(p =>
            p.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase));
        if (platformProvider is not null)
            return platformProvider;

        if (IsPlatformMcpConfigEndpointConfigured())
            return null;

        return BuildLocalProviderConfig(provider);
    }

    private PlatformMcpSsoProviderConfig? BuildLocalProviderConfig(string provider)
    {
        return provider.ToLowerInvariant() switch
        {
            "microsoft" => new PlatformMcpSsoProviderConfig
            {
                Provider = "Microsoft",
                DisplayName = "Sign in with Microsoft",
                ClientId = _configuration["PlatformSSO:Microsoft:ClientId"] ?? string.Empty,
                ClientSecret = _configuration["PlatformSSO:Microsoft:ClientSecret"],
                RequiresClientSecret = true,
                Authority = "https://login.microsoftonline.com/common/v2.0"
            },
            "google" => new PlatformMcpSsoProviderConfig
            {
                Provider = "Google",
                DisplayName = "Sign in with Google",
                ClientId = _configuration["PlatformSSO:Google:ClientId"] ?? string.Empty,
                ClientSecret = _configuration["PlatformSSO:Google:ClientSecret"],
                RequiresClientSecret = true,
                Authority = "https://accounts.google.com"
            },
            _ => null
        };
    }

    private async Task<OpenIdConnectConfiguration> GetOidcConfigurationAsync(string authority)
    {
        if (_oidcConfigCache.TryGetValue(authority, out var cached) &&
            (DateTime.UtcNow - cached.FetchedAt).TotalMinutes < OidcCacheTtlMinutes)
        {
            return cached.Config;
        }

        var metadataAddress = authority.TrimEnd('/') + "/.well-known/openid-configuration";
        var configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(_httpClientFactory.CreateClient()));

        var config = await configManager.GetConfigurationAsync(CancellationToken.None);
        _oidcConfigCache[authority] = (config, DateTime.UtcNow);

        return config;
    }

    private async Task<string?> ValidateAndExtractEmailAsync(
        string idToken, PlatformMcpSsoProviderConfig providerConfig, string expectedNonce)
    {
        try
        {
            var oidcConfig = await GetOidcConfigurationAsync(providerConfig.Authority);

            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidAudience = providerConfig.ClientId,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = oidcConfig.SigningKeys,
                ClockSkew = TimeSpan.FromMinutes(5),
            };

            // Microsoft "common" endpoint uses tenant-specific issuers
            if (IsMicrosoftProvider(providerConfig.Provider))
            {
                validationParameters.IssuerValidator = (issuer, token, parameters) =>
                {
                    if (issuer.StartsWith("https://login.microsoftonline.com/") &&
                        issuer.EndsWith("/v2.0"))
                        return issuer;
                    throw new SecurityTokenInvalidIssuerException($"Invalid issuer: {issuer}");
                };
            }
            else
            {
                validationParameters.ValidIssuer = providerConfig.Authority;
            }

            var handler = new JwtSecurityTokenHandler();
            handler.ValidateToken(idToken, validationParameters, out var validatedToken);

            var jwt = (JwtSecurityToken)validatedToken;

            // Validate nonce
            var tokenNonce = jwt.Claims.FirstOrDefault(c => c.Type == "nonce")?.Value;
            if (tokenNonce != expectedNonce)
            {
                _logger.LogWarning("MCP SSO nonce mismatch for provider {Provider}", providerConfig.Provider);
                return null;
            }

            if (IsMicrosoftProvider(providerConfig.Provider) &&
                !ValidateMicrosoftTenantAndRestrictions(jwt, providerConfig))
            {
                return null;
            }

            // Extract email from claims (try multiple claim types)
            var email = jwt.Claims.FirstOrDefault(c => c.Type == "email")?.Value
                ?? jwt.Claims.FirstOrDefault(c => c.Type == "preferred_username")?.Value
                ?? jwt.Claims.FirstOrDefault(c => c.Type == "upn")?.Value;

            return email;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP SSO ID token validation failed for provider {Provider}", providerConfig.Provider);
            return null;
        }
    }

    private bool ValidateMicrosoftTenantAndRestrictions(
        JwtSecurityToken jwt,
        PlatformMcpSsoProviderConfig providerConfig)
    {
        if (providerConfig.AllowedTenantIds.Count > 0)
        {
            var tid = jwt.Claims.FirstOrDefault(c => c.Type == "tid")?.Value;
            if (string.IsNullOrWhiteSpace(tid) ||
                !providerConfig.AllowedTenantIds.Contains(tid, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "MCP SSO rejected Microsoft token because tenant id {TenantId} is not allowed",
                    tid ?? "<missing>");
                return false;
            }
        }

        if (providerConfig.RequiredGroupIds.Count > 0)
        {
            if (HasMicrosoftGroupsOverage(jwt))
            {
                _logger.LogWarning("MCP SSO rejected Microsoft token because groups overage is unsupported");
                return false;
            }

            var tokenGroups = jwt.Claims
                .Where(c => c.Type == "groups")
                .Select(c => c.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!tokenGroups.Overlaps(providerConfig.RequiredGroupIds))
            {
                _logger.LogWarning("MCP SSO rejected Microsoft token because required groups are missing");
                return false;
            }
        }

        if (providerConfig.RequiredAppRoleNames.Count > 0)
        {
            var tokenRoles = jwt.Claims
                .Where(c => c.Type is "roles" or "role" ||
                    c.Type == "http://schemas.microsoft.com/ws/2008/06/identity/claims/role")
                .Select(c => c.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!tokenRoles.Overlaps(providerConfig.RequiredAppRoleNames))
            {
                _logger.LogWarning("MCP SSO rejected Microsoft token because required app roles are missing");
                return false;
            }
        }

        return true;
    }

    private static bool HasMicrosoftGroupsOverage(JwtSecurityToken jwt)
    {
        if (jwt.Claims.Any(c => c.Type == "hasgroups"))
            return true;

        return jwt.Payload.TryGetValue("_claim_names", out var claimNames) &&
            claimNames?.ToString()?.Contains("groups", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsMicrosoftProvider(string provider)
        => provider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("MicrosoftEntraId", StringComparison.OrdinalIgnoreCase);

    private async Task<string?> ResolveEmailToApiKeyAsync(string email, string provider)
    {
        var platformUrl = _configuration["Knowz:BaseUrl"]
            ?? throw new InvalidOperationException("Knowz:BaseUrl is not configured");
        var serviceKey = _configuration["MCP:ServiceKey"];

        if (string.IsNullOrEmpty(serviceKey))
        {
            _logger.LogError("MCP:ServiceKey not configured -- cannot resolve SSO email to API key");
            return null;
        }

        var httpClient = _httpClientFactory.CreateClient("McpApiClient");
        var request = new HttpRequestMessage(HttpMethod.Post, $"{platformUrl}/api/v1/internal/sso/resolve")
        {
            Content = JsonContent.Create(new { email, provider })
        };
        request.Headers.Add("X-Service-Key", serviceKey);

        try
        {
            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Platform SSO resolve failed: {Status} {Error}",
                    response.StatusCode, errorBody);
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (body.TryGetProperty("data", out var data) &&
                data.TryGetProperty("apiKey", out var apiKeyEl))
            {
                return apiKeyEl.GetString();
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve SSO email to API key via platform API");
            return null;
        }
    }

    private async Task<PlatformMcpSsoConfig?> FetchPlatformMcpSsoConfigAsync(CancellationToken ct = default)
    {
        if (_isSelfHosted || !IsPlatformMcpConfigEndpointConfigured())
            return null;

        if (_platformMcpSsoConfigCache.HasValue &&
            (DateTime.UtcNow - _platformMcpSsoConfigCache.Value.FetchedAt).TotalSeconds < PlatformConfigCacheTtlSeconds)
        {
            return _platformMcpSsoConfigCache.Value.Config;
        }

        var platformUrl = _configuration["Knowz:BaseUrl"]!.TrimEnd('/');
        var serviceKey = _configuration["MCP:ServiceKey"]!;

        try
        {
            var httpClient = _httpClientFactory.CreateClient("McpApiClient");
            var request = new HttpRequestMessage(HttpMethod.Get, $"{platformUrl}/api/v1/internal/sso/mcp-config");
            request.Headers.Add("X-Service-Key", serviceKey);

            var response = await httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Failed to fetch platform MCP SSO config: {StatusCode}",
                    response.StatusCode);
                ClearPlatformMcpSsoConfigCache();
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var envelope = JsonSerializer.Deserialize<PlatformMcpSsoConfigEnvelope>(body, JsonOptions);
            if (envelope?.Success != true || envelope.Data is null)
            {
                _logger.LogWarning("Platform MCP SSO config response was unsuccessful or empty");
                ClearPlatformMcpSsoConfigCache();
                return null;
            }

            _platformMcpSsoConfigCache = (envelope.Data, DateTime.UtcNow);
            return envelope.Data;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch platform MCP SSO config");
            ClearPlatformMcpSsoConfigCache();
            return null;
        }
    }

    private void ClearPlatformMcpSsoConfigCache()
        => _platformMcpSsoConfigCache = null;

    private bool IsPlatformMcpConfigEndpointConfigured()
        => !string.IsNullOrWhiteSpace(_configuration["Knowz:BaseUrl"]) &&
            !string.IsNullOrWhiteSpace(_configuration["MCP:ServiceKey"]);

    // ==================== Self-Hosted SSO Config ====================

    /// <summary>
    /// Gets enabled SSO providers from the self-hosted API's internal config endpoint.
    /// Results are cached for 5 minutes.
    /// </summary>
    private List<McpSSOProvider> GetSelfHostedProviders()
    {
        var configs = FetchSelfHostedSSOConfigAsync().GetAwaiter().GetResult();
        return configs.Select(c => new McpSSOProvider
        {
            Provider = c.Provider,
            DisplayName = c.DisplayName,
        }).ToList();
    }

    private SelfHostedSSOProviderConfig? GetCachedSelfHostedConfig(string provider)
    {
        var configs = FetchSelfHostedSSOConfigAsync().GetAwaiter().GetResult();
        return configs.FirstOrDefault(c =>
            c.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<SelfHostedSSOProviderConfig>> FetchSelfHostedSSOConfigAsync()
    {
        // Return cached if still valid
        if (_selfHostedSSOConfigCache.HasValue &&
            (DateTime.UtcNow - _selfHostedSSOConfigCache.Value.FetchedAt).TotalMinutes < SelfHostedConfigCacheTtlMinutes)
        {
            return _selfHostedSSOConfigCache.Value.Providers;
        }

        var baseUrl = _configuration["Knowz:BaseUrl"]
            ?? throw new InvalidOperationException("Knowz:BaseUrl is not configured");
        var serviceKey = _configuration["MCP:ServiceKey"];

        if (string.IsNullOrEmpty(serviceKey))
        {
            _logger.LogWarning("MCP:ServiceKey not configured -- cannot fetch self-hosted SSO config");
            return new List<SelfHostedSSOProviderConfig>();
        }

        try
        {
            var httpClient = _httpClientFactory.CreateClient("McpApiClient");
            var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/internal/sso/config");
            request.Headers.Add("X-Service-Key", serviceKey);

            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch self-hosted SSO config: {Status}", response.StatusCode);
                return _selfHostedSSOConfigCache?.Providers ?? new List<SelfHostedSSOProviderConfig>();
            }

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var providers = new List<SelfHostedSSOProviderConfig>();

            if (body.TryGetProperty("data", out var data) &&
                data.TryGetProperty("providers", out var providersEl))
            {
                foreach (var p in providersEl.EnumerateArray())
                {
                    providers.Add(new SelfHostedSSOProviderConfig
                    {
                        Provider = p.GetProperty("provider").GetString() ?? "",
                        DisplayName = p.GetProperty("displayName").GetString() ?? "",
                        ClientId = p.GetProperty("clientId").GetString() ?? "",
                        ClientSecret = p.TryGetProperty("clientSecret", out var cs) ? cs.GetString() : null,
                        RequiresClientSecret = !p.TryGetProperty("requiresClientSecret", out var requiresSecret) ||
                            requiresSecret.GetBoolean(),
                        Authority = p.GetProperty("authority").GetString() ?? "",
                    });
                }
            }

            _selfHostedSSOConfigCache = (providers, DateTime.UtcNow);
            return providers;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch self-hosted SSO config");
            return _selfHostedSSOConfigCache?.Providers ?? new List<SelfHostedSSOProviderConfig>();
        }
    }

    private class SelfHostedSSOProviderConfig
    {
        public string Provider { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string? ClientSecret { get; set; }
        public bool RequiresClientSecret { get; set; } = true;
        public string Authority { get; set; } = string.Empty;
    }

    private class PlatformMcpSsoConfigEnvelope
    {
        public bool Success { get; set; }
        public PlatformMcpSsoConfig? Data { get; set; }
    }

    private class PlatformMcpSsoConfig
    {
        public bool Enabled { get; set; }
        public bool Forced { get; set; }
        public bool ShowPasswordLogin { get; set; } = true;
        public bool ShowApiKeyLogin { get; set; } = true;
        public string ConfigurationStatus { get; set; } = "disabled";
        public string? ErrorCode { get; set; }
        public string? OrganizationName { get; set; }
        public List<PlatformMcpSsoProviderConfig> Providers { get; set; } = new();
    }

    private class PlatformMcpSsoProviderConfig
    {
        public string Provider { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string? ClientSecret { get; set; }
        public bool RequiresClientSecret { get; set; } = true;
        public string Authority { get; set; } = string.Empty;
        public bool IsForcedDefault { get; set; }
        public List<string> AllowedTenantIds { get; set; } = new();
        public List<string> RequiredGroupIds { get; set; } = new();
        public List<string> RequiredAppRoleNames { get; set; } = new();
    }

    // ==================== PKCE & Crypto ====================

    private static string GenerateCodeVerifier()
    {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    private static string GenerateCodeChallenge(string codeVerifier)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.ASCII.GetBytes(codeVerifier));
        return Convert.ToBase64String(hash)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    private static string GenerateSecureRandomString(int length)
    {
        var bytes = new byte[length];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=')[..length];
    }

    private static void CleanupExpiredStates()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _ssoStateStore)
        {
            if (kvp.Value.ExpiresAt < now)
                _ssoStateStore.TryRemove(kvp.Key, out _);
        }
    }

    private class McpSSOState
    {
        public string Provider { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
        public string CodeVerifier { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public string CallbackUrl { get; set; } = string.Empty;
    }
}
