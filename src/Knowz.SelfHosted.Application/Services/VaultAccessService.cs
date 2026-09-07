using Knowz.Core.Entities;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Application.Services;

/// <summary>
/// Service for checking and managing user vault access permissions.
/// Backward compatible: users without a UserPermissions record get full access.
/// </summary>
public class VaultAccessService : IVaultAccessService
{
    private readonly SelfHostedDbContext _context;

    public VaultAccessService(SelfHostedDbContext context)
    {
        _context = context;
    }

    public async Task<bool> HasVaultAccessAsync(
        Guid userId, Guid vaultId,
        bool requireWrite = false, bool requireDelete = false, bool requireManage = false,
        CancellationToken ct = default)
    {
        var permissions = await _context.UserPermissions
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        // No permissions record = full access (backward compatibility)
        if (permissions == null || permissions.HasAllVaultsAccess)
            return true;

        var access = await _context.UserVaultAccess
            .FirstOrDefaultAsync(va => va.UserId == userId && va.VaultId == vaultId, ct);

        if (access == null)
            return false;

        if (requireManage && !access.CanManage) return false;
        if (requireDelete && !access.CanDelete) return false;
        if (requireWrite && !access.CanWrite) return false;

        return access.CanRead;
    }

    public async Task<bool> HasAllVaultsAccessAsync(Guid userId, CancellationToken ct = default)
    {
        var permissions = await _context.UserPermissions
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
        return permissions?.HasAllVaultsAccess ?? true;
    }

    public async Task<bool> CanCreateVaultsAsync(Guid userId, CancellationToken ct = default)
    {
        var permissions = await _context.UserPermissions
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
        return permissions?.CanCreateVaults ?? true;
    }

    public async Task<List<Guid>> GetAccessibleVaultIdsAsync(
        Guid userId, Guid tenantId, bool requireWrite = false, CancellationToken ct = default)
    {
        var permissions = await _context.UserPermissions
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (permissions == null || permissions.HasAllVaultsAccess)
        {
            return await _context.Vaults
                .Where(v => v.TenantId == tenantId)
                .Select(v => v.Id)
                .ToListAsync(ct);
        }

        var query = _context.UserVaultAccess
            .Where(va => va.UserId == userId && va.TenantId == tenantId);
        query = requireWrite ? query.Where(va => va.CanWrite) : query.Where(va => va.CanRead);

        return await query.Select(va => va.VaultId).ToListAsync(ct);
    }

    public async Task<UserPermissionsDto?> GetUserPermissionsAsync(Guid userId, CancellationToken ct = default)
    {
        var permissions = await _context.UserPermissions
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (permissions == null)
            return null;

        return new UserPermissionsDto(permissions.UserId, permissions.HasAllVaultsAccess, permissions.CanCreateVaults);
    }

    public async Task<UserPermissionsDto> SetUserPermissionsAsync(
        Guid userId, Guid tenantId, bool hasAllVaultsAccess, bool canCreateVaults, CancellationToken ct = default)
    {
        var permissions = await _context.UserPermissions
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (permissions == null)
        {
            permissions = new UserPermissions
            {
                TenantId = tenantId,
                UserId = userId,
                HasAllVaultsAccess = hasAllVaultsAccess,
                CanCreateVaults = canCreateVaults
            };
            _context.UserPermissions.Add(permissions);
        }
        else
        {
            permissions.HasAllVaultsAccess = hasAllVaultsAccess;
            permissions.CanCreateVaults = canCreateVaults;
            permissions.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(ct);
        return new UserPermissionsDto(permissions.UserId, permissions.HasAllVaultsAccess, permissions.CanCreateVaults);
    }

    public async Task<List<UserVaultAccessDto>> GetUserVaultAccessAsync(Guid userId, Guid tenantId, CancellationToken ct = default)
    {
        return await _context.UserVaultAccess
            .Include(va => va.Vault)
            .Where(va => va.UserId == userId && va.TenantId == tenantId)
            .Select(va => new UserVaultAccessDto(
                va.Id,
                va.VaultId,
                va.Vault.Name,
                va.CanRead,
                va.CanWrite,
                va.CanDelete,
                va.CanManage
            ))
            .ToListAsync(ct);
    }

    public async Task<UserVaultAccessDto> GrantVaultAccessAsync(
        Guid userId, Guid tenantId, Guid vaultId,
        bool canRead, bool canWrite, bool canDelete, bool canManage,
        Guid? grantedByUserId, CancellationToken ct = default)
    {
        // Validate vault belongs to tenant
        var vaultBelongsToTenant = await _context.Vaults
            .AnyAsync(v => v.Id == vaultId && v.TenantId == tenantId, ct);
        if (!vaultBelongsToTenant)
        {
            throw new InvalidOperationException($"Vault {vaultId} does not belong to tenant {tenantId}");
        }

        // Validate user belongs to tenant (via membership or home tenant)
        var userBelongsToTenant = await _context.UserTenantMemberships
            .AnyAsync(m => m.UserId == userId && m.TenantId == tenantId && m.IsActive, ct);
        if (!userBelongsToTenant)
        {
            // Fallback to User.TenantId for backward compat (pre-migration users)
            userBelongsToTenant = await _context.Users
                .AnyAsync(u => u.Id == userId && u.TenantId == tenantId, ct);
        }
        if (!userBelongsToTenant)
        {
            throw new InvalidOperationException($"User {userId} does not belong to tenant {tenantId}");
        }

        var existing = await _context.UserVaultAccess
            .FirstOrDefaultAsync(va => va.UserId == userId && va.VaultId == vaultId, ct);

        if (existing != null)
        {
            existing.CanRead = canRead;
            existing.CanWrite = canWrite;
            existing.CanDelete = canDelete;
            existing.CanManage = canManage;
            existing.GrantedByUserId = grantedByUserId;
            existing.GrantedAt = DateTime.UtcNow;
        }
        else
        {
            existing = new UserVaultAccess
            {
                TenantId = tenantId,
                UserId = userId,
                VaultId = vaultId,
                CanRead = canRead,
                CanWrite = canWrite,
                CanDelete = canDelete,
                CanManage = canManage,
                GrantedByUserId = grantedByUserId
            };
            _context.UserVaultAccess.Add(existing);
        }

        await _context.SaveChangesAsync(ct);

        var vaultName = await _context.Vaults
            .Where(v => v.Id == vaultId)
            .Select(v => v.Name)
            .FirstOrDefaultAsync(ct) ?? "Unknown";

        return new UserVaultAccessDto(existing.Id, vaultId, vaultName, canRead, canWrite, canDelete, canManage);
    }

    public async Task<bool> RevokeVaultAccessAsync(Guid userId, Guid tenantId, Guid vaultId, CancellationToken ct = default)
    {
        var access = await _context.UserVaultAccess
            .FirstOrDefaultAsync(va => va.UserId == userId && va.TenantId == tenantId && va.VaultId == vaultId, ct);

        if (access == null)
            return false;

        _context.UserVaultAccess.Remove(access);
        await _context.SaveChangesAsync(ct);
        return true;
    }

    public async Task BatchSetVaultAccessAsync(
        Guid userId, Guid tenantId, List<VaultAccessGrant> grants, Guid? grantedByUserId, CancellationToken ct = default)
    {
        // Validate user belongs to tenant (via membership or home tenant)
        var userBelongsToTenant = await _context.UserTenantMemberships
            .AnyAsync(m => m.UserId == userId && m.TenantId == tenantId && m.IsActive, ct);
        if (!userBelongsToTenant)
        {
            // Fallback to User.TenantId for backward compat (pre-migration users)
            userBelongsToTenant = await _context.Users
                .AnyAsync(u => u.Id == userId && u.TenantId == tenantId, ct);
        }
        if (!userBelongsToTenant)
            throw new InvalidOperationException($"User {userId} does not belong to tenant {tenantId}");

        // Validate all vaults belong to tenant (single query)
        var grantVaultIds = grants.Select(g => g.VaultId).Distinct().ToList();
        if (grantVaultIds.Count > 0)
        {
            var tenantVaultIds = await _context.Vaults
                .Where(v => v.TenantId == tenantId && grantVaultIds.Contains(v.Id))
                .Select(v => v.Id).ToListAsync(ct);
            var invalid = grantVaultIds.Except(tenantVaultIds).FirstOrDefault();
            if (invalid != Guid.Empty)
                throw new InvalidOperationException($"Vault {invalid} does not belong to tenant {tenantId}");
        }

        // Remove all existing access records for this user within this tenant
        var existing = await _context.UserVaultAccess
            .Where(va => va.UserId == userId && va.TenantId == tenantId)
            .ToListAsync(ct);
        _context.UserVaultAccess.RemoveRange(existing);

        // Add new grants
        foreach (var grant in grants)
        {
            _context.UserVaultAccess.Add(new UserVaultAccess
            {
                TenantId = tenantId,
                UserId = userId,
                VaultId = grant.VaultId,
                CanRead = grant.CanRead,
                CanWrite = grant.CanWrite,
                CanDelete = grant.CanDelete,
                CanManage = grant.CanManage,
                GrantedByUserId = grantedByUserId
            });
        }

        await _context.SaveChangesAsync(ct);
    }
}
