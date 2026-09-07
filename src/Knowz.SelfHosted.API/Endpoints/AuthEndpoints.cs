using System.Security.Claims;
using Knowz.SelfHosted.API.Middleware;
using Knowz.SelfHosted.API.Models;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Models;
using Microsoft.AspNetCore.Mvc;

namespace Knowz.SelfHosted.API.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Authentication");

        group.MapPost("/verify", async (HttpContext context, IAuthService authService) =>
        {
            // Try to get API key from header or request body
            var apiKey = ApiKeyExtractor.Extract(context);

            // Also try to read from request body if not in header
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                try
                {
                    var body = await context.Request.ReadFromJsonAsync<VerifyApiKeyRequest>();
                    apiKey = body?.ApiKey;
                }
                catch
                {
                    // Body parsing failed, continue with null
                }
            }

            if (string.IsNullOrWhiteSpace(apiKey))
                return Results.BadRequest(new { error = "API key is required via X-Api-Key header or request body." });

            // For ksh_ prefixed keys, validate via auth service
            if (apiKey.StartsWith("ksh_", StringComparison.Ordinal))
            {
                var result = await authService.ValidateApiKeyAsync(apiKey);
                if (result is null)
                    return Results.Json(new { success = false, error = "Invalid API key." }, statusCode: 401);

                return Results.Ok(new
                {
                    success = true,
                    tenantId = result.User.TenantId.ToString(),
                    tenantName = result.User.TenantName ?? "Default",
                    email = result.User.Email ?? result.User.Username
                });
            }

            // For non-ksh_ keys, check if the user is already authenticated (legacy key handled by middleware)
            var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)
                           ?? context.User.FindFirst("sub");
            var tenantIdClaim = context.User.FindFirst("tenantId");

            if (userIdClaim is not null)
            {
                var userId = Guid.TryParse(userIdClaim.Value, out var uid) ? uid : Guid.Empty;
                var user = userId != Guid.Empty ? await authService.GetCurrentUserAsync(userId) : null;

                return Results.Ok(new
                {
                    success = true,
                    tenantId = tenantIdClaim?.Value ?? user?.TenantId.ToString() ?? "",
                    tenantName = user?.TenantName ?? "Default",
                    email = user?.Email ?? user?.Username ?? "api-key-user"
                });
            }

            return Results.Json(new { success = false, error = "Invalid API key." }, statusCode: 401);
        })
        .AllowAnonymous()
        .Produces<object>()
        .Produces(401)
        .Produces(400);

        group.MapPost("/login", async (IAuthService authService, LoginRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
                return Results.BadRequest(new { error = "Username and password are required." });

            try
            {
                var result = await authService.MultiTenantLoginAsync(request.Username, request.Password);
                return Results.Ok(result);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(new { error = "Invalid username or password." }, statusCode: 401);
            }
        })
        .AllowAnonymous()
        .RequireRateLimiting("auth")
        .Produces<MultiTenantLoginResult>()
        .Produces(401)
        .Produces(400)
        .Produces(429);

        group.MapPost("/select-tenant", async (IAuthService authService, SelectTenantRequest request) =>
        {
            if (request.UserId == Guid.Empty || request.TenantId == Guid.Empty)
                return Results.BadRequest(new { error = "UserId and TenantId are required." });

            if (string.IsNullOrWhiteSpace(request.SelectionToken))
                return Results.BadRequest(new { error = "SelectionToken is required." });

            try
            {
                var result = await authService.SelectTenantAsync(request.UserId, request.TenantId, request.SelectionToken);
                return Results.Ok(result);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 401);
            }
        })
        .AllowAnonymous()
        .RequireRateLimiting("auth")
        .Produces<AuthResult>()
        .Produces(401)
        .Produces(400);

        group.MapPost("/switch-tenant", async (HttpContext context, IAuthService authService, SwitchTenantRequest request) =>
        {
            var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)
                           ?? context.User.FindFirst("sub");

            if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
            {
                return Results.Json(new { error = "Not authenticated." }, statusCode: 401);
            }

            if (request.TenantId == Guid.Empty)
                return Results.BadRequest(new { error = "TenantId is required." });

            try
            {
                var result = await authService.SwitchTenantAsync(userId, request.TenantId);
                return Results.Ok(result);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 401);
            }
        })
        .Produces<AuthResult>()
        .Produces(401)
        .Produces(400);

        group.MapGet("/tenants", async (HttpContext context, IAuthService authService) =>
        {
            var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)
                           ?? context.User.FindFirst("sub");

            if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
            {
                return Results.Json(new { error = "Not authenticated." }, statusCode: 401);
            }

            var tenants = await authService.GetUserTenantsAsync(userId);
            return Results.Ok(tenants);
        })
        .Produces<List<TenantMembershipDto>>()
        .Produces(401);

        group.MapGet("/me", async (HttpContext context, IAuthService authService) =>
        {
            var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)
                           ?? context.User.FindFirst("sub");

            if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
            {
                return Results.Json(new { error = "Not authenticated." }, statusCode: 401);
            }

            var user = await authService.GetCurrentUserAsync(userId);
            if (user is null)
                return Results.Json(new { error = "User not found." }, statusCode: 401);

            return Results.Ok(user);
        })
        .Produces<UserDto>()
        .Produces(401);

        // --- SSO Endpoints ---

        group.MapGet("/sso/providers", async (ISelfHostedSSOService ssoService) =>
        {
            var providers = await ssoService.GetEnabledProvidersAsync();
            return Results.Ok(new { success = true, data = providers });
        })
        .AllowAnonymous();

        group.MapGet("/sso/authorize", async (
            HttpContext httpContext,
            [FromQuery] string provider,
            [FromQuery] string? redirectUri,
            ISelfHostedSSOService ssoService) =>
        {
            // OIDC redirect_uri must be an absolute URL. If the caller supplies
            // one, reject relative values up-front (Entra returns AADSTS50011;
            // Google returns invalid_request when redirect_uri is relative).
            if (!string.IsNullOrWhiteSpace(redirectUri) &&
                !Uri.IsWellFormedUriString(redirectUri, UriKind.Absolute))
            {
                return Results.BadRequest(new { error = "redirectUri must be an absolute URL." });
            }

            // Default fallback is built from the inbound request so the IdP
            // receives a complete reply URL. Requires UseForwardedHeaders to
            // be configured when running behind a TLS-terminating proxy.
            var effectiveRedirectUri = string.IsNullOrWhiteSpace(redirectUri)
                ? $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/auth/sso/callback"
                : redirectUri;

            var result = await ssoService.GenerateAuthorizeUrlAsync(provider, effectiveRedirectUri);
            if (!result.Success)
                return Results.BadRequest(new { error = result.ErrorMessage });

            return Results.Ok(new { success = true, data = new
            {
                authorizationUrl = result.AuthorizationUrl,
                state = result.State,
            }});
        })
        .AllowAnonymous();

        group.MapPost("/sso/callback", async (SSOCallbackRequest request, ISelfHostedSSOService ssoService) =>
        {
            if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.State))
                return Results.BadRequest(new { error = "Code and state are required" });

            var result = await ssoService.HandleCallbackAsync(request.Code, request.State);
            if (!result.Success)
                return Results.Json(new { error = result.ErrorMessage }, statusCode: 401);

            return Results.Ok(new { success = true, data = result });
        })
        .AllowAnonymous();
    }
}
