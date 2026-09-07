using Microsoft.AspNetCore.Http;

namespace Knowz.SelfHosted.API.Middleware;

public static class ApiKeyExtractor
{
    public static string? Extract(HttpContext context)
    {
        var key = context.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(key)) return key;

        var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(authHeader) &&
            authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authHeader[7..].Trim();
        }

        return null;
    }
}
