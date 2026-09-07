using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Knowz.SelfHosted.Setup.Services;

public static class ConfigValidator
{
    public static bool IsValidUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }

    public static bool IsValidHttpsUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps;
    }

    public static bool IsValidSqlPassword(string password)
    {
        if (password.Length < 8) return false;

        var categories = 0;
        if (password.Any(char.IsUpper)) categories++;
        if (password.Any(char.IsLower)) categories++;
        if (password.Any(char.IsDigit)) categories++;
        if (password.Any(c => !char.IsLetterOrDigit(c))) categories++;

        return categories >= 3;
    }

    public static bool IsValidJwtSecret(string secret)
    {
        return secret.Length >= 32;
    }

    /// <summary>
    /// Mirror of <c>AuthService.WeakPasswordList</c> — case-insensitive substring
    /// denylist for SuperAdmin seed passwords. Kept in sync by
    /// <c>ConfigValidatorTests.WeakPasswordDenylist_MatchesAuthServicePolicy</c>.
    /// Duplicated here (rather than project-referencing Knowz.SelfHosted.Application)
    /// because the setup CLI is a standalone utility — taking a dependency on the
    /// application layer would drag EF Core, BCrypt, and the DbContext into a
    /// 4-step credential-capture wizard.
    /// </summary>
    private static readonly string[] WeakPasswordList =
    {
        "admin", "changeme", "password", "p@ssw0rd", "p@ssword",
        "letmein", "welcome", "knowz", "selfhosted", "default",
        "root", "qwerty", "abc123", "iloveyou", "monkey",
        "dragon", "master", "superuser", "administrator",
        "dev-fallback-secret-key",
    };

    /// <summary>
    /// Same complexity policy as <c>AuthService.PasswordComplexityRegex</c>:
    /// &gt;=12 chars, at least one each of uppercase, lowercase, digit, non-alphanumeric.
    /// </summary>
    private static readonly Regex PasswordComplexityRegex = new(
        @"^(?=.{12,})(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^A-Za-z0-9]).+$",
        RegexOptions.Compiled);

    /// <summary>
    /// Validates a SuperAdmin password captured by the Setup CLI. Shared policy
    /// with <c>AuthService.EnsureSuperAdminExistsAsync</c> so the CLI cannot
    /// capture a credential the runtime would reject at first boot.
    /// Part of SEC_P0Triage expansion (security-officer Option A sign-off).
    /// </summary>
    public static bool IsStrongAdminPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return false;

        foreach (var fragment in WeakPasswordList)
        {
            if (password.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return PasswordComplexityRegex.IsMatch(password);
    }

    /// <summary>
    /// Crypto-strength JWT secret generation. SEC_P0Triage §Rule 6: replaces
    /// the previous <c>System.Random</c>-based version (predictable from a
    /// handful of observed draws) with <c>RandomNumberGenerator</c> + URL-safe
    /// base64 encoding. 48 random bytes → 384 bits of entropy → 64 chars.
    /// </summary>
    public static string GenerateJwtSecret(int byteCount = 48)
    {
        var bytes = new byte[byteCount];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
