using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Knowz.Core.Configuration;
using Knowz.SelfHosted.Application.Extensions;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Knowz.SelfHosted.API.Middleware;

/// <summary>
/// Dual authentication middleware supporting JWT Bearer tokens, per-user API keys (ksh_ prefix),
/// and legacy single API key for backward compatibility.
/// </summary>
public class AuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SelfHostedOptions _options;
    private readonly ILogger<AuthenticationMiddleware> _logger;

    private static readonly string[] PublicPathPrefixes = ["/healthz", "/swagger", "/index.html", "/api/v1/auth/login", "/api/v1/auth/select-tenant", "/api/v1/auth/sso", "/api/v1/internal/",
        // SH_ENTERPRISE_CREDENTIAL_BOOTSTRAP §3.4: status endpoint is intentionally
        // anonymous so deploy tooling can poll without credentials. Response shape
        // is intentionally trivial ({ready: bool}) — no recon value. Rate-limit
        // policy "auth" is applied on the endpoint to cap enumeration budget.
        "/api/bootstrap/status"];

    public AuthenticationMiddleware(
        RequestDelegate next,
        IOptions<SelfHostedOptions> options,
        ILogger<AuthenticationMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Always allow public paths and static files
        if (IsPublicPath(path) || IsStaticFile(path))
        {
            await _next(context);
            return;
        }

        // Allow GET requests for non-API paths (SPA routes that fall back to index.html)
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
            context.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Try to authenticate via JWT or API key
        var authenticated = await TryAuthenticateJwt(context)
                         || await TryAuthenticateUserApiKey(context)
                         || TryAuthenticateLegacyApiKey(context);

        if (authenticated)
        {
            if (RequiresPasswordChange(context.User) && !IsPasswordChangePath(path))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Change the temporary administrator password before continuing.",
                    code = "PASSWORD_CHANGE_REQUIRED"
                });
                return;
            }
            await _next(context);
            return;
        }

        // No valid auth provided
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "Authentication required. Provide a JWT Bearer token, X-Api-Key header, or Authorization: Bearer header."
        });
    }

    private static bool RequiresPasswordChange(ClaimsPrincipal principal) =>
        string.Equals(
            principal.FindFirst("password_change_required")?.Value,
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsPasswordChangePath(string path) =>
        path.Equals("/api/v1/auth/me", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/v1/account/change-password", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tries to authenticate using a JWT Bearer token from the Authorization header.
    /// </summary>
    private Task<bool> TryAuthenticateJwt(HttpContext context)
    {
        var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authHeader) ||
            !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(false);
        }

        var token = authHeader[7..].Trim();

        // If it starts with ksh_, it's a user API key, not a JWT
        if (token.StartsWith("ksh_", StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        // Fail-closed when JwtSecret is missing or <32 chars. SelfHostedOptionsValidator
        // normally prevents this state at startup; this is a defense-in-depth guard.
        // Never substitute a literal fallback — that was the 2026-04 P0-3 finding.
        if (string.IsNullOrWhiteSpace(_options.JwtSecret) || _options.JwtSecret.Length < 32)
        {
            _logger.LogCritical(
                "SelfHosted:JwtSecret is missing or <32 chars; JWT request rejected. " +
                "Set SelfHosted:JwtSecret (>=32 chars, cryptographically random).");
            return Task.FromResult(false);
        }

        try
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.JwtSecret));
            var tokenHandler = new JwtSecurityTokenHandler();

            var principal = tokenHandler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _options.JwtIssuer,
                ValidateAudience = true,
                ValidAudience = _options.JwtIssuer,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ClockSkew = TimeSpan.FromMinutes(1)
            }, out _);

            context.User = principal;
            return Task.FromResult(true);
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning("JWT validation failed: {Error}", ex.Message);
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Tries to authenticate using a per-user API key (ksh_ prefix) via X-Api-Key header
    /// or Authorization: Bearer with ksh_ prefix.
    /// </summary>
    private async Task<bool> TryAuthenticateUserApiKey(HttpContext context)
    {
        var apiKey = ApiKeyExtractor.Extract(context);

        if (string.IsNullOrWhiteSpace(apiKey))
            return false;

        // Only handle ksh_ prefixed keys here
        if (!apiKey.StartsWith("ksh_", StringComparison.Ordinal))
            return false;

        try
        {
            var authService = context.RequestServices.GetRequiredService<IAuthService>();
            var result = await authService.ValidateApiKeyAsync(apiKey);

            if (result is null)
            {
                _logger.LogWarning("Invalid user API key from {RemoteIp}", context.Connection.RemoteIpAddress);
                return false;
            }

            // Set the user principal from the JWT claims in the auth result.
            // No fallback secret: SelfHostedOptionsValidator ensures JwtSecret is >=32 chars
            // at startup. If we somehow reach here with an empty/short secret (e.g. validator
            // bypassed), fail-closed rather than sign with a literal.
            if (string.IsNullOrWhiteSpace(_options.JwtSecret) || _options.JwtSecret.Length < 32)
            {
                _logger.LogCritical(
                    "SelfHosted:JwtSecret is missing or <32 chars at user-API-key auth path. " +
                    "Request rejected. Check SelfHostedOptionsValidator registration.");
                return false;
            }

            var tokenHandler = new JwtSecurityTokenHandler();
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.JwtSecret));

            var principal = tokenHandler.ValidateToken(result.Token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _options.JwtIssuer,
                ValidateAudience = true,
                ValidAudience = _options.JwtIssuer,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ClockSkew = TimeSpan.FromMinutes(1)
            }, out _);

            context.User = principal;

            // Check for X-Tenant-Id header to override tenant context
            var tenantIdHeader = context.Request.Headers["X-Tenant-Id"].FirstOrDefault();
            if (!string.IsNullOrEmpty(tenantIdHeader) && Guid.TryParse(tenantIdHeader, out var requestedTenantId))
            {
                var db = context.RequestServices.GetRequiredService<SelfHostedDbContext>();
                var membership = await db.UserTenantMemberships
                    .FirstOrDefaultAsync(m => m.UserId == result.User.Id && m.TenantId == requestedTenantId && m.IsActive);

                if (membership != null)
                {
                    var displayName = result.User.DisplayName ?? result.User.Username;
                    var expiresAt = DateTime.UtcNow.AddMinutes(_options.JwtExpirationMinutes);
                    var newToken = JwtTokenHelper.GenerateToken(
                        result.User.Id, displayName, requestedTenantId, membership.Role,
                        expiresAt, _options.JwtSecret, _options.JwtIssuer, _logger,
                        result.User.MustChangePassword);

                    var newPrincipal = tokenHandler.ValidateToken(newToken, new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = _options.JwtIssuer,
                        ValidateAudience = true,
                        ValidAudience = _options.JwtIssuer,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = key,
                        ClockSkew = TimeSpan.FromMinutes(1)
                    }, out _);

                    context.User = newPrincipal;
                    _logger.LogDebug("API key user {UserId} tenant context overridden to {TenantId}", result.User.Id, requestedTenantId);
                }
                else
                {
                    _logger.LogWarning("User {UserId} requested tenant {TenantId} via X-Tenant-Id but has no active membership", result.User.Id, requestedTenantId);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("User API key validation error: {Error}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Legacy single API key authentication for backward compatibility.
    /// If SelfHostedOptions.ApiKey is set AND no JWT/user-API-key was provided,
    /// checks the provided key against the configured global key.
    /// </summary>
    private bool TryAuthenticateLegacyApiKey(HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            return false;

        var providedKey = ApiKeyExtractor.Extract(context);
        if (string.IsNullOrWhiteSpace(providedKey))
            return false;

        // Don't handle ksh_ keys as legacy
        if (providedKey.StartsWith("ksh_", StringComparison.Ordinal))
            return false;

        var configuredBytes = Encoding.UTF8.GetBytes(_options.ApiKey);
        var providedBytes = Encoding.UTF8.GetBytes(providedKey);

        if (configuredBytes.Length != providedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(configuredBytes, providedBytes))
        {
            _logger.LogWarning("Invalid legacy API key from {RemoteIp}", context.Connection.RemoteIpAddress);
            return false;
        }

        // SEC_P0Triage §Rule 7: legacy key is scoped to a dedicated "LegacyApiKey"
        // role — NOT SuperAdmin. AuthorizationHelpers.IsSuperAdmin / IsAdminOrAbove
        // therefore return false for legacy callers, so /api/superadmin/*,
        // /api/config/*, /api/users/*, /api/admin/* endpoints return 403.
        // The legacy key retains read/write access to knowledge endpoints for
        // backward compatibility until the 2026-06-18 sunset tracked as
        // SEC_LegacyApiKeyRemoval.
        _logger.LogWarning(
            "Legacy API key used from {RemoteIp} — deprecated, scheduled for removal 2026-06-18. " +
            "Migrate callers to per-user ksh_ keys (POST /api/v1/apikeys).",
            context.Connection.RemoteIpAddress);

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "legacy-api-key"),
            new Claim(ClaimTypes.Role, "LegacyApiKey"),
            new Claim("role", "LegacyApiKey")
        };
        var identity = new ClaimsIdentity(claims, "ApiKey");
        context.User = new ClaimsPrincipal(identity);

        return true;
    }

    private static bool IsPublicPath(string path) =>
        PublicPathPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool IsStaticFile(string path) =>
        path.Contains('.') && !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
}
