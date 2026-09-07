using Knowz.Core.Entities;
using Knowz.Core.Enums;

namespace Knowz.SelfHosted.Infrastructure.Data.Entities;

/// <summary>
/// Junction table allowing a user to be a member of multiple tenants with per-tenant roles.
/// User.TenantId remains as "home tenant" for backward compatibility.
/// </summary>
public class UserTenantMembership
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public UserRole Role { get; set; } = UserRole.User;
    public bool IsActive { get; set; } = true;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual User User { get; set; } = null!;
    public virtual Tenant Tenant { get; set; } = null!;
}
