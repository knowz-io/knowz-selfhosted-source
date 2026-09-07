using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.SelfHosted.Application.Extensions;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Knowz.SelfHosted.Application.Services;

public class SelfHostedSSOService : ISelfHostedSSOService
{
    private readonly SelfHostedDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SelfHostedSSOService> _logger;
    private readonly SelfHostedOptions _options;

    // In-memory state store (self-hosted does not have Redis)
    internal static readonly ConcurrentDictionary<string, (SSOStateData Data, DateTime ExpiresAt)>
        StateStore = new();
    private const int StateExpirationMinutes = 10;

    // OIDC discovery cache
    private static readonly ConcurrentDictionary<string, (OpenIdConnectConfiguration Config, DateTime FetchedAt)>
        OidcConfigCache = new();
    private const int OidcCacheMinutes = 60;

    public SelfHostedSSOService(
        SelfHostedDbContext db,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<SelfHostedSSOService> logger,
        IOptions<SelfHostedOptions> options)
    {
        _db = db;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _options = options.Value;
    }

    public Task<List<SSOProviderInfo>> GetEnabledProvidersAsync()
    {
        var isEnabled = _configuration.GetValue<bool>("SSO:Enabled");
        if (!isEnabled) return Task.FromResult(new List<SSOProviderInfo>());

        var providers = new List<SSOProviderInfo>();

        var msMode = DetectSSOMode("Microsoft");
        if (msMode != SSOMode.Disabled)
        {
            providers.Add(new SSOProviderInfo
            {
                Provider = "Microsoft",
                DisplayName = "Sign in with Microsoft",
                Mode = msMode.ToString(),
            });
        }

        if (!string.IsNullOrEmpty(_configuration["SSO:Google:ClientId"]))
        {
            providers.Add(new SSOProviderInfo
            {
                Provider = "Google",
                DisplayName = "Sign in with Google",
                Mode = "ConfidentialClient",
            });
        }

        return Task.FromResult(providers);
    }

    public async Task<SSOAuthorizeResult> GenerateAuthorizeUrlAsync(string provider, string redirectUri)
    {
        var isEnabled = _configuration.GetValue<bool>("SSO:Enabled");
        if (!isEnabled)
            return new SSOAuthorizeResult { Success = false, ErrorMessage = "SSO is not enabled" };

        // OIDC redirect_uri must be an absolute URL. A relative path here
        // ends up forwarded to the IdP (rejected as AADSTS50011 by Entra
        // and invalid_request by Google) and then re-sent in the token
        // exchange POST, breaking the entire SSO flow.
        if (!Uri.IsWellFormedUriString(redirectUri, UriKind.Absolute))
            return new SSOAuthorizeResult { Success = false, ErrorMessage = "redirectUri must be an absolute URL" };

        var (clientId, authority) = GetProviderConfig(provider);
        if (string.IsNullOrEmpty(clientId))
            return new SSOAuthorizeResult { Success = false, ErrorMessage = $"SSO not configured for {provider}" };

        // Generate PKCE
        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = GenerateCodeChallenge(codeVerifier);
        var state = GenerateSecureRandomString(32);
        var nonce = GenerateSecureRandomString(32);

        // Store state in memory
        CleanupExpiredStates();
        StateStore[state] = (new SSOStateData
        {
            Provider = provider,
            RedirectUri = redirectUri,
            CodeVerifier = codeVerifier,
            Nonce = nonce,
            CreatedAt = DateTime.UtcNow,
        }, DateTime.UtcNow.AddMinutes(StateExpirationMinutes));

        // Fetch OIDC discovery
        var oidcConfig = await GetOidcConfigurationAsync(authority);

        var authUrl = $"{oidcConfig.AuthorizationEndpoint}" +
            $"?client_id={Uri.EscapeDataString(clientId)}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString("openid email profile")}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&nonce={Uri.EscapeDataString(nonce)}" +
            $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
            $"&code_challenge_method=S256" +
            $"&response_mode=query";

        return new SSOAuthorizeResult
        {
            Success = true,
            AuthorizationUrl = authUrl,
            State = state,
        };
    }

    public async Task<SSOCallbackResult> HandleCallbackAsync(string code, string state)
    {
        // 1. Retrieve and validate state
        if (!StateStore.TryRemove(state, out var stateEntry) || stateEntry.ExpiresAt < DateTime.UtcNow)
            return new SSOCallbackResult { Success = false, ErrorMessage = "Invalid or expired state" };

        var stateData = stateEntry.Data;
        var (clientId, authority) = GetProviderConfig(stateData.Provider);
        var clientSecret = GetClientSecret(stateData.Provider);
        var mode = DetectSSOMode(stateData.Provider);

        if (string.IsNullOrEmpty(clientId))
            return new SSOCallbackResult { Success = false, ErrorMessage = "SSO configuration incomplete" };

        if (mode == SSOMode.Disabled)
            return new SSOCallbackResult { Success = false, ErrorMessage = "SSO configuration incomplete" };

        if (mode == SSOMode.ConfidentialClient && string.IsNullOrEmpty(clientSecret))
            return new SSOCallbackResult { Success = false, ErrorMessage = "SSO configuration incomplete: client secret required for confidential mode" };

        // 2. Exchange code for tokens
        var oidcConfig = await GetOidcConfigurationAsync(authority);

        var tokenRequestBody = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = stateData.RedirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = stateData.CodeVerifier,
        };

        if (mode == SSOMode.ConfidentialClient)
        {
            tokenRequestBody["client_secret"] = clientSecret!;
        }

        var httpClient = _httpClientFactory.CreateClient();
        var tokenResponse = await httpClient.PostAsync(
            oidcConfig.TokenEndpoint,
            new FormUrlEncodedContent(tokenRequestBody));

        if (!tokenResponse.IsSuccessStatusCode)
        {
            var errorBody = await tokenResponse.Content.ReadAsStringAsync();
            _logger.LogWarning("SSO token exchange failed for {Provider}: {Status} - {Error}",
                stateData.Provider, tokenResponse.StatusCode, errorBody);
            return new SSOCallbackResult { Success = false, ErrorMessage = "Token exchange failed" };
        }

        var tokenResult = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>();
        if (string.IsNullOrEmpty(tokenResult?.IdToken))
            return new SSOCallbackResult { Success = false, ErrorMessage = "No ID token received" };

        // 3. Validate ID token
        var claims = await ValidateIdTokenAsync(
            tokenResult.IdToken, stateData.Provider, clientId, authority, stateData.Nonce);

        if (claims == null)
            return new SSOCallbackResult { Success = false, ErrorMessage = "ID token validation failed" };

        // 4. Find or create user
        var (user, wasAutoProvisioned) = await FindOrCreateUserAsync(claims, stateData.Provider);

        if (user == null)
            return new SSOCallbackResult
            {
                Success = false,
                ErrorMessage = "No account found. Registration is disabled for SSO users."
            };

        // 5. Issue self-hosted JWT (same as login)
        var expiresAt = DateTime.UtcNow.AddMinutes(_options.JwtExpirationMinutes);
        var token = GenerateJwtToken(user, expiresAt);

        return new SSOCallbackResult
        {
            Success = true,
            Token = token,
            ExpiresAt = expiresAt,
            Email = user.Email,
            DisplayName = user.DisplayName,
            WasAutoProvisioned = wasAutoProvisioned,
        };
    }

    internal async Task<(User? User, bool WasAutoProvisioned)> FindOrCreateUserAsync(
        ValidatedTokenClaims claims, string provider)
    {
        var normalizedEmail = claims.Email.Trim().ToLowerInvariant();
        var subjectId = claims.SubjectId;

        // 1. Try match by OAuthSubjectId + OAuthProvider
        var user = await _db.Users
            .Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.OAuthProvider == provider
                                   && u.OAuthSubjectId == subjectId
                                   && u.IsActive);

        if (user != null)
        {
            user.LastLoginAt = DateTime.UtcNow;
            user.OAuthEmail = normalizedEmail;
            await _db.SaveChangesAsync();
            return (user, false);
        }

        // 2. Try match by Email
        user = await _db.Users
            .Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Email != null
                                   && u.Email.ToLower() == normalizedEmail
                                   && u.IsActive);

        if (user != null)
        {
            user.OAuthProvider = provider;
            user.OAuthSubjectId = subjectId;
            user.OAuthEmail = normalizedEmail;
            user.LastLoginAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return (user, false);
        }

        // 3. Auto-provision if enabled
        var autoProvision = _configuration.GetValue<bool>("SSO:AutoProvisionUsers");
        if (!autoProvision)
            return (null, false);

        var defaultRoleStr = _configuration["SSO:DefaultRole"] ?? "User";
        if (!Enum.TryParse<UserRole>(defaultRoleStr, true, out var defaultRole))
            defaultRole = UserRole.User;

        var tenant = await _db.Tenants.FirstOrDefaultAsync();
        var tenantId = tenant?.Id ?? _options.TenantId;

        var displayName = claims.DisplayName
            ?? $"{claims.GivenName} {claims.FamilyName}".Trim();
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = normalizedEmail;

        var newUser = new User
        {
            TenantId = tenantId,
            Username = normalizedEmail,
            Email = normalizedEmail,
            DisplayName = displayName,
            PasswordHash = "SSO_ONLY_NO_PASSWORD",
            Role = defaultRole,
            IsActive = true,
            OAuthProvider = provider,
            OAuthSubjectId = subjectId,
            OAuthEmail = normalizedEmail,
            LastLoginAt = DateTime.UtcNow,
        };

        _db.Users.Add(newUser);
        await _db.SaveChangesAsync();

        // Create tenant membership for auto-provisioned user
        var membership = new UserTenantMembership
        {
            UserId = newUser.Id,
            TenantId = tenantId,
            Role = defaultRole,
            IsActive = true,
            JoinedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.UserTenantMemberships.Add(membership);
        await _db.SaveChangesAsync();

        // Reload with tenant navigation
        if (tenant != null)
        {
            newUser.Tenant = tenant;
        }

        _logger.LogInformation("Auto-provisioned SSO user {Email} with role {Role}", normalizedEmail, defaultRole);
        return (newUser, true);
    }

    internal async Task<ValidatedTokenClaims?> ValidateIdTokenAsync(
        string idToken, string provider, string clientId, string authority, string expectedNonce)
    {
        try
        {
            var oidcConfig = await GetOidcConfigurationAsync(authority);

            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidAudience = clientId,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = oidcConfig.SigningKeys,
                ClockSkew = TimeSpan.FromMinutes(5)
            };

            // Microsoft "common" endpoint: issuer varies per tenant
            if (provider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) &&
                authority.Contains("/common/", StringComparison.OrdinalIgnoreCase))
            {
                validationParameters.ValidateIssuer = true;
                validationParameters.IssuerValidator = (issuer, _, _) =>
                {
                    if (Uri.TryCreate(issuer, UriKind.Absolute, out var uri) &&
                        uri.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
                    {
                        return issuer;
                    }
                    throw new SecurityTokenInvalidIssuerException($"Invalid issuer: {issuer}");
                };
            }
            else if (provider.Equals("Google", StringComparison.OrdinalIgnoreCase))
            {
                validationParameters.ValidIssuer = "https://accounts.google.com";
            }
            else
            {
                // Specific Microsoft tenant
                validationParameters.ValidIssuer = oidcConfig.Issuer;
            }

            var tokenHandler = new JwtSecurityTokenHandler();
            var principal = tokenHandler.ValidateToken(idToken, validationParameters, out _);

            // Validate nonce
            var nonceClaim = principal.FindFirst("nonce")?.Value;
            if (nonceClaim != expectedNonce)
            {
                _logger.LogWarning("SSO nonce mismatch for {Provider}", provider);
                return null;
            }

            // Validate tid claim against allowed tenant IDs
            string? tidValue = null;
            if (provider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
            {
                tidValue = principal.FindFirst("tid")?.Value;
                var allowedTenantIds = ParseAllowedTenantIds();

                if (allowedTenantIds.Count > 0 && !string.IsNullOrEmpty(tidValue))
                {
                    if (!Guid.TryParse(tidValue, out var tokenTid) ||
                        !allowedTenantIds.Contains(tokenTid))
                    {
                        _logger.LogWarning("SSO tenant ID {TenantId} not in allowed list for {Provider}",
                            tidValue, provider);
                        return null;
                    }
                }
                else if (DetectSSOMode(provider) == SSOMode.PkcePublicClient && allowedTenantIds.Count == 0)
                {
                    _logger.LogError("PKCE mode requires DirectoryTenantId but none configured");
                    return null;
                }
            }

            // Extract claims
            var email = principal.FindFirst(ClaimTypes.Email)?.Value
                ?? principal.FindFirst("email")?.Value
                ?? principal.FindFirst("preferred_username")?.Value;

            if (string.IsNullOrEmpty(email))
            {
                _logger.LogWarning("SSO ID token has no email claim for {Provider}", provider);
                return null;
            }

            return new ValidatedTokenClaims
            {
                SubjectId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? principal.FindFirst("sub")?.Value ?? "",
                Email = email,
                DisplayName = principal.FindFirst("name")?.Value,
                GivenName = principal.FindFirst(ClaimTypes.GivenName)?.Value
                    ?? principal.FindFirst("given_name")?.Value,
                FamilyName = principal.FindFirst(ClaimTypes.Surname)?.Value
                    ?? principal.FindFirst("family_name")?.Value,
                TenantId = tidValue,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SSO ID token validation failed for {Provider}", provider);
            return null;
        }
    }

    internal (string? ClientId, string Authority) GetProviderConfig(string provider)
    {
        return provider.ToLowerInvariant() switch
        {
            "microsoft" => (
                _configuration["SSO:Microsoft:ClientId"],
                GetMicrosoftAuthority()
            ),
            "google" => (
                _configuration["SSO:Google:ClientId"],
                "https://accounts.google.com"
            ),
            _ => (null, "")
        };
    }

    internal SSOMode DetectSSOMode(string provider)
    {
        if (!provider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
            return SSOMode.Disabled;

        var isEnabled = _configuration.GetValue<bool>("SSO:Enabled");
        if (!isEnabled) return SSOMode.Disabled;

        var clientId = _configuration["SSO:Microsoft:ClientId"];
        if (string.IsNullOrEmpty(clientId)) return SSOMode.Disabled;

        var clientSecret = _configuration["SSO:Microsoft:ClientSecret"];
        if (!string.IsNullOrEmpty(clientSecret))
            return SSOMode.ConfidentialClient;

        var tenantIds = ParseAllowedTenantIds();
        if (tenantIds.Count > 0)
            return SSOMode.PkcePublicClient;

        return SSOMode.Disabled;
    }

    internal List<Guid> ParseAllowedTenantIds()
    {
        var raw = _configuration["SSO:Microsoft:DirectoryTenantId"];
        if (string.IsNullOrWhiteSpace(raw)) return new List<Guid>();

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => Guid.TryParse(s, out _))
            .Select(Guid.Parse)
            .Distinct()
            .ToList();
    }

    private string GetMicrosoftAuthority()
    {
        var tenantIds = ParseAllowedTenantIds();
        if (tenantIds.Count == 1)
            return $"https://login.microsoftonline.com/{tenantIds[0]}/v2.0";
        return "https://login.microsoftonline.com/common/v2.0";
    }

    internal string? GetClientSecret(string provider)
    {
        return provider.ToLowerInvariant() switch
        {
            "microsoft" => _configuration["SSO:Microsoft:ClientSecret"],
            "google" => _configuration["SSO:Google:ClientSecret"],
            _ => null
        };
    }

    private async Task<OpenIdConnectConfiguration> GetOidcConfigurationAsync(string authority)
    {
        var metadataAddress = $"{authority.TrimEnd('/')}/.well-known/openid-configuration";

        if (OidcConfigCache.TryGetValue(metadataAddress, out var cached) &&
            cached.FetchedAt > DateTime.UtcNow.AddMinutes(-OidcCacheMinutes))
        {
            return cached.Config;
        }

        var configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(_httpClientFactory.CreateClient()));

        var config = await configManager.GetConfigurationAsync();
        OidcConfigCache[metadataAddress] = (config, DateTime.UtcNow);

        return config;
    }

    private string GenerateJwtToken(User user, DateTime expiresAt) =>
        JwtTokenHelper.GenerateToken(user, expiresAt, _options.JwtSecret, _options.JwtIssuer, _logger);

    internal static string GenerateCodeVerifier()
    {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    internal static string GenerateCodeChallenge(string codeVerifier)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.ASCII.GetBytes(codeVerifier));
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    internal static string GenerateSecureRandomString(int length)
    {
        var bytes = new byte[length];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_')[..length];
    }

    internal static void CleanupExpiredStates()
    {
        var expired = StateStore
            .Where(kvp => kvp.Value.ExpiresAt < DateTime.UtcNow)
            .Select(kvp => kvp.Key).ToList();
        foreach (var key in expired)
            StateStore.TryRemove(key, out _);
    }

    /// <summary>
    /// Clears the static state store. Used for testing.
    /// </summary>
    internal static void ClearStateStore()
    {
        StateStore.Clear();
    }

    /// <summary>
    /// Clears the OIDC configuration cache. Used for testing.
    /// </summary>
    internal static void ClearOidcCache()
    {
        OidcConfigCache.Clear();
    }

    internal class TokenResponse
    {
        [JsonPropertyName("id_token")]
        public string? IdToken { get; set; }

        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; set; }

        [JsonPropertyName("expires_in")]
        public int? ExpiresIn { get; set; }
    }
}

public class SSOStateData
{
    public string Provider { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public string CodeVerifier { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class ValidatedTokenClaims
{
    public string SubjectId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? GivenName { get; set; }
    public string? FamilyName { get; set; }
    public string? TenantId { get; set; }
}

public enum SSOMode
{
    Disabled,
    PkcePublicClient,
    ConfidentialClient
}
