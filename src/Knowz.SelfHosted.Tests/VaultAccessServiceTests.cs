using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class VaultAccessServiceTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly VaultAccessService _svc;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid VaultId = Guid.NewGuid();

    public VaultAccessServiceTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);

        _svc = new VaultAccessService(_db);

        // Seed a test tenant, user, and vault
        _db.Tenants.Add(new Tenant { Id = TenantId, Name = "Test Tenant" });
        _db.Users.Add(new User { Id = UserId, TenantId = TenantId, Username = "testuser", PasswordHash = "hash" });
        _db.Vaults.Add(new Vault { Id = VaultId, TenantId = TenantId, Name = "Test Vault" });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    #region Backward Compatibility Tests

    [Fact]
    public async Task GetAccessibleVaultIds_NoPermissionsRecord_ReturnsAllVaults()
    {
        // Arrange: No UserPermissions record = backward compat full access
        var otherVault = new Vault { TenantId = TenantId, Name = "Other Vault" };
        _db.Vaults.Add(otherVault);
        await _db.SaveChangesAsync();

        // Act
        var vaultIds = await _svc.GetAccessibleVaultIdsAsync(UserId, TenantId);

        // Assert: User should have access to all vaults in tenant
        Assert.Equal(2, vaultIds.Count);
        Assert.Contains(VaultId, vaultIds);
        Assert.Contains(otherVault.Id, vaultIds);
    }

    [Fact]
    public async Task GetAccessibleVaultIds_HasAllVaultsAccess_ReturnsAllVaults()
    {
        // Arrange
        _db.UserPermissions.Add(new UserPermissions
        {
            UserId = UserId,
            TenantId = TenantId,
            HasAllVaultsAccess = true,
            CanCreateVaults = false
        });
        await _db.SaveChangesAsync();

        // Act
        var vaultIds = await _svc.GetAccessibleVaultIdsAsync(UserId, TenantId);

        // Assert
        Assert.Single(vaultIds);
        Assert.Contains(VaultId, vaultIds);
    }

    #endregion

    #region Restricted Access Tests

    [Fact]
    public async Task GetAccessibleVaultIds_RestrictedWithGrants_ReturnsOnlyGrantedVaults()
    {
        // Arrange: User has restricted access with one vault grant
        _db.UserPermissions.Add(new UserPermissions
        {
            UserId = UserId,
            TenantId = TenantId,
            HasAllVaultsAccess = false,
            CanCreateVaults = false
        });
        _db.UserVaultAccess.Add(new UserVaultAccess
        {
            UserId = UserId,
            TenantId = TenantId,
            VaultId = VaultId,
            CanRead = true,
            CanWrite = false,
            CanDelete = false,
            CanManage = false
        });

        var otherVault = new Vault { TenantId = TenantId, Name = "Other Vault" };
        _db.Vaults.Add(otherVault);
        await _db.SaveChangesAsync();

        // Act
        var vaultIds = await _svc.GetAccessibleVaultIdsAsync(UserId, TenantId);

        // Assert: Should only return the granted vault
        Assert.Single(vaultIds);
        Assert.Contains(VaultId, vaultIds);
        Assert.DoesNotContain(otherVault.Id, vaultIds);
    }

    [Fact]
    public async Task GetAccessibleVaultIds_RequireWrite_FiltersWritableVaults()
    {
        // Arrange
        _db.UserPermissions.Add(new UserPermissions
        {
            UserId = UserId,
            TenantId = TenantId,
            HasAllVaultsAccess = false
        });

        var readOnlyVault = new Vault { TenantId = TenantId, Name = "Read-Only Vault" };
        var writableVault = new Vault { TenantId = TenantId, Name = "Writable Vault" };
        _db.Vaults.AddRange(readOnlyVault, writableVault);

        _db.UserVaultAccess.Add(new UserVaultAccess
        {
            UserId = UserId,
            TenantId = TenantId,
            VaultId = readOnlyVault.Id,
            CanRead = true,
            CanWrite = false,
            CanDelete = false,
            CanManage = false
        });
        _db.UserVaultAccess.Add(new UserVaultAccess
        {
            UserId = UserId,
            TenantId = TenantId,
            VaultId = writableVault.Id,
            CanRead = true,
            CanWrite = true,
            CanDelete = false,
            CanManage = false
        });
        await _db.SaveChangesAsync();

        // Act
        var vaultIds = await _svc.GetAccessibleVaultIdsAsync(UserId, TenantId, requireWrite: true);

        // Assert: Should only return the writable vault
        Assert.Single(vaultIds);
        Assert.Contains(writableVault.Id, vaultIds);
        Assert.DoesNotContain(readOnlyVault.Id, vaultIds);
    }

    [Fact]
    public async Task GetAccessibleVaultIds_RestrictedNoGrants_ReturnsEmpty()
    {
        // Arrange: User has restricted access but no grants
        _db.UserPermissions.Add(new UserPermissions
        {
            UserId = UserId,
            TenantId = TenantId,
            HasAllVaultsAccess = false,
            CanCreateVaults = false
        });
        await _db.SaveChangesAsync();

        // Act
        var vaultIds = await _svc.GetAccessibleVaultIdsAsync(UserId, TenantId);

        // Assert
        Assert.Empty(vaultIds);
    }

    #endregion

    #region Tenant Scoping Tests

    [Fact]
    public async Task GetAccessibleVaultIds_FiltersByTenant()
    {
        // Arrange: Create vault in different tenant
        var otherTenantId = Guid.NewGuid();
        _db.Tenants.Add(new Tenant { Id = otherTenantId, Name = "Other Tenant" });
        var otherTenantVault = new Vault { TenantId = otherTenantId, Name = "Other Tenant Vault" };
        _db.Vaults.Add(otherTenantVault);

        _db.UserPermissions.Add(new UserPermissions
        {
            UserId = UserId,
            TenantId = TenantId,
            HasAllVaultsAccess = true
        });

        // Add cross-tenant grant (should be filtered out)
        _db.UserVaultAccess.Add(new UserVaultAccess
        {
            UserId = UserId,
            TenantId = otherTenantId,
            VaultId = otherTenantVault.Id,
            CanRead = true,
            CanWrite = false,
            CanDelete = false,
            CanManage = false
        });
        await _db.SaveChangesAsync();

        // Act
        var vaultIds = await _svc.GetAccessibleVaultIdsAsync(UserId, TenantId);

        // Assert: Should only return vaults from the requested tenant
        Assert.Single(vaultIds);
        Assert.Contains(VaultId, vaultIds);
        Assert.DoesNotContain(otherTenantVault.Id, vaultIds);
    }

    [Fact]
    public async Task GrantVaultAccessAsync_ValidatesVaultBelongsToTenant()
    {
        // Arrange: Vault in different tenant
        var otherTenantId = Guid.NewGuid();
        _db.Tenants.Add(new Tenant { Id = otherTenantId, Name = "Other Tenant" });
        var otherTenantVault = new Vault { TenantId = otherTenantId, Name = "Other Tenant Vault" };
        _db.Vaults.Add(otherTenantVault);
        await _db.SaveChangesAsync();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _svc.GrantVaultAccessAsync(
                UserId, TenantId, otherTenantVault.Id,
                canRead: true, canWrite: false, canDelete: false, canManage: false,
                grantedByUserId: null));
    }

    [Fact]
    public async Task GrantVaultAccessAsync_ValidatesUserBelongsToTenant()
    {
        // Arrange: User in different tenant
        var otherTenantId = Guid.NewGuid();
        var otherUser = new User { TenantId = otherTenantId, Username = "otheruser", PasswordHash = "hash" };
        _db.Tenants.Add(new Tenant { Id = otherTenantId, Name = "Other Tenant" });
        _db.Users.Add(otherUser);
        await _db.SaveChangesAsync();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _svc.GrantVaultAccessAsync(
                otherUser.Id, TenantId, VaultId,
                canRead: true, canWrite: false, canDelete: false, canManage: false,
                grantedByUserId: null));
    }

    #endregion

    #region CRUD Operations Tests

    [Fact]
    public async Task GrantVaultAccessAsync_CreatesNewRecord()
    {
        // Act
        var result = await _svc.GrantVaultAccessAsync(
            UserId, TenantId, VaultId,
            canRead: true, canWrite: true, canDelete: false, canManage: false,
            grantedByUserId: null);

        // Assert
        Assert.Equal(VaultId, result.VaultId);
        Assert.True(result.CanRead);
        Assert.True(result.CanWrite);
        Assert.False(result.CanDelete);
        Assert.False(result.CanManage);

        var record = await _db.UserVaultAccess.FirstOrDefaultAsync(va => va.UserId == UserId && va.VaultId == VaultId);
        Assert.NotNull(record);
        Assert.Equal(TenantId, record.TenantId);
    }

    [Fact]
    public async Task GrantVaultAccessAsync_UpdatesExistingRecord()
    {
        // Arrange: Create initial grant
        var existing = new UserVaultAccess
        {
            UserId = UserId,
            TenantId = TenantId,
            VaultId = VaultId,
            CanRead = true,
            CanWrite = false,
            CanDelete = false,
            CanManage = false
        };
        _db.UserVaultAccess.Add(existing);
        await _db.SaveChangesAsync();

        // Act: Update to grant write access
        var result = await _svc.GrantVaultAccessAsync(
            UserId, TenantId, VaultId,
            canRead: true, canWrite: true, canDelete: true, canManage: false,
            grantedByUserId: null);

        // Assert
        Assert.True(result.CanRead);
        Assert.True(result.CanWrite);
        Assert.True(result.CanDelete);

        var updated = await _db.UserVaultAccess.FirstOrDefaultAsync(va => va.UserId == UserId && va.VaultId == VaultId);
        Assert.NotNull(updated);
        Assert.True(updated.CanWrite);
        Assert.True(updated.CanDelete);
    }

    [Fact]
    public async Task RevokeVaultAccessAsync_RemovesRecord()
    {
        // Arrange
        _db.UserVaultAccess.Add(new UserVaultAccess
        {
            UserId = UserId,
            TenantId = TenantId,
            VaultId = VaultId,
            CanRead = true,
            CanWrite = false,
            CanDelete = false,
            CanManage = false
        });
        await _db.SaveChangesAsync();

        // Act
        var removed = await _svc.RevokeVaultAccessAsync(UserId, TenantId, VaultId);

        // Assert
        Assert.True(removed);
        var record = await _db.UserVaultAccess.FirstOrDefaultAsync(va => va.UserId == UserId && va.VaultId == VaultId);
        Assert.Null(record);
    }

    [Fact]
    public async Task RevokeVaultAccessAsync_NonexistentRecord_ReturnsFalse()
    {
        // Act
        var removed = await _svc.RevokeVaultAccessAsync(UserId, TenantId, Guid.NewGuid());

        // Assert
        Assert.False(removed);
    }

    #endregion
}
