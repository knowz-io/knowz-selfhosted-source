using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// Tests for NodeID PlatformSyncItemOps — single-item pull/push, rate limiting,
/// and the 100-item bulk cap (V-SEC-09, V-SEC-11, V-SEC-12).
/// </summary>
public class PlatformSyncItemOpsTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid LocalVaultId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid RemoteVaultId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid LinkId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid KnowledgeId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    private readonly SelfHostedDbContext _db;
    private readonly IPlatformSyncClient _platformClient;
    private readonly VaultScopedExportService _exportService;
    private readonly FileSyncService _fileSyncService;
    private readonly IPortableImportService _importService;
    private readonly PlatformSyncRateLimiter _rateLimiter;
    private readonly VaultSyncOrchestrator _orchestrator;

    public PlatformSyncItemOpsTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);

        _platformClient = Substitute.For<IPlatformSyncClient>();
        _importService = Substitute.For<IPortableImportService>();
        _exportService = null!;  // Mocked methods don't touch this in pull tests
        _fileSyncService = null!;

        _rateLimiter = new PlatformSyncRateLimiter(
            Substitute.For<ILogger<PlatformSyncRateLimiter>>());

        _orchestrator = new VaultSyncOrchestrator(
            _db, tenantProvider, _platformClient,
            _exportService!, _fileSyncService!,
            Substitute.For<ILogger<VaultSyncOrchestrator>>(),
            auditLog: null,
            importService: _importService,
            rateLimiter: _rateLimiter);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // ----- V-SEC-11: Pull conflict strategy -----

    [Fact]
    public async Task SyncItemAsync_Pull_Skip_PreservesLocalWhenConflict()
    {
        SeedLink();
        SeedExistingKnowledge();
        StubPlatformReturnsKnowledgeItem();

        var result = await _orchestrator.SyncItemAsync(
            LinkId, KnowledgeId, SyncItemDirection.Pull, overwriteLocal: false);

        Assert.True(result.Success);
        Assert.Equal(SyncItemOutcome.Skipped, result.Outcome);
        // PortableImportService must NOT have been called when we skipped.
        await _importService.DidNotReceive().ImportAsync(
            Arg.Any<PortableExportPackage>(),
            Arg.Any<ImportConflictStrategy>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncItemAsync_Pull_Overwrite_UpdatesLocal()
    {
        SeedLink();
        SeedExistingKnowledge();
        StubPlatformReturnsKnowledgeItem();
        _importService.ImportAsync(
            Arg.Any<PortableExportPackage>(),
            Arg.Any<ImportConflictStrategy>(),
            Arg.Any<CancellationToken>())
            .Returns(new PortableImportResult { Success = true });

        var result = await _orchestrator.SyncItemAsync(
            LinkId, KnowledgeId, SyncItemDirection.Pull, overwriteLocal: true);

        Assert.True(result.Success);
        Assert.Equal(SyncItemOutcome.Updated, result.Outcome);
        await _importService.Received(1).ImportAsync(
            Arg.Any<PortableExportPackage>(),
            ImportConflictStrategy.Overwrite,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncItemAsync_Pull_NewItem_CreatesLocal()
    {
        SeedLink();
        // No existing local item.
        StubPlatformReturnsKnowledgeItem();
        _importService.ImportAsync(
            Arg.Any<PortableExportPackage>(),
            Arg.Any<ImportConflictStrategy>(),
            Arg.Any<CancellationToken>())
            .Returns(new PortableImportResult { Success = true });

        var result = await _orchestrator.SyncItemAsync(
            LinkId, KnowledgeId, SyncItemDirection.Pull);

        Assert.True(result.Success);
        Assert.Equal(SyncItemOutcome.Created, result.Outcome);
    }

    // ----- V-SEC-12: Platform-supplied id validation -----

    [Fact]
    public async Task SyncItemAsync_Pull_PlatformReturnsWrongId_ReturnsInvalidData()
    {
        SeedLink();
        var package = MakePackage(Guid.NewGuid());  // Different id
        _platformClient.ExportItemAsync(
            Arg.Any<VaultSyncLink>(), KnowledgeId, Arg.Any<CancellationToken>())
            .Returns(package);

        var result = await _orchestrator.SyncItemAsync(
            LinkId, KnowledgeId, SyncItemDirection.Pull);

        Assert.False(result.Success);
        Assert.Equal(SyncItemOutcome.Failed, result.Outcome);
        Assert.Contains("invalid data", result.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SyncItemAsync_EmptyKnowledgeId_RejectedAtServiceBoundary()
    {
        SeedLink();
        var result = await _orchestrator.SyncItemAsync(
            LinkId, Guid.Empty, SyncItemDirection.Pull);

        Assert.False(result.Success);
        Assert.Equal(SyncItemOutcome.Failed, result.Outcome);
    }

    // ----- V-SEC-09: Rate limiter -----

    [Fact]
    public async Task RateLimiter_Enforces10RunsPerHour()
    {
        // Record 10 ops for the tenant, completing each one to free the concurrency slot
        // so the 11th request reaches the hourly quota check rather than tripping concurrency.
        for (int i = 0; i < 10; i++)
        {
            var opId = await _rateLimiter.RecordOperationAsync(TenantId, $"test-{i}");
            await _rateLimiter.CompleteOperationAsync(opId);
        }

        var decision = await _rateLimiter.CheckAsync(TenantId, itemCount: 1);

        Assert.False(decision.Allowed);
        Assert.Equal(RateLimitReason.HourlyQuotaExceeded, decision.Reason);
        Assert.NotNull(decision.RetryAfter);
    }

    [Fact]
    public async Task RateLimiter_Enforces1ConcurrentRunPerTenant()
    {
        var opId = await _rateLimiter.RecordOperationAsync(TenantId, "long-running");

        var decision = await _rateLimiter.CheckAsync(TenantId, itemCount: 1);

        Assert.False(decision.Allowed);
        Assert.Equal(RateLimitReason.ConcurrentRunInProgress, decision.Reason);

        await _rateLimiter.CompleteOperationAsync(opId);
        // Counter still has the recorded op so the hourly window still counts it,
        // but concurrency should now be free.
        var afterComplete = await _rateLimiter.CheckAsync(TenantId, itemCount: 1);
        Assert.True(afterComplete.Allowed);
    }

    [Fact]
    public async Task RateLimiter_TenantsAreIsolated()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        for (int i = 0; i < 10; i++)
            await _rateLimiter.RecordOperationAsync(tenantA, "fill");

        var aDecision = await _rateLimiter.CheckAsync(tenantA, 1);
        var bDecision = await _rateLimiter.CheckAsync(tenantB, 1);

        Assert.False(aDecision.Allowed);
        Assert.True(bDecision.Allowed);
    }

    [Fact]
    public async Task RateLimiter_RejectsItemCountAbove100()
    {
        var decision = await _rateLimiter.CheckAsync(TenantId, itemCount: 101);

        Assert.False(decision.Allowed);
        Assert.Equal(RateLimitReason.ItemLimitExceeded, decision.Reason);
    }

    // ----- Helpers -----

    private void SeedLink()
    {
        _db.Vaults.Add(new Vault { Id = LocalVaultId, TenantId = TenantId, Name = "Test" });
        _db.VaultSyncLinks.Add(new VaultSyncLink
        {
            Id = LinkId,
            LocalVaultId = LocalVaultId,
            RemoteVaultId = RemoteVaultId,
            RemoteTenantId = Guid.NewGuid(),
            PlatformApiUrl = "https://api.knowz.io",
            ApiKeyEncrypted = "test",
            SyncEnabled = true,
            LastSyncStatus = VaultSyncStatus.NotSynced,
        });
        _db.SaveChanges();
    }

    private void SeedExistingKnowledge()
    {
        _db.KnowledgeItems.Add(new Knowledge
        {
            Id = KnowledgeId,
            TenantId = TenantId,
            Title = "Existing",
            Content = "Local content",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow.AddDays(-1),
        });
        _db.KnowledgeVaults.Add(new KnowledgeVault
        {
            KnowledgeId = KnowledgeId,
            VaultId = LocalVaultId,
            IsPrimary = true,
        });
        _db.SaveChanges();
    }

    private void StubPlatformReturnsKnowledgeItem()
    {
        _platformClient.ExportItemAsync(
            Arg.Any<VaultSyncLink>(), KnowledgeId, Arg.Any<CancellationToken>())
            .Returns(MakePackage(KnowledgeId));
    }

    private static PortableExportPackage MakePackage(Guid knowledgeId)
    {
        return new PortableExportPackage
        {
            SchemaVersion = Knowz.Core.Schema.CoreSchema.Version,
            SourceEdition = "platform",
            ExportedAt = DateTime.UtcNow,
            Metadata = new PortableExportMetadata { TotalKnowledgeItems = 1 },
            Data = new PortableExportData
            {
                KnowledgeItems = new List<PortableKnowledge>
                {
                    new()
                    {
                        Id = knowledgeId,
                        Title = "Remote",
                        Content = "Remote content",
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    }
                }
            }
        };
    }
}
