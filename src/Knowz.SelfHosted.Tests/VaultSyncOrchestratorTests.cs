using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.Core.Schema;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// Tests for VaultSyncOrchestrator error paths, status queries, and CRUD operations.
/// Full sync flow tests (pull/push with transactions) are tested via integration tests.
/// </summary>
public class VaultSyncOrchestratorTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly IPlatformSyncClient _platformClient;
    private readonly VaultSyncOrchestrator _orchestrator;

    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid LocalVaultId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RemoteVaultId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public VaultSyncOrchestratorTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);

        _platformClient = Substitute.For<IPlatformSyncClient>();
        var logger = Substitute.For<ILogger<VaultSyncOrchestrator>>();

        // Pass null! for VaultScopedExportService and FileSyncService —
        // tests in this class only exercise error paths and CRUD that don't touch those dependencies
        _orchestrator = new VaultSyncOrchestrator(
            _db, tenantProvider, _platformClient,
            null!, null!, logger);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // --- SyncAsync error paths ---

    [Fact]
    public async Task SyncAsync_NoLinkFound_ReturnsFailure()
    {
        var result = await _orchestrator.SyncAsync(Guid.NewGuid());

        Assert.False(result.Success);
        Assert.Contains("No sync link found", result.Error);
    }

    [Fact]
    public async Task SyncAsync_SyncDisabled_ReturnsFailure()
    {
        var link = CreateSyncLink();
        link.SyncEnabled = false;
        _db.VaultSyncLinks.Add(link);
        await _db.SaveChangesAsync();

        var result = await _orchestrator.SyncAsync(LocalVaultId);

        Assert.False(result.Success);
        Assert.Contains("disabled", result.Error);
    }

    [Fact]
    public async Task SyncAsync_AlreadyInProgress_ReturnsFailure()
    {
        var link = CreateSyncLink();
        link.LastSyncStatus = VaultSyncStatus.InProgress;
        _db.VaultSyncLinks.Add(link);
        await _db.SaveChangesAsync();

        var result = await _orchestrator.SyncAsync(LocalVaultId);

        Assert.False(result.Success);
        Assert.Contains("already in progress", result.Error);
    }

    [Fact]
    public async Task SyncAsync_IncompatibleSchema_SetsStatusToFailed()
    {
        var link = CreateSyncLink();
        _db.VaultSyncLinks.Add(link);
        await _db.SaveChangesAsync();

        _platformClient.GetSchemaAsync(Arg.Any<VaultSyncLink>(), Arg.Any<CancellationToken>())
            .Returns(new PlatformSchemaResponse { Version = 999 });

        var result = await _orchestrator.SyncAsync(LocalVaultId);

        Assert.False(result.Success);
        Assert.Contains("not compatible", result.Error);

        var updatedLink = await _db.VaultSyncLinks.FirstAsync(l => l.LocalVaultId == LocalVaultId);
        Assert.Equal(VaultSyncStatus.Failed, updatedLink.LastSyncStatus);
        Assert.Contains("not compatible", updatedLink.LastSyncError);
    }

    [Fact]
    public async Task SyncAsync_SchemaCheckThrows_SetsStatusToFailed()
    {
        var link = CreateSyncLink();
        _db.VaultSyncLinks.Add(link);
        await _db.SaveChangesAsync();

        _platformClient.GetSchemaAsync(Arg.Any<VaultSyncLink>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Connection refused"));

        var result = await _orchestrator.SyncAsync(LocalVaultId);

        Assert.False(result.Success);
        Assert.Contains("Connection refused", result.Error);

        var updatedLink = await _db.VaultSyncLinks.FirstAsync(l => l.LocalVaultId == LocalVaultId);
        Assert.Equal(VaultSyncStatus.Failed, updatedLink.LastSyncStatus);
    }

    [Fact]
    public async Task SyncAsync_SetsStatusToInProgress_BeforeSchemaCheck()
    {
        var link = CreateSyncLink();
        _db.VaultSyncLinks.Add(link);
        await _db.SaveChangesAsync();

        VaultSyncStatus capturedStatus = VaultSyncStatus.NotSynced;
        _platformClient.GetSchemaAsync(Arg.Any<VaultSyncLink>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                // Capture the link's status at the time of the schema check
                var dbLink = await _db.VaultSyncLinks.FirstAsync(l => l.LocalVaultId == LocalVaultId);
                capturedStatus = dbLink.LastSyncStatus;
                throw new Exception("test abort");
            });

        await _orchestrator.SyncAsync(LocalVaultId);

        Assert.Equal(VaultSyncStatus.InProgress, capturedStatus);
    }

    [Fact]
    public async Task SyncAsync_ReportsDuration()
    {
        var link = CreateSyncLink();
        _db.VaultSyncLinks.Add(link);
        await _db.SaveChangesAsync();

        _platformClient.GetSchemaAsync(Arg.Any<VaultSyncLink>(), Arg.Any<CancellationToken>())
            .Returns(new PlatformSchemaResponse { Version = 999 });

        var result = await _orchestrator.SyncAsync(LocalVaultId);

        Assert.True(result.Duration.TotalMilliseconds >= 0);
    }

    // --- GetStatusAsync ---

    [Fact]
    public async Task GetStatusAsync_ReturnsNull_WhenNoLinkExists()
    {
        var status = await _orchestrator.GetStatusAsync(Guid.NewGuid());
        Assert.Null(status);
    }

    [Fact]
    public async Task GetStatusAsync_ReturnsStatus_WhenLinkExists()
    {
        var link = CreateSyncLink();
        _db.VaultSyncLinks.Add(link);
        _db.Vaults.Add(new Knowz.Core.Entities.Vault
        {
            Id = LocalVaultId, TenantId = TenantId, Name = "TestVault"
        });
        await _db.SaveChangesAsync();

        var status = await _orchestrator.GetStatusAsync(LocalVaultId);

        Assert.NotNull(status);
        Assert.Equal(LocalVaultId, status.LocalVaultId);
        Assert.Equal("TestVault", status.LocalVaultName);
        Assert.Equal(RemoteVaultId, status.RemoteVaultId);
        Assert.Equal("https://api.knowz.io", status.PlatformApiUrl);
        Assert.True(status.SyncEnabled);
    }

    [Fact]
    public async Task GetStatusAsync_ReturnsUnknown_WhenVaultNotFound()
    {
        var link = CreateSyncLink();
        _db.VaultSyncLinks.Add(link);
        await _db.SaveChangesAsync();

        var status = await _orchestrator.GetStatusAsync(LocalVaultId);

        Assert.NotNull(status);
        Assert.Equal("Unknown", status.LocalVaultName);
    }

    // --- ListLinksAsync ---

    [Fact]
    public async Task ListLinksAsync_ReturnsEmpty_WhenNoLinks()
    {
        var links = await _orchestrator.ListLinksAsync();
        Assert.Empty(links);
    }

    [Fact]
    public async Task ListLinksAsync_ReturnsAllLinks()
    {
        var vault1Id = Guid.NewGuid();
        var vault2Id = Guid.NewGuid();

        _db.VaultSyncLinks.Add(CreateSyncLink(vault1Id));
        _db.VaultSyncLinks.Add(CreateSyncLink(vault2Id));
        _db.Vaults.Add(new Knowz.Core.Entities.Vault { Id = vault1Id, TenantId = TenantId, Name = "Vault1" });
        _db.Vaults.Add(new Knowz.Core.Entities.Vault { Id = vault2Id, TenantId = TenantId, Name = "Vault2" });
        await _db.SaveChangesAsync();

        var links = await _orchestrator.ListLinksAsync();

        Assert.Equal(2, links.Count);
    }

    // --- EstablishLinkAsync ---

    [Fact]
    public async Task EstablishLinkAsync_CreatesLink()
    {
        _db.Vaults.Add(new Knowz.Core.Entities.Vault
        {
            Id = LocalVaultId, TenantId = TenantId, Name = "TestVault"
        });
        await _db.SaveChangesAsync();

        _platformClient.RegisterPartnerAsync(
            Arg.Any<VaultSyncLink>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var request = new EstablishSyncLinkRequest
        {
            LocalVaultId = LocalVaultId,
            RemoteVaultId = RemoteVaultId,
            PlatformApiUrl = "https://api.knowz.io",
            ApiKey = "test-key",
        };

        var status = await _orchestrator.EstablishLinkAsync(request);

        Assert.NotNull(status);
        Assert.Equal(LocalVaultId, status.LocalVaultId);
        Assert.Equal("TestVault", status.LocalVaultName);
        Assert.Equal(RemoteVaultId, status.RemoteVaultId);

        var savedLink = await _db.VaultSyncLinks.FirstOrDefaultAsync(l => l.LocalVaultId == LocalVaultId);
        Assert.NotNull(savedLink);
        Assert.Equal("https://api.knowz.io", savedLink.PlatformApiUrl);
    }

    [Fact]
    public async Task EstablishLinkAsync_SucceedsEvenIfRegistrationFails()
    {
        _db.Vaults.Add(new Knowz.Core.Entities.Vault
        {
            Id = LocalVaultId, TenantId = TenantId, Name = "TestVault"
        });
        await _db.SaveChangesAsync();

        _platformClient.RegisterPartnerAsync(
            Arg.Any<VaultSyncLink>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Platform unreachable"));

        var request = new EstablishSyncLinkRequest
        {
            LocalVaultId = LocalVaultId,
            RemoteVaultId = RemoteVaultId,
            PlatformApiUrl = "https://api.knowz.io",
            ApiKey = "test-key",
        };

        // Should still succeed — registration failure is non-fatal
        var status = await _orchestrator.EstablishLinkAsync(request);
        Assert.NotNull(status);

        var savedLink = await _db.VaultSyncLinks.FirstOrDefaultAsync(l => l.LocalVaultId == LocalVaultId);
        Assert.NotNull(savedLink);
    }

    [Fact]
    public async Task EstablishLinkAsync_ThrowsWhenVaultNotFound()
    {
        var request = new EstablishSyncLinkRequest { LocalVaultId = Guid.NewGuid() };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _orchestrator.EstablishLinkAsync(request));
    }

    [Fact]
    public async Task EstablishLinkAsync_ThrowsWhenDuplicate()
    {
        _db.Vaults.Add(new Knowz.Core.Entities.Vault
        {
            Id = LocalVaultId, TenantId = TenantId, Name = "TestVault"
        });
        _db.VaultSyncLinks.Add(CreateSyncLink());
        await _db.SaveChangesAsync();

        var request = new EstablishSyncLinkRequest
        {
            LocalVaultId = LocalVaultId,
            RemoteVaultId = Guid.NewGuid(),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _orchestrator.EstablishLinkAsync(request));
    }

    // --- RemoveLinkAsync ---

    [Fact]
    public async Task RemoveLinkAsync_ReturnsTrue_WhenExists()
    {
        var link = CreateSyncLink();
        _db.VaultSyncLinks.Add(link);
        await _db.SaveChangesAsync();

        var removed = await _orchestrator.RemoveLinkAsync(LocalVaultId);
        Assert.True(removed);

        var remaining = await _db.VaultSyncLinks.CountAsync();
        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task RemoveLinkAsync_ReturnsFalse_WhenNotFound()
    {
        var removed = await _orchestrator.RemoveLinkAsync(Guid.NewGuid());
        Assert.False(removed);
    }

    [Fact]
    public async Task RemoveLinkAsync_RemovesAssociatedTombstones()
    {
        var link = CreateSyncLink();
        _db.VaultSyncLinks.Add(link);
        _db.SyncTombstones.Add(new SyncTombstone
        {
            VaultSyncLinkId = link.Id,
            EntityType = "Knowledge",
            LocalEntityId = Guid.NewGuid(),
            DeletedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        await _orchestrator.RemoveLinkAsync(LocalVaultId);

        var tombstones = await _db.SyncTombstones.CountAsync();
        Assert.Equal(0, tombstones);
    }

    // --- Helpers ---

    private static VaultSyncLink CreateSyncLink(Guid? vaultId = null) => new()
    {
        LocalVaultId = vaultId ?? LocalVaultId,
        RemoteVaultId = RemoteVaultId,
        RemoteTenantId = Guid.NewGuid(),
        PlatformApiUrl = "https://api.knowz.io",
        ApiKeyEncrypted = "test-key",
        SyncEnabled = true,
        LastSyncStatus = VaultSyncStatus.NotSynced,
    };
}
