using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Knowz.SelfHosted.Application.Extensions;

public static class JwtTokenHelper
{
    private const int MinSecretLength = 32;

    /// <summary>
    /// Generates a JWT using the user's own TenantId and Role (backward-compatible).
    /// </summary>
    public static string GenerateToken(User user, DateTime expiresAt, string jwtSecret, string jwtIssuer, ILogger logger)
    {
        var displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName;
        return GenerateToken(
            user.Id, displayName, user.TenantId, user.Role, expiresAt,
            jwtSecret, jwtIssuer, logger, user.MustChangePassword);
    }

    /// <summary>
    /// Generates a JWT with explicit tenantId and role, allowing tokens to reflect
    /// a specific tenant membership rather than the user's home tenant.
    ///
    /// Fails-closed when <paramref name="jwtSecret"/> is null/empty/&lt;32 chars.
    /// No fallback literal — see SEC_P0Triage Item 4 (§Rule 1). Callers must either
    /// supply a validated secret (SelfHostedOptionsValidator catches this at startup)
    /// or handle the thrown exception.
    /// </summary>
    public static string GenerateToken(
        Guid userId,
        string displayName,
        Guid tenantId,
        UserRole role,
        DateTime expiresAt,
        string jwtSecret,
        string jwtIssuer,
        ILogger logger,
        bool mustChangePassword = false)
    {
        if (string.IsNullOrWhiteSpace(jwtSecret) || jwtSecret.Length < MinSecretLength)
        {
            logger.LogCritical(
                "JwtSecret is missing or <{MinLength} chars; token generation refused. " +
                "Set SelfHosted:JwtSecret (>=32 chars, cryptographically random).",
                MinSecretLength);
            throw new InvalidOperationException(
                $"SelfHosted:JwtSecret is required and must be at least {MinSecretLength} characters.");
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Name, displayName),
            new Claim(ClaimTypes.Role, role.ToString()),
            new Claim("role", role.ToString()),
            new Claim("tenantId", tenantId.ToString()),
            new Claim("password_change_required", mustChangePassword ? "true" : "false"),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: jwtIssuer,
            audience: jwtIssuer,
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
