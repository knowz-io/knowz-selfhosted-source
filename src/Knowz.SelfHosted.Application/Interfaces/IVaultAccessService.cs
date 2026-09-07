namespace Knowz.SelfHosted.Application.Interfaces;

/// <summary>
/// Service for checking and managing user vault access permissions.
/// </summary>
public interface IVaultAccessService
{
    /// <summary>
    /// Check if a user has access to a specific vault.
    /// </summary>
    Task<bool> HasVaultAccessAsync(Guid userId, Guid vaultId, bool requireWrite = false, bool requireDelete = false, bool requireManage = false, CancellationToken ct = default);

    /// <summary>
    /// Check if a user has unrestricted access to all vaults.
    /// Returns true if no UserPermissions record exists (backward compatibility).
    /// </summary>
    Task<bool> HasAllVaultsAccessAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Check if a user can create new vaults.
    /// </summary>
    Task<bool> CanCreateVaultsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Get list of vault IDs that a user has access to.
    /// Returns all vaults for the tenant if user has unrestricted access.
    /// </summary>
    Task<List<Guid>> GetAccessibleVaultIdsAsync(Guid userId, Guid tenantId, bool requireWrite = false, CancellationToken ct = default);

    /// <summary>
    /// Get the user's permissions record.
    /// </summary>
    Task<UserPermissionsDto?> GetUserPermissionsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Set user permissions (create or update).
    /// </summary>
    Task<UserPermissionsDto> SetUserPermissionsAsync(Guid userId, Guid tenantId, bool hasAllVaultsAccess, bool canCreateVaults, CancellationToken ct = default);

    /// <summary>
    /// Get all vault access records for a user within a tenant.
    /// </summary>
    Task<List<UserVaultAccessDto>> GetUserVaultAccessAsync(Guid userId, Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Grant a user access to a specific vault.
    /// </summary>
    Task<UserVaultAccessDto> GrantVaultAccessAsync(Guid userId, Guid tenantId, Guid vaultId, bool canRead, bool canWrite, bool canDelete, bool canManage, Guid? grantedByUserId, CancellationToken ct = default);

    /// <summary>
    /// Revoke a user's access to a specific vault within a tenant.
    /// </summary>
    Task<bool> RevokeVaultAccessAsync(Guid userId, Guid tenantId, Guid vaultId, CancellationToken ct = default);

    /// <summary>
    /// Batch set vault access for a user (replaces all existing access records).
    /// </summary>
    Task BatchSetVaultAccessAsync(Guid userId, Guid tenantId, List<VaultAccessGrant> grants, Guid? grantedByUserId, CancellationToken ct = default);
}

public record UserPermissionsDto(
    Guid UserId,
    bool HasAllVaultsAccess,
    bool CanCreateVaults
);

public record UserVaultAccessDto(
    Guid Id,
    Guid VaultId,
    string VaultName,
    bool CanRead,
    bool CanWrite,
    bool CanDelete,
    bool CanManage
);

public record VaultAccessGrant(
    Guid VaultId,
    bool CanRead = true,
    bool CanWrite = true,
    bool CanDelete = false,
    bool CanManage = false
);
