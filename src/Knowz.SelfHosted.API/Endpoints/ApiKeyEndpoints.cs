using System.Security.Claims;
using Knowz.SelfHosted.Application.Interfaces;

namespace Knowz.SelfHosted.API.Endpoints;

public static class ApiKeyEndpoints
{
    public static void MapApiKeyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/account/api-key")
            .WithTags("API Key");

        // GET /api/account/api-key — check if current user has an API key
        group.MapGet("/", async (HttpContext context, IUserManagementService svc) =>
        {
            var userId = GetUserId(context);
            if (userId is null) return Results.Unauthorized();

            var user = await svc.GetUserAsync(userId.Value);
            if (user is null) return Results.NotFound(new { error = "User not found." });

            var hasKey = !string.IsNullOrEmpty(user.ApiKey);
            var masked = hasKey ? MaskApiKey(user.ApiKey!) : null;

            return Results.Ok(new { hasKey, maskedKey = masked });
        });

        // POST /api/account/api-key — generate API key for current user
        group.MapPost("/", async (HttpContext context, IUserManagementService svc) =>
        {
            var userId = GetUserId(context);
            if (userId is null) return Results.Unauthorized();

            try
            {
                var apiKey = await svc.GenerateApiKeyAsync(userId.Value);
                return Results.Ok(new { apiKey });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "User not found." });
            }
        });

        // DELETE /api/account/api-key — revoke own API key
        group.MapDelete("/", async (HttpContext context, IUserManagementService svc) =>
        {
            var userId = GetUserId(context);
            if (userId is null) return Results.Unauthorized();

            var user = await svc.GetUserAsync(userId.Value);
            if (user is null) return Results.NotFound(new { error = "User not found." });

            // Revoke by generating a null key — use the underlying service
            // We reuse GenerateApiKeyAsync logic but need to clear the key
            // For now, we'll use the update approach
            await svc.RevokeApiKeyAsync(userId.Value);
            return Results.Ok(new { message = "API key revoked successfully." });
        });
    }

    private static Guid? GetUserId(HttpContext context)
    {
        var claim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private static string MaskApiKey(string apiKey)
    {
        if (apiKey.Length <= 8) return "****";
        var prefix = apiKey[..4]; // "ksh_"
        var last4 = apiKey[^4..];
        return $"{prefix}****{last4}";
    }
}
