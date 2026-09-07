using System.Security.Cryptography;
using System.Text;
using Knowz.Core.Enums;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.API.Endpoints;

/// <summary>
/// Internal endpoints called by the MCP server for authentication.
/// All endpoints are protected by X-Service-Key header validation.
/// </summary>
public static class InternalMcpEndpoints
{
    public static void MapInternalMcpEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/internal").WithTags("Internal MCP");

        group.MapPost("/mcp/authenticate", AuthenticateAsync);
        group.MapPost("/sso/resolve", SSOResolveAsync);
        group.MapGet("/sso/config", GetSSOConfig);
    }

    /// <summary>
    /// Authenticates a user by username/password and returns their API key.
    /// Called by the MCP server OAuth flow when a user logs in with credentials.
    /// </summary>
    private static async Task<IResult> AuthenticateAsync(
        McpAuthenticateRequest request,
        IAuthService authService,
        IUserManagementService userManagementService,
        SelfHostedDbContext db,
        HttpContext httpContext,
        IConfiguration configuration,
        ILogger<Program> logger)
    {
        if (!ValidateServiceKey(httpContext, configuration))
            return Results.Json(new { success = false, error = "Unauthorized" }, statusCode: 401);

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return Results.BadRequest(new { success = false, error = "Username and password are required" });

        try
        {
            var authResult = await authService.LoginAsync(request.Username, request.Password);
            var user = authResult.User;

            // Get or generate API key
            var apiKey = user.ApiKey;
            if (string.IsNullOrEmpty(apiKey))
            {
                apiKey = await userManagementService.GenerateApiKeyAsync(user.Id);
                logger.LogInformation("Generated API key for user {UserId} via MCP authenticate", user.Id);
            }

            // Resolve tenant based on memberships
            var activeMemberships = await db.UserTenantMemberships
                .Include(m => m.Tenant)
                .Where(m => m.UserId == user.Id && m.IsActive)
                .ToListAsync();

            if (request.TenantId.HasValue)
            {
                // Caller specified a tenant - validate membership
                var membership = activeMemberships.FirstOrDefault(m => m.TenantId == request.TenantId.Value);
                if (membership == null)
                {
                    return Results.Json(
                        new { success = false, error = "User does not have access to the specified tenant" },
                        statusCode: 403);
                }

                return Results.Ok(new
                {
                    success = true,
                    data = new
                    {
                        apiKey,
                        email = user.Email,
                        displayName = user.DisplayName,
                        userId = user.Id,
                        tenantId = membership.TenantId,
                    }
                });
            }

            if (activeMemberships.Count >= 2)
            {
                // Multiple tenants - client must select
                return Results.Ok(new
                {
                    success = true,
                    requiresTenantSelection = true,
                    data = new
                    {
                        apiKey,
                        userId = user.Id,
                        tenants = activeMemberships.Select(m => new
                        {
                            tenantId = m.TenantId,
                            tenantName = m.Tenant?.Name ?? "Unknown",
                            role = m.Role.ToString(),
                        })
                    }
                });
            }

            // 0 or 1 membership - proceed with single tenant
            var resolvedTenantId = activeMemberships.FirstOrDefault()?.TenantId ?? user.TenantId;

            return Results.Ok(new
            {
                success = true,
                data = new
                {
                    apiKey,
                    email = user.Email,
                    displayName = user.DisplayName,
                    userId = user.Id,
                    tenantId = resolvedTenantId,
                }
            });
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Json(new { success = false, error = "Invalid username or password" }, statusCode: 401);
        }
    }

    /// <summary>
    /// Resolves an email address to an API key. Called by the MCP server SSO flow.
    /// Mirrors the platform endpoint at /api/v1/internal/sso/resolve.
    /// </summary>
    private static async Task<IResult> SSOResolveAsync(
        SSOResolveRequest request,
        IUserManagementService userManagementService,
        SelfHostedDbContext db,
        HttpContext httpContext,
        IConfiguration configuration,
        ILogger<Program> logger)
    {
        if (!ValidateServiceKey(httpContext, configuration))
            return Results.Json(new { success = false, error = "Unauthorized" }, statusCode: 401);

        if (string.IsNullOrWhiteSpace(request.Email))
            return Results.BadRequest(new { success = false, error = "Email is required" });

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        // Look up user by email
        var user = await db.Users
            .Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Email != null
                                   && u.Email.ToLower() == normalizedEmail
                                   && u.IsActive);

        if (user == null)
        {
            // Auto-provision if enabled
            var autoProvision = configuration.GetValue<bool>("SSO:AutoProvisionUsers");
            if (!autoProvision)
            {
                return Results.Json(
                    new { success = false, error = "No account found for this email" },
                    statusCode: 404);
            }

            // Create user via SSO auto-provisioning
            var tenant = await db.Tenants.FirstOrDefaultAsync();
            if (tenant == null)
            {
                return Results.Json(
                    new { success = false, error = "No tenant configured" },
                    statusCode: 500);
            }

            user = new Knowz.Core.Entities.User
            {
                TenantId = tenant.Id,
                Username = normalizedEmail,
                Email = normalizedEmail,
                DisplayName = normalizedEmail,
                PasswordHash = "SSO_ONLY_NO_PASSWORD",
                Role = Knowz.Core.Enums.UserRole.User,
                IsActive = true,
                OAuthProvider = request.Provider,
                OAuthEmail = normalizedEmail,
                LastLoginAt = DateTime.UtcNow,
            };

            db.Users.Add(user);
            await db.SaveChangesAsync();

            // Create tenant membership for auto-provisioned user
            var membership = new UserTenantMembership
            {
                UserId = user.Id,
                TenantId = tenant.Id,
                Role = UserRole.User,
                IsActive = true,
                JoinedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.UserTenantMemberships.Add(membership);
            await db.SaveChangesAsync();

            user.Tenant = tenant;
            logger.LogInformation("Auto-provisioned SSO user {Email} via MCP SSO resolve", normalizedEmail);
        }

        // Get or generate API key
        var apiKey = user.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            apiKey = await userManagementService.GenerateApiKeyAsync(user.Id);
            logger.LogInformation("Generated API key for user {UserId} via MCP SSO resolve", user.Id);
        }

        // Update login tracking
        user.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // Response shape matches platform InternalSSOEndpoints
        return Results.Ok(new
        {
            success = true,
            data = new
            {
                apiKey,
                email = normalizedEmail,
                tenantName = user.Tenant?.Name ?? "Unknown",
                userId = user.Id,
            }
        });
    }

    /// <summary>
    /// Returns SSO provider configuration for the MCP server.
    /// The MCP server calls this to discover which SSO providers are configured.
    /// </summary>
    private static IResult GetSSOConfig(
        HttpContext httpContext,
        IConfiguration configuration)
    {
        if (!ValidateServiceKey(httpContext, configuration))
            return Results.Json(new { success = false, error = "Unauthorized" }, statusCode: 401);

        var isEnabled = configuration.GetValue<bool>("SSO:Enabled");
        var providers = new List<object>();

        if (isEnabled)
        {
            var msClientId = configuration["SSO:Microsoft:ClientId"];
            if (!string.IsNullOrEmpty(msClientId))
            {
                var directoryTenantId = configuration["SSO:Microsoft:DirectoryTenantId"];
                var authority = !string.IsNullOrEmpty(directoryTenantId) && !directoryTenantId.Contains(',')
                    ? $"https://login.microsoftonline.com/{directoryTenantId}/v2.0"
                    : "https://login.microsoftonline.com/common/v2.0";

                providers.Add(new
                {
                    provider = "Microsoft",
                    displayName = "Sign in with Microsoft",
                    clientId = msClientId,
                    authority,
                    configured = true,
                });
            }

            var googleClientId = configuration["SSO:Google:ClientId"];
            if (!string.IsNullOrEmpty(googleClientId))
            {
                providers.Add(new
                {
                    provider = "Google",
                    displayName = "Sign in with Google",
                    clientId = googleClientId,
                    authority = "https://accounts.google.com",
                    configured = true,
                });
            }
        }

        return Results.Ok(new { success = true, data = new { enabled = isEnabled, providers } });
    }

    private static bool ValidateServiceKey(HttpContext httpContext, IConfiguration configuration)
    {
        var expectedKey = configuration["MCP:ServiceKey"];
        var providedKey = httpContext.Request.Headers["X-Service-Key"].FirstOrDefault();

        if (string.IsNullOrEmpty(expectedKey) || string.IsNullOrEmpty(providedKey))
            return false;

        var expectedBytes = Encoding.UTF8.GetBytes(expectedKey);
        var providedBytes = Encoding.UTF8.GetBytes(providedKey);

        return expectedBytes.Length == providedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }
}

public class McpAuthenticateRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public Guid? TenantId { get; set; }
}

public class SSOResolveRequest
{
    public string Email { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
}
