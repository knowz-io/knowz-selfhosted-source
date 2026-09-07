using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Knowz.Core.Enums;

namespace Knowz.SelfHosted.API.Helpers;

/// <summary>
/// Centralized authorization helpers for admin endpoint access control.
/// Replaces per-file IsSuperAdminOrAdmin() methods.
/// </summary>
public static class AuthorizationHelpers
{
    /// <summary>
    /// Returns true only for SuperAdmin role. Use for platform-wide operations
    /// (tenant CRUD, SSO config, system configuration).
    /// </summary>
    public static bool IsSuperAdmin(HttpContext context) =>
        context.User.IsInRole("SuperAdmin");

    /// <summary>
    /// Returns true for Admin or SuperAdmin roles. Use for tenant-scoped operations
    /// (user management, vault access) — always combine with tenant scoping for Admin.
    /// </summary>
    public static bool IsAdminOrAbove(HttpContext context) =>
        context.User.IsInRole("SuperAdmin") || context.User.IsInRole("Admin");

    /// <summary>
    /// Authenticated User, Admin, or SuperAdmin. Use for mothership connect/browse/pull
    /// (VERIFY-M1). Does NOT admit LegacyApiKey.
    /// </summary>
    public static bool IsAuthenticatedKnowzUser(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true
        && (context.User.IsInRole("User")
            || context.User.IsInRole("Admin")
            || context.User.IsInRole("SuperAdmin"));

    /// <summary>
    /// Extracts the caller's tenant ID from JWT "tenantId" claim.
    /// Returns null for legacy API key users (no tenantId in claims).
    /// </summary>
    public static Guid? GetCallerTenantId(HttpContext context)
    {
        var claim = context.User.FindFirst("tenantId")?.Value;
        return !string.IsNullOrEmpty(claim) && Guid.TryParse(claim, out var id) ? id : null;
    }

    /// <summary>
    /// Extracts the caller's user ID from JWT "sub" claim.
    /// Returns null for legacy API key users.
    /// </summary>
    public static Guid? GetCallerId(HttpContext context)
    {
        var claim = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return !string.IsNullOrEmpty(claim) && Guid.TryParse(claim, out var id) ? id : null;
    }

    /// <summary>
    /// Checks whether the caller is allowed to assign the given role.
    /// SuperAdmin: can assign any role.
    /// Admin: can only assign User role (cannot create/promote Admin or SuperAdmin).
    /// Uses explicit role name checks — immune to UserRole enum ordinal ordering.
    /// </summary>
    public static bool CanAssignRole(HttpContext context, UserRole targetRole)
    {
        if (context.User.IsInRole("SuperAdmin"))
            return true; // SuperAdmin can assign any role
        if (context.User.IsInRole("Admin"))
            return targetRole == UserRole.User; // Admin can only assign User
        return false;
    }

    /// <summary>
    /// Returns true if the given role is Admin or SuperAdmin.
    /// Uses explicit comparison — immune to UserRole enum ordinal ordering.
    /// </summary>
    public static bool IsPrivilegedRole(UserRole role) =>
        role == UserRole.Admin || role == UserRole.SuperAdmin;

    /// <summary>
    /// Returns a 403 IResult with the standard error format.
    /// </summary>
    public static IResult Forbidden(string message = "Forbidden.") =>
        Results.Json(new { error = message }, statusCode: 403);
}
