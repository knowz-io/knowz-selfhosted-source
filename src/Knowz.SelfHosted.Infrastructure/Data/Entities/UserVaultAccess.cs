using Knowz.Core.Entities;

namespace Knowz.SelfHosted.Infrastructure.Data.Entities;

/// <summary>
/// Represents a user's access permissions to a specific vault.
/// Only checked when UserPermissions.HasAllVaultsAccess is false.
/// </summary>
public class UserVaultAccess
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid VaultId { get; set; }

    public bool CanRead { get; set; } = true;
    public bool CanWrite { get; set; } = true;
    public bool CanDelete { get; set; } = false;
    public bool CanManage { get; set; } = false;

    public Guid? GrantedByUserId { get; set; }
    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public virtual User User { get; set; } = null!;
    public virtual Vault Vault { get; set; } = null!;
}
