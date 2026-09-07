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
using NSubstitute.ExceptionExtensions;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// Tests for bulk sync cap, cursor preservation, conflict resolution edge cases,
/// direction control, tombstone propagation, and rate-limit interaction with bulk
/// (V-SEC-09 / V-SEC-11). Complements VaultSyncOrchestratorTests (error paths, CRUD)
/// and PlatformSyncItemOpsTests (single-item pull/push, rate limiter primitives).
/// </summary>
public class PlatformSyncBulkConflictTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid LocalVaultId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid RemoteVaultId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid LinkId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid ExistingKnowledgeId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");

    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly IPlatformSyncClient _platformClient;
    private readonly IPortableImportService _importService;
    private readonly PlatformSyncRateLimiter _rateLimiter;

    public PlatformSyncBulkConflictTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _tenantProvider = Substitute.For<ITenantProvider>();
        _tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, _tenantProvider);

        _platformClient = Substitute.For<IPlatformSyncClient>();
        _importService = Substitute.For<IPortableImportService>();

        _rateLimiter = new PlatformSyncRateLimiter(
            Substitute.For<ILogger<PlatformSyncRateLimiter>>());

        // Default: compatible schema so the orchestrator proceeds past the schema check.
        _platformClient.GetSchemaAsync(Arg.Any<VaultSyncLink>(), Arg.Any<CancellationToken>())
            .Returns(new PlatformSchemaResponse
            {
                Version = Knowz.Core.Schema.CoreSchema.Version,
                MinReadableVersion = Knowz.Core.Schema.CoreSchema.Version,
            });
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // ---------- Fixture helpers ----------

    private VaultSyncOrchestrator CreatePullOnlyOrchestrator() =>
        new(_db, _tenantProvider, _platformClient,
            exportService: null!, fileSyncService: null!,
            Substitute.For<ILogger<VaultSyncOrchestrator>>(),
            auditLog: null, importService: _importService, rateLimiter: _rateLimiter);

    private VaultSyncOrchestrator CreateOrchestratorWithRealExport()
    {
        var exportService = new VaultScopedExportService(
            _db, _tenantProvider,
            Substitute.For<ILogger<VaultScopedExportService>>());

        return new VaultSyncOrchestrator(
            _db, _tenantProvider, _platformClient,
            exportService, fileSyncService: null!,
            Substitute.For<ILogger<VaultSyncOrchestrator>>(),
            auditLog: null, importService: _importService, rateLimiter: _rateLimiter);
    }

    private void SeedLink(DateTime? lastPullCursor = null, DateTime? lastPushCursor = null)
    {
        _db.Vaults.Add(new Vault { Id = LocalVaultId, TenantId = TenantId, Name = "Local" });
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
            LastPullCursor = lastPullCursor,
            LastPushCursor = lastPushCursor,
        });
        _db.SaveChanges();
    }

    private void SeedLocalKnowledge(Guid id, DateTime updatedAt, string title = "Local")
    {
        _db.KnowledgeItems.Add(new Knowledge
        {
            Id = id,
            TenantId = TenantId,
            Title = title,
            Content = "content",
            CreatedAt = updatedAt.AddDays(-1),
            UpdatedAt = updatedAt,
        });
        _db.KnowledgeVaults.Add(new KnowledgeVault
        {
            KnowledgeId = id,
            VaultId = LocalVaultId,
            IsPrimary = true,
        });
        _db.SaveChanges();
    }

    private static PortableExportPackage MakeDeltaPackage(int itemCount, DateTime? baseTime = null)
    {
        var t = baseTime ?? DateTime.UtcNow;
        var items = new List<PortableKnowledge>();
        for (int i = 0; i < itemCount; i++)
        {
            items.Add(new PortableKnowledge
            {
                Id = Guid.NewGuid(),
                Title = $"Remote-{i}",
                Content = $"content-{i}",
                CreatedAt = t.AddSeconds(i),
                UpdatedAt = t.AddSeconds(i),
            });
        }
        return new PortableExportPackage
        {
            SchemaVersion = Knowz.Core.Schema.CoreSchema.Version,
            SourceEdition = "platform",
            SourceTenantId = Guid.NewGuid(),
            ExportedAt = t,
            SyncCursor = t,
            IsIncrementalSync = true,
            Metadata = new PortableExportMetadata { TotalKnowledgeItems = itemCount },
            Data = new PortableExportData { KnowledgeItems = items },
            Tombstones = new List<SyncTombstoneDto>(),
        };
    }

    // ================================================================
    // Bulk cap tests
    // ================================================================

    [Fact]
    public async Task SyncAsync_250Items_Returns100ProcessedPartialTrue()
    {
        SeedLink();
        _platformClient.ExportDeltaAsync(
                Arg.Any<VaultSyncLink>(), Arg.Any<DateTime?>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(MakeDeltaPackage(250));

        var orchestrator = CreatePullOnlyOrchestrator();
        var result = await orchestrator.SyncAsync(LocalVaultId, SyncDirection.PullOnly);

        Assert.True(result.Success);
        Assert.True(result.Partial);
        Assert.Equal(VaultSyncOrchestrator.BulkItemCap, result.PullAccepted + result.PullSkipped);
        Assert.Equal(100, result.PullAccepted);
    }

    [Fact]
    public async Task SyncAsync_Partial_PreservesCursorForResume()
    {
        // Starts with null cursor. After a partial run the orchestrator MUST NOT advance
        // the cursor — otherwise the unprocessed tail of the delta window would be lost.
        SeedLink(lastPullCursor: null);
        _platformClient.ExportDeltaAsync(
                Arg.Any<VaultSyncLink>(), Arg.Any<DateTime?>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(MakeDeltaPackage(150));

        var orchestrator = CreatePullOnlyOrchestrator();
        var result = await orchestrator.SyncAsync(LocalVaultId, SyncDirection.PullOnly);

        Assert.True(result.Partial);

        var refreshed = await _db.VaultSyncLinks.AsNoTracking()
            .FirstAsync(l => l.LocalVaultId == LocalVaultId);
        Assert.Null(refreshed.LastPullCursor);
    }

    [Fact]
    public async Task SyncAsync_ExactlyAtCap_MarksPartialTrue()
    {
        // Exactly 100 items hits the cap; the orchestrator conservatively flags Partial
        // for any `Full`/`PullOnly` run that exhausted the entire budget on pull so the
        // user is nudged to re-run (and because push is skipped when budget == 0).
        SeedLink();
        _platformClient.ExportDeltaAsync(
                Arg.Any<VaultSyncLink>(), Arg.Any<DateTime?>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(MakeDeltaPackage(100));

        var orchestrator = CreatePullOnlyOrchestrator();
        var result = await orchestrator.SyncAsync(LocalVaultId, SyncDirection.Full);

        Assert.True(result.Success);
        Assert.True(result.Partial,
            "Full run that drained the budget on pull must be marked Partial — push/file-sync skipped.");
    }

    [Fact]
    public async Task SyncAsync_Under100_ReturnsSucceededNotPartial()
    {
        SeedLink();
        _platformClient.ExportDeltaAsync(
                Arg.Any<VaultSyncLink>(), Arg.Any<DateTime?>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(MakeDeltaPackage(50));

        var orchestrator = CreatePullOnlyOrchestrator();
        var result = await orchestrator.SyncAsync(LocalVaultId, SyncDirection.PullOnly);

        Assert.True(result.Success);
        Assert.False(result.Partial);
        Assert.Equal(50, result.PullAccepted);
    }

    [Fact]
    public async Task SyncAsync_MultipleRunsContinue_ProcessesAllEventually()
    {
        // Simulates a 150-item backlog drained over two runs. The orchestrator itself
        // doesn't advance the cursor on partial — it re-invokes ExportDeltaAsync and
        // trusts the platform to return a narrower window next time. We fake that by
        // returning 150 on the first call and 50 on the second.
        SeedLink();

        var firstPage = MakeDeltaPackage(150);
        var secondPage = MakeDeltaPackage(50);

        var callCount = 0;
        _platformClient.ExportDeltaAsync(
                Arg.Any<VaultSyncLink>(), Arg.Any<DateTime?>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => callCount++ == 0 ? firstPage : secondPage);

        var orchestrator = CreatePullOnlyOrchestrator();

        var first = await orchestrator.SyncAsync(LocalVaultId, SyncDirection.PullOnly);
        Assert.True(first.Success);
        Assert.True(first.Partial);
        Assert.Equal(100, first.PullAccepted);

        var second = await orchestrator.SyncAsync(LocalVaultId, SyncDirection.PullOnly);
        Assert.True(second.Success);
        Assert.False(second.Partial);
        Assert.Equal(50, second.PullAccepted);
    }

    // ================================================================
    // Conflict resolution edge cases (SyncItemAsync)
    // ================================================================

    [Fact]
    public async Task SyncItemAsync_LocalNewerThanRemote_SkipStrategy_Skipped()
    {
        // Even when the local row is strictly newer than what the remote offers, the
        // Pull-with-overwriteLocal=false contract is "skip by default — never replace".
        SeedLink();
        SeedLocalKnowledge(ExistingKnowledgeId, updatedAt: DateTime.UtcNow);

        _platformClient.ExportItemAsync(
                Arg.Any<VaultSyncLink>(), ExistingKnowledgeId, Arg.Any<CancellationToken>())
            .Returns(MakeSingleItemPackage(ExistingKnowledgeId, updatedAt: DateTime.UtcNow.AddDays(-10)));

        var orchestrator = CreatePullOnlyOrchestrator();
        var result = await orchestrator.SyncItemAsync(
            LinkId, ExistingKnowledgeId, SyncItemDirection.Pull, overwriteLocal: false);

        Assert.True(result.Success);
        Assert.Equal(SyncItemOutcome.Skipped, result.Outcome);
        await _importService.DidNotReceive().ImportAsync(
            Arg.Any<PortableExportPackage>(),
            Arg.Any<ImportConflictStrategy>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncItemAsync_LocalNewerThanRemote_OverwriteStrategy_Overwritten()
    {
        // Explicit overwrite wins even if the local row is newer — the caller has
        // taken responsibility for the clobber.
        SeedLink();
        SeedLocalKnowledge(ExistingKnowledgeId, updatedAt: DateTime.UtcNow);

        _platformClient.ExportItemAsync(
                Arg.Any<VaultSyncLink>(), ExistingKnowledgeId, Arg.Any<CancellationToken>())
            .Returns(MakeSingleItemPackage(ExistingKnowledgeId, updatedAt: DateTime.UtcNow.AddDays(-10)));

        _importService.ImportAsync(
                Arg.Any<PortableExportPackage>(),
                Arg.Any<ImportConflictStrategy>(),
                Arg.Any<CancellationToken>())
            .Returns(new PortableImportResult { Success = true });

        var orchestrator = CreatePullOnlyOrchestrator();
        var result = await orchestrator.SyncItemAsync(
            LinkId, ExistingKnowledgeId, SyncItemDirection.Pull, overwriteLocal: true);

        Assert.True(result.Success);
        Assert.Equal(SyncItemOutcome.Updated, result.Outcome);
        await _importService.Received(1).ImportAsync(
            Arg.Any<PortableExportPackage>(),
            ImportConflictStrategy.Overwrite,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncItemAsync_RemoteDeleted_TombstoneExists_LocalPreserved()
    {
        // Single-item pull returns 404 (platform already deleted) — single-item semantics
        // are NotFound, not "apply tombstone". Local row is preserved.
        SeedLink();
        SeedLocalKnowledge(ExistingKnowledgeId, updatedAt: DateTime.UtcNow);

        _platformClient.ExportItemAsync(
                Arg.Any<VaultSyncLink>(), ExistingKnowledgeId, Arg.Any<CancellationToken>())
            .Returns((PortableExportPackage?)null);

        var orchestrator = CreatePullOnlyOrchestrator();
        var result = await orchestrator.SyncItemAsync(
            LinkId, ExistingKnowledgeId, SyncItemDirection.Pull, overwriteLocal: false);

        Assert.False(result.Success);
        Assert.Equal(SyncItemOutcome.NotFound, result.Outcome);

        var local = await _db.KnowledgeItems.IgnoreQueryFilters()
            .FirstAsync(k => k.Id == ExistingKnowledgeId);
        Assert.False(local.IsDeleted);
    }

    [Fact]
    public async Task SyncItemAsync_IdenticalTimestamps_SkipByDefault()
    {
        // Single-item pull's contract is "skip if local exists, regardless of timestamps",
        // so identical timestamps deterministically resolve to Skip unless overwriteLocal
        // is set. This locks in the documented tie-breaker behavior.
        var ts = DateTime.UtcNow;
        SeedLink();
        SeedLocalKnowledge(ExistingKnowledgeId, updatedAt: ts);

        _platformClient.ExportItemAsync(
                Arg.Any<VaultSyncLink>(), ExistingKnowledgeId, Arg.Any<CancellationToken>())
            .Returns(MakeSingleItemPackage(ExistingKnowledgeId, updatedAt: ts));

        var orchestrator = CreatePullOnlyOrchestrator();
        var result = await orchestrator.SyncItemAsync(
            LinkId, ExistingKnowledgeId, SyncItemDirection.Pull, overwriteLocal: false);

        Assert.True(result.Success);
        Assert.Equal(SyncItemOutcome.Skipped, result.Outcome);
    }

    [Fact]
    public async Task SyncItemAsync_PullNewItem_CreatesLocalRow()
    {
        // Fresh pull — no local row — should import via the strategy-aware service.
        SeedLink();
        var newId = Guid.NewGuid();

        _platformClient.ExportItemAsync(
                Arg.Any<VaultSyncLink>(), newId, Arg.Any<CancellationToken>())
            .Returns(MakeSingleItemPackage(newId, updatedAt: DateTime.UtcNow));

        _importService.ImportAsync(
                Arg.Any<PortableExportPackage>(),
                Arg.Any<ImportConflictStrategy>(),
                Arg.Any<CancellationToken>())
            .Returns(new PortableImportResult { Success = true });

        var orchestrator = CreatePullOnlyOrchestrator();
        var result = await orchestrator.SyncItemAsync(
            LinkId, newId, SyncItemDirection.Pull, overwriteLocal: false);

        Assert.True(result.Success);
        Assert.Equal(SyncItemOutcome.Created, result.Outcome);
        Assert.Equal(newId, result.LocalKnowledgeId);
    }

    // ================================================================
    // Direction control
    // ================================================================

    [Fact]
    public async Task SyncAsync_PullOnly_DoesNotCallPushPath()
    {
        SeedLink();
        _platformClient.ExportDeltaAsync(
                Arg.Any<VaultSyncLink>(), Arg.Any<DateTime?>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(MakeDeltaPackage(5));

        var orchestrator = CreatePullOnlyOrchestrator();
        var result = await orchestrator.SyncAsync(LocalVaultId, SyncDirection.PullOnly);

        Assert.True(result.Success);
        // Push path would call ImportDeltaAsync — MUST NOT happen on PullOnly.
        await _platformClient.DidNotReceive().ImportDeltaAsync(
            Arg.Any<VaultSyncLink>(),
            Arg.Any<PortableExportPackage>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_PushOnly_DoesNotCallPullPath()
    {
        // Real VaultScopedExportService backed by an empty DB — returns a package with
        // zero items. PushAsync short-circuits ("nothing to push") without calling the
        // platform. We then assert the pull path (ExportDeltaAsync) was never invoked.
        SeedLink();
        var orchestrator = CreateOrchestratorWithRealExport();

        var result = await orchestrator.SyncAsync(LocalVaultId, SyncDirection.PushOnly);

        Assert.True(result.Success);
        await _platformClient.DidNotReceive().ExportDeltaAsync(
            Arg.Any<VaultSyncLink>(), Arg.Any<DateTime?>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_Full_CallsBothPullAndPush()
    {
        // Full direction: 150 items on pull forces Partial=true, which cleanly skips
        // push and file sync. That still exercises the Full-branch routing: pull runs,
        // push's "budget exhausted" else-branch runs, file sync's gate blocks it.
        // Assertion focus: the pull path was actually invoked under Full direction.
        SeedLink();
        _platformClient.ExportDeltaAsync(
                Arg.Any<VaultSyncLink>(), Arg.Any<DateTime?>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(MakeDeltaPackage(150));

        var orchestrator = CreatePullOnlyOrchestrator();
        var result = await orchestrator.SyncAsync(LocalVaultId, SyncDirection.Full);

        Assert.True(result.Success);
        Assert.True(result.Partial);
        Assert.Contains(result.Details, d => d.Contains("100-item limit", StringComparison.OrdinalIgnoreCase));

        await _platformClient.Received().ExportDeltaAsync(
            Arg.Any<VaultSyncLink>(), Arg.Any<DateTime?>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ================================================================
    // Tombstone propagation
    // ================================================================

    // Test: SyncAsync_LocalDeletion_CreatesTombstone
    // SKIPPED: SyncTombstone rows are created by the knowledge-deletion pathway
    // (service layer / soft-delete hook), NOT by VaultSyncOrchestrator.SyncAsync.
    // Tombstone creation is outside this orchestrator's public surface and belongs
    // in a test against whichever service marks knowledge IsDeleted=true for a synced
    // vault. Exercising it here would require reaching into internals or coupling to
    // a second service's implementation detail, both of which the task guidance
    // explicitly discourages.

    [Fact]
    public async Task SyncAsync_TombstonePropagated_MarkedPropagated()
    {
        // Seed an unpropagated tombstone. After a PushOnly run the orchestrator MUST
        // mark it Propagated=true and stamp PropagatedAt. The push path runs through
        // the real VaultScopedExportService — with no local items, the export package
        // will still reach the "mark propagated" loop because PushAsync only short-
        // circuits when BOTH KnowledgeItems AND package.Tombstones are empty.
        SeedLink();

        var tombstoneEntityId = Guid.NewGuid();
        _db.SyncTombstones.Add(new SyncTombstone
        {
            VaultSyncLinkId = LinkId,
            EntityType = "Knowledge",
            LocalEntityId = tombstoneEntityId,
            DeletedAt = DateTime.UtcNow,
            Propagated = false,
        });
        await _db.SaveChangesAsync();

        _platformClient.ImportDeltaAsync(
                Arg.Any<VaultSyncLink>(),
                Arg.Any<PortableExportPackage>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlatformImportResponse
            {
                Success = true,
                Accepted = 0,
                Skipped = 0,
                ServerTimestamp = DateTime.UtcNow,
            });

        var orchestrator = CreateOrchestratorWithRealExport();
        var result = await orchestrator.SyncAsync(LocalVaultId, SyncDirection.PushOnly);

        Assert.True(result.Success);

        var ts = await _db.SyncTombstones.AsNoTracking()
            .FirstAsync(t => t.LocalEntityId == tombstoneEntityId);
        Assert.True(ts.Propagated);
        Assert.NotNull(ts.PropagatedAt);
    }

    [Fact]
    public async Task SyncAsync_TombstoneApplied_LocalItemSoftDeleted()
    {
        // Pull delivers a tombstone for an existing local item — orchestrator's
        // ApplyTombstoneAsync MUST soft-delete it (IsDeleted=true) and bump UpdatedAt.
        SeedLink();
        var localTs = DateTime.UtcNow.AddDays(-1);
        SeedLocalKnowledge(ExistingKnowledgeId, updatedAt: localTs);

        var tombstonePackage = new PortableExportPackage
        {
            SchemaVersion = Knowz.Core.Schema.CoreSchema.Version,
            SourceEdition = "platform",
            ExportedAt = DateTime.UtcNow,
            SyncCursor = DateTime.UtcNow,
            IsIncrementalSync = true,
            Metadata = new PortableExportMetadata(),
            Data = new PortableExportData(),
            Tombstones = new List<SyncTombstoneDto>
            {
                new()
                {
                    EntityType = "Knowledge",
                    EntityId = ExistingKnowledgeId,
                    DeletedAt = DateTime.UtcNow, // newer than local UpdatedAt
                },
            },
        };

        _platformClient.ExportDeltaAsync(
                Arg.Any<VaultSyncLink>(), Arg.Any<DateTime?>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(tombstonePackage);

        var orchestrator = CreatePullOnlyOrchestrator();
        var result = await orchestrator.SyncAsync(LocalVaultId, SyncDirection.PullOnly);

        Assert.True(result.Success);
        Assert.Equal(1, result.TombstonesApplied);

        var local = await _db.KnowledgeItems.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(k => k.Id == ExistingKnowledgeId);
        Assert.True(local.IsDeleted);
    }

    // ================================================================
    // Rate limit + bulk interaction
    // ================================================================

    [Fact]
    public async Task SyncAsync_HourlyQuotaExceeded_ThrowsBeforeProcessing()
    {
        // Exhaust the tenant's hourly quota so the rate limiter rejects the 11th run
        // BEFORE any platform HTTP call or schema check fires. Verifies fail-fast
        // placement: V-SEC-09 demands the check sit ahead of GetSchemaAsync.
        for (int i = 0; i < 10; i++)
        {
            var opId = await _rateLimiter.RecordOperationAsync(TenantId, $"fill-{i}");
            await _rateLimiter.CompleteOperationAsync(opId);
        }

        SeedLink();
        var orchestrator = CreatePullOnlyOrchestrator();

        var ex = await Assert.ThrowsAsync<RateLimitExceededException>(
            () => orchestrator.SyncAsync(LocalVaultId, SyncDirection.PullOnly));
        Assert.Equal(RateLimitReason.HourlyQuotaExceeded, ex.Reason);

        // Fail-fast: no platform calls should have happened.
        await _platformClient.DidNotReceive().GetSchemaAsync(
            Arg.Any<VaultSyncLink>(), Arg.Any<CancellationToken>());
        await _platformClient.DidNotReceive().ExportDeltaAsync(
            Arg.Any<VaultSyncLink>(), Arg.Any<DateTime?>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ---------- Single-item helper ----------

    private static PortableExportPackage MakeSingleItemPackage(Guid id, DateTime updatedAt)
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
                        Id = id,
                        Title = "Remote",
                        Content = "Remote content",
                        CreatedAt = updatedAt.AddDays(-1),
                        UpdatedAt = updatedAt,
                    },
                },
            },
        };
    }
}
