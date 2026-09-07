using Knowz.Core.Entities;

namespace Knowz.SelfHosted.Infrastructure.Data.Entities;

/// <summary>
/// Represents user-level permissions and restrictions.
/// One record per user. If no record exists, user has full access (backward compatibility).
/// </summary>
public class UserPermissions
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Whether the user can create new vaults.</summary>
    public bool CanCreateVaults { get; set; } = true;

    /// <summary>
    /// If true, user has access to all vaults in the tenant.
    /// If false, check UserVaultAccess records for specific vault permissions.
    /// </summary>
    public bool HasAllVaultsAccess { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public virtual User User { get; set; } = null!;
}
