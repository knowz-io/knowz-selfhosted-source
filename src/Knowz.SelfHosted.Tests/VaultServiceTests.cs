using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class VaultServiceTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly VaultService _svc;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public VaultServiceTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);

        var vaultRepo = new SelfHostedRepository<Vault>(_db);
        var logger = Substitute.For<ILogger<VaultService>>();

        _svc = new VaultService(vaultRepo, _db, tenantProvider, logger);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public async Task CreateVaultAsync_ReturnsCreateVaultResult()
    {
        var result = await _svc.CreateVaultAsync("My Vault", "A test vault", null, null, CancellationToken.None);

        Assert.IsType<CreateVaultResult>(result);
        Assert.Equal("My Vault", result.Name);
        Assert.True(result.Created);
        Assert.NotEqual(Guid.Empty, result.Id);
    }

    [Fact]
    public async Task CreateVaultAsync_WithParent_CreatesAncestors()
    {
        // Create parent vault
        var parentResult = await _svc.CreateVaultAsync("Parent", null, null, null, CancellationToken.None);

        // Create child vault under parent
        var childResult = await _svc.CreateVaultAsync(
            "Child", null, parentResult.Id.ToString(), null, CancellationToken.None);

        Assert.True(childResult.Created);

        // Verify the VaultAncestor was created
        var ancestors = await _db.VaultAncestors
            .Where(va => va.DescendantVaultId == childResult.Id)
            .ToListAsync();

        Assert.Single(ancestors);
        Assert.Equal(parentResult.Id, ancestors[0].AncestorVaultId);
        Assert.Equal(1, ancestors[0].Depth);
    }

    [Fact]
    public async Task ListVaultsAsync_WithStats_IncludesKnowledgeCount()
    {
        // Create vault + knowledge items linked to it
        var vault = new Vault { TenantId = TenantId, Name = "Stats Vault" };
        _db.Vaults.Add(vault);

        var knowledge = new Knowledge { TenantId = TenantId, Title = "K1", Content = "C1" };
        _db.KnowledgeItems.Add(knowledge);
        _db.KnowledgeVaults.Add(new KnowledgeVault
        {
            TenantId = TenantId,
            KnowledgeId = knowledge.Id,
            VaultId = vault.Id,
            IsPrimary = true
        });
        await _db.SaveChangesAsync();

        var result = await _svc.ListVaultsAsync(includeStats: true, CancellationToken.None);

        Assert.IsType<VaultListResponse>(result);
        Assert.Single(result.Vaults);
        Assert.Equal("Stats Vault", result.Vaults[0].Name);
        Assert.Equal(1, result.Vaults[0].KnowledgeCount);
    }

    [Fact]
    public async Task ListVaultsAsync_WithoutStats_NoKnowledgeCount()
    {
        var vault = new Vault { TenantId = TenantId, Name = "No Stats Vault" };
        _db.Vaults.Add(vault);
        await _db.SaveChangesAsync();

        var result = await _svc.ListVaultsAsync(includeStats: false, CancellationToken.None);

        Assert.Single(result.Vaults);
        Assert.Null(result.Vaults[0].KnowledgeCount);
    }

    [Fact]
    public async Task ListVaultContentsAsync_ReturnsItems()
    {
        var vault = new Vault { TenantId = TenantId, Name = "Content Vault" };
        _db.Vaults.Add(vault);

        var k1 = new Knowledge { TenantId = TenantId, Title = "Item 1", Content = "Content 1" };
        var k2 = new Knowledge { TenantId = TenantId, Title = "Item 2", Content = "Content 2" };
        _db.KnowledgeItems.AddRange(k1, k2);
        _db.KnowledgeVaults.AddRange(
            new KnowledgeVault { TenantId = TenantId, KnowledgeId = k1.Id, VaultId = vault.Id, IsPrimary = true },
            new KnowledgeVault { TenantId = TenantId, KnowledgeId = k2.Id, VaultId = vault.Id, IsPrimary = true });
        await _db.SaveChangesAsync();

        var result = await _svc.ListVaultContentsAsync(vault.Id, false, 50, CancellationToken.None);

        Assert.IsType<VaultContentsResponse>(result);
        Assert.Equal(vault.Id, result.VaultId);
        Assert.Equal(2, result.TotalItems);
        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public async Task ListVaultContentsAsync_WithChildren_IncludesDescendants()
    {
        // Create parent and child vaults
        var parent = new Vault { TenantId = TenantId, Name = "Parent" };
        var child = new Vault { TenantId = TenantId, Name = "Child", ParentVaultId = parent.Id };
        _db.Vaults.AddRange(parent, child);
        _db.VaultAncestors.Add(new VaultAncestor
        {
            AncestorVaultId = parent.Id,
            DescendantVaultId = child.Id,
            Depth = 1
        });

        // Knowledge in child vault
        var k1 = new Knowledge { TenantId = TenantId, Title = "Child Item", Content = "In child" };
        _db.KnowledgeItems.Add(k1);
        _db.KnowledgeVaults.Add(new KnowledgeVault
        {
            TenantId = TenantId,
            KnowledgeId = k1.Id,
            VaultId = child.Id,
            IsPrimary = true
        });

        // Knowledge in parent vault
        var k2 = new Knowledge { TenantId = TenantId, Title = "Parent Item", Content = "In parent" };
        _db.KnowledgeItems.Add(k2);
        _db.KnowledgeVaults.Add(new KnowledgeVault
        {
            TenantId = TenantId,
            KnowledgeId = k2.Id,
            VaultId = parent.Id,
            IsPrimary = true
        });

        await _db.SaveChangesAsync();

        var result = await _svc.ListVaultContentsAsync(parent.Id, includeChildVaults: true, 50, CancellationToken.None);

        Assert.Equal(2, result.TotalItems);
        Assert.Equal(2, result.Items.Count);
    }
}
