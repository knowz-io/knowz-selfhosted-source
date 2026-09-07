namespace Knowz.MCP.Helpers;

/// <summary>
/// Helper methods for API key validation and URL construction.
/// </summary>
public static class ApiKeyValidator
{
    public static bool IsValidApiKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return false;
        // Accept platform keys (kz_, ukz_), self-hosted user keys (ksh_), and self-hosted legacy keys (sh-)
        return (apiKey.StartsWith("kz_") || apiKey.StartsWith("ukz_") || apiKey.StartsWith("ksh_") || apiKey.StartsWith("sh-")) && apiKey.Length >= 20;
    }

    public static string GetBaseUrl(HttpContext context)
    {
        var scheme = context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault()
                     ?? (context.Request.IsHttps ? "https" : "http");
        var host = context.Request.Headers["X-Forwarded-Host"].FirstOrDefault()
                   ?? context.Request.Host.ToString();
        return $"{scheme}://{host}";
    }
}
