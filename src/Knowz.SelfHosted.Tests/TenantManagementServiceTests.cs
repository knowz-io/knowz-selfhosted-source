using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Models;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class TenantManagementServiceTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly ITenantManagementService _service;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public TenantManagementServiceTests()
    {
        var dbOptions = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<Knowz.Core.Interfaces.ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(dbOptions, tenantProvider);
        var logger = Substitute.For<ILogger<TenantManagementService>>();

        _service = new TenantManagementService(_db, logger);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // --- ListTenantsAsync ---

    [Fact]
    public async Task Should_ReturnEmptyList_WhenNoTenants()
    {
        var result = await _service.ListTenantsAsync();

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Should_ReturnAllTenants_WhenTenantsExist()
    {
        _db.Tenants.Add(new Tenant { Name = "Tenant A", Slug = "tenant-a" });
        _db.Tenants.Add(new Tenant { Name = "Tenant B", Slug = "tenant-b" });
        await _db.SaveChangesAsync();

        var result = await _service.ListTenantsAsync();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task Should_IncludeUserCount_InTenantList()
    {
        var tenant = new Tenant { Name = "Tenant A", Slug = "tenant-a" };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();

        _db.Users.Add(new User { TenantId = tenant.Id, Username = "user1", PasswordHash = "hash" });
        _db.Users.Add(new User { TenantId = tenant.Id, Username = "user2", PasswordHash = "hash" });
        await _db.SaveChangesAsync();

        var result = await _service.ListTenantsAsync();

        Assert.Single(result);
        Assert.Equal(2, result[0].UserCount);
    }

    // --- GetTenantAsync ---

    [Fact]
    public async Task Should_ReturnTenant_WhenExists()
    {
        var tenant = new Tenant { Name = "Test", Slug = "test" };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();

        var result = await _service.GetTenantAsync(tenant.Id);

        Assert.NotNull(result);
        Assert.Equal("Test", result!.Name);
        Assert.Equal("test", result.Slug);
    }

    [Fact]
    public async Task Should_ReturnNull_WhenTenantNotFound()
    {
        var result = await _service.GetTenantAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    // --- CreateTenantAsync ---

    [Fact]
    public async Task Should_CreateTenant_WithValidRequest()
    {
        var request = new CreateTenantRequest
        {
            Name = "New Tenant",
            Slug = "new-tenant",
            Description = "A test tenant"
        };

        var result = await _service.CreateTenantAsync(request);

        Assert.NotNull(result);
        Assert.Equal("New Tenant", result.Name);
        Assert.Equal("new-tenant", result.Slug);
        Assert.Equal("A test tenant", result.Description);
        Assert.True(result.IsActive);
        Assert.NotEqual(Guid.Empty, result.Id);
    }

    [Fact]
    public async Task Should_ThrowException_WhenSlugDuplicate()
    {
        _db.Tenants.Add(new Tenant { Name = "Existing", Slug = "duplicate-slug" });
        await _db.SaveChangesAsync();

        var request = new CreateTenantRequest { Name = "New", Slug = "duplicate-slug" };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateTenantAsync(request));
    }

    [Fact]
    public async Task Should_PersistTenant_ToDatabase()
    {
        var request = new CreateTenantRequest { Name = "Persisted", Slug = "persisted" };

        var result = await _service.CreateTenantAsync(request);

        var tenant = await _db.Tenants.FindAsync(result.Id);
        Assert.NotNull(tenant);
        Assert.Equal("Persisted", tenant!.Name);
    }

    // --- UpdateTenantAsync ---

    [Fact]
    public async Task Should_UpdateTenantName_WhenProvided()
    {
        var tenant = new Tenant { Name = "Old", Slug = "old" };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();

        var result = await _service.UpdateTenantAsync(tenant.Id,
            new UpdateTenantRequest { Name = "Updated" });

        Assert.Equal("Updated", result.Name);
        Assert.Equal("old", result.Slug); // Slug unchanged
    }

    [Fact]
    public async Task Should_UpdateTenantSlug_WhenProvided()
    {
        var tenant = new Tenant { Name = "Test", Slug = "old-slug" };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();

        var result = await _service.UpdateTenantAsync(tenant.Id,
            new UpdateTenantRequest { Slug = "new-slug" });

        Assert.Equal("new-slug", result.Slug);
    }

    [Fact]
    public async Task Should_DeactivateTenant_WhenIsActiveFalse()
    {
        var tenant = new Tenant { Name = "Active", Slug = "active" };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();

        var result = await _service.UpdateTenantAsync(tenant.Id,
            new UpdateTenantRequest { IsActive = false });

        Assert.False(result.IsActive);
    }

    [Fact]
    public async Task Should_ThrowKeyNotFound_WhenUpdateNonExistentTenant()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.UpdateTenantAsync(Guid.NewGuid(),
                new UpdateTenantRequest { Name = "X" }));
    }

    // --- DeleteTenantAsync ---

    [Fact]
    public async Task Should_DeleteTenant_WhenExists()
    {
        var tenant = new Tenant { Name = "Delete Me", Slug = "delete-me" };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();

        await _service.DeleteTenantAsync(tenant.Id);

        var found = await _db.Tenants.FindAsync(tenant.Id);
        Assert.Null(found);
    }

    [Fact]
    public async Task Should_ThrowKeyNotFound_WhenDeleteNonExistentTenant()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.DeleteTenantAsync(Guid.NewGuid()));
    }
}
