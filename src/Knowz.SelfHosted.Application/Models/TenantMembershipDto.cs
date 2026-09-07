using Knowz.Core.Enums;

namespace Knowz.SelfHosted.Application.Models;

/// <summary>
/// Represents a user's membership in a specific tenant.
/// </summary>
public class TenantMembershipDto
{
    public Guid TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public string TenantSlug { get; set; } = string.Empty;
    public UserRole Role { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>
/// Extended login result for multi-tenant users.
/// When RequiresTenantSelection is true, Token is empty and AvailableTenants is populated.
/// </summary>
public class MultiTenantLoginResult
{
    public string Token { get; set; } = string.Empty;
    public DateTime? ExpiresAt { get; set; }
    public UserDto? User { get; set; }
    public bool RequiresTenantSelection { get; set; }
    public Guid? UserId { get; set; }
    public List<TenantMembershipDto> AvailableTenants { get; set; } = new();

    /// <summary>
    /// Short-lived, single-use token for tenant selection after multi-tenant login.
    /// Only populated when RequiresTenantSelection is true.
    /// </summary>
    public string? SelectionToken { get; set; }
}

/// <summary>
/// Request to select a tenant after multi-tenant login.
/// </summary>
public class SelectTenantRequest
{
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>
    /// The single-use selection token returned by the login endpoint.
    /// Required to prove the caller authenticated successfully.
    /// </summary>
    public string? SelectionToken { get; set; }
}

/// <summary>
/// Request to switch tenant for an authenticated user.
/// </summary>
public class SwitchTenantRequest
{
    public Guid TenantId { get; set; }
}

/// <summary>
/// Request to add a user to a tenant.
/// </summary>
public class AddUserToTenantRequest
{
    public Guid TenantId { get; set; }
    public UserRole Role { get; set; } = UserRole.User;
}

/// <summary>
/// Request to update a user's role in a tenant.
/// </summary>
public class UpdateUserTenantRoleRequest
{
    public UserRole Role { get; set; }
}
