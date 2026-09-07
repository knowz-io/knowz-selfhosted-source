namespace Knowz.SelfHosted.Application.Services;

using System.Diagnostics;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.Core.Schema;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Orchestrates bidirectional vault sync between selfhosted and platform.
/// Flow: Pull (remote → local) → Push (local → remote) → Update cursors.
/// Selfhosted always initiates; platform is passive.
/// </summary>
public class VaultSyncOrchestrator : IVaultSyncOrchestrator
{
    /// <summary>
    /// Hard cap on the number of knowledge items a single SyncAsync run will process (V-SEC-09).
    /// When exceeded, the run completes with <see cref="VaultSyncResult.Partial"/>=true and
    /// the user must re-run to continue from the cursor.
    /// </summary>
    public const int BulkItemCap = 100;

    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly IPlatformSyncClient _platformClient;
    private readonly VaultScopedExportService _exportService;
    private readonly FileSyncService _fileSyncService;
    private readonly IPlatformAuditLog? _auditLog;
    private readonly IPortableImportService? _importService;
    private readonly IPlatformSyncRateLimiter? _rateLimiter;
    private readonly IPlatformConnectionService? _connectionService;
    private readonly ILogger<VaultSyncOrchestrator> _logger;

    public VaultSyncOrchestrator(
        SelfHostedDbContext db,
        ITenantProvider tenantProvider,
        IPlatformSyncClient platformClient,
        VaultScopedExportService exportService,
        FileSyncService fileSyncService,
        ILogger<VaultSyncOrchestrator> logger,
        IPlatformAuditLog? auditLog = null,
        IPortableImportService? importService = null,
        IPlatformSyncRateLimiter? rateLimiter = null,
        IPlatformConnectionService? connectionService = null)
    {
        _db = db;
        _tenantProvider = tenantProvider;
        _platformClient = platformClient;
        _exportService = exportService;
        _fileSyncService = fileSyncService;
        _auditLog = auditLog;
        _importService = importService;
        _rateLimiter = rateLimiter;
        _connectionService = connectionService;
        _logger = logger;
    }

    public async Task<VaultSyncResult> SyncAsync(
        Guid localVaultId, SyncDirection direction = SyncDirection.Full,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new VaultSyncResult { Direction = direction };

        // Find sync link
        var link = await _db.VaultSyncLinks
            .FirstOrDefaultAsync(l => l.LocalVaultId == localVaultId, ct);

        if (link == null)
        {
            result.Success = false;
            result.Error = $"No sync link found for vault {localVaultId}";
            result.Duration = sw.Elapsed;
            return result;
        }

        if (!link.SyncEnabled)
        {
            result.Success = false;
            result.Error = "Sync is disabled for this vault";
            result.Duration = sw.Elapsed;
            return result;
        }

        // Prevent concurrent sync
        if (link.LastSyncStatus == VaultSyncStatus.InProgress)
        {
            result.Success = false;
            result.Error = "Sync is already in progress";
            result.Duration = sw.Elapsed;
            return result;
        }

        // V-SEC-09: rate limit check BEFORE any platform HTTP call (cheap fail-fast).
        Guid? rateLimitOpId = null;
        if (_rateLimiter != null)
        {
            var decision = await _rateLimiter.CheckAsync(_tenantProvider.TenantId, itemCount: BulkItemCap, ct);
            if (!decision.Allowed)
            {
                result.Success = false;
                result.Error = decision.Reason switch
                {
                    RateLimitReason.HourlyQuotaExceeded => "Rate limit exceeded (10 runs per hour).",
                    RateLimitReason.ConcurrentRunInProgress => "Another sync is already in progress for this tenant.",
                    RateLimitReason.ItemLimitExceeded => "Run exceeds the 100-item-per-run limit.",
                    _ => "Rate limit exceeded.",
                };
                result.Duration = sw.Elapsed;
                throw new RateLimitExceededException(
                    decision.Reason!.Value, decision.RetryAfter, result.Error);
            }
            rateLimitOpId = await _rateLimiter.RecordOperationAsync(
                _tenantProvider.TenantId, "SyncAsync", ct);
        }

        // V-SEC-07: begin audit row for a real platform sync run. Short-circuits above
        // (no link / disabled / already in-progress) are intentionally not audited —
        // the history tracks committed runs, not request validation failures.
        Guid? auditRunId = null;
        if (_auditLog != null)
        {
            var (auditOp, auditDir) = direction switch
            {
                SyncDirection.PullOnly => (PlatformSyncOperation.PullVault, PlatformSyncDirection.Pull),
                SyncDirection.PushOnly => (PlatformSyncOperation.PushVault, PlatformSyncDirection.Push),
                _ => (PlatformSyncOperation.PullVault, PlatformSyncDirection.Pull),
            };
            try
            {
                auditRunId = await _auditLog.StartAsync(new PlatformSyncRunStart(
                    UserId: Guid.Empty,
                    UserEmail: null,
                    Operation: auditOp,
                    Direction: auditDir,
                    VaultSyncLinkId: link.Id), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PlatformAuditLog.StartAsync failed for vault {VaultId}", localVaultId);
            }
        }

        // Mark as in progress
        link.LastSyncStatus = VaultSyncStatus.InProgress;
        link.LastSyncError = null;
        link.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        try
        {
            // Step 0: Schema compatibility check
            var schema = await _platformClient.GetSchemaAsync(link, ct);
            if (!CoreSchema.CanRead(schema.Version))
            {
                throw new InvalidOperationException(
                    $"Platform schema v{schema.Version} is not compatible. Local supports {CoreSchema.GetCompatibilityInfo()}.");
            }

            var itemBudget = BulkItemCap;

            // Step 1: Pull (remote → local)
            if (direction is SyncDirection.Full or SyncDirection.PullOnly)
            {
                var pullResult = await PullAsync(link, ct, itemBudget);
                result.PullAccepted = pullResult.Accepted;
                result.PullSkipped = pullResult.Skipped;
                result.TombstonesApplied += pullResult.TombstonesApplied;
                result.Details.AddRange(pullResult.Details);
                itemBudget -= pullResult.Accepted + pullResult.Skipped;
                if (pullResult.HitItemCap)
                    result.Partial = true;
            }

            // Step 2: Push (local → remote). Skip when budget already exhausted.
            if (direction is SyncDirection.Full or SyncDirection.PushOnly && itemBudget > 0)
            {
                var pushResult = await PushAsync(link, ct, itemBudget);
                result.PushAccepted = pushResult.Accepted;
                result.PushSkipped = pushResult.Skipped;
                result.Details.AddRange(pushResult.Details);
                if (pushResult.HitItemCap)
                    result.Partial = true;
            }
            else if (direction is SyncDirection.Full or SyncDirection.PushOnly)
            {
                result.Partial = true;
                result.Details.Add("Push skipped — 100-item limit reached during pull. Re-run to continue.");
            }

            // Step 3: File sync (after entity sync). Skipped on partial runs to keep run bounded.
            if (direction == SyncDirection.Full && !result.Partial)
            {
                var fileResult = await _fileSyncService.SyncFilesAsync(link, ct);
                result.Details.Add($"Files: {fileResult.Downloaded} downloaded, {fileResult.Uploaded} uploaded, {fileResult.Skipped} skipped");
                if (fileResult.Errors.Count > 0)
                    result.Details.AddRange(fileResult.Errors.Select(e => $"File error: {e}"));
            }

            if (result.Partial)
                result.Details.Add($"Reached {BulkItemCap}-item limit — run again to continue.");

            // Step 4: Finalize
            link.LastSyncStatus = VaultSyncStatus.Succeeded;
            link.LastSyncCompletedAt = DateTime.UtcNow;
            link.LastSyncError = null;
            link.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            result.Success = true;
            _logger.LogInformation(
                "Vault sync completed for {VaultId}: pull={PullAccepted}/{PullSkipped}, push={PushAccepted}/{PushSkipped}",
                localVaultId, result.PullAccepted, result.PullSkipped, result.PushAccepted, result.PushSkipped);

            if (_auditLog != null && auditRunId.HasValue)
            {
                try
                {
                    var total = result.PullAccepted + result.PullSkipped + result.PushAccepted + result.PushSkipped;
                    await _auditLog.CompleteAsync(
                        auditRunId.Value,
                        new PlatformSyncRunResult(
                            ItemCount: total,
                            BytesTransferred: 0,
                            Status: PlatformSyncRunStatus.Succeeded),
                        ct);
                }
                catch (Exception auditEx)
                {
                    _logger.LogWarning(auditEx, "PlatformAuditLog.CompleteAsync failed for run {RunId}", auditRunId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vault sync failed for {VaultId}", localVaultId);

            link.LastSyncStatus = VaultSyncStatus.Failed;
            link.LastSyncError = ex.Message;
            link.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            result.Success = false;
            result.Error = ex.Message;

            if (_auditLog != null && auditRunId.HasValue)
            {
                try
                {
                    await _auditLog.FailAsync(auditRunId.Value, ex.Message, ct);
                }
                catch (Exception auditEx)
                {
                    _logger.LogWarning(auditEx, "PlatformAuditLog.FailAsync failed for run {RunId}", auditRunId);
                }
            }
        }
        finally
        {
            if (_rateLimiter != null && rateLimitOpId.HasValue)
                await _rateLimiter.CompleteOperationAsync(rateLimitOpId.Value, ct);
        }

        result.Duration = sw.Elapsed;
        return result;
    }

    private async Task<SyncDeltaImportResult> PullAsync(
        VaultSyncLink link, CancellationToken ct, int itemBudget = int.MaxValue)
    {
        var totalResult = new SyncDeltaImportResult { Success = true };
        var page = 1;
        DateTime? newCursor = null;

        // Paginated pull
        while (true)
        {
            if (itemBudget <= 0)
            {
                totalResult.HitItemCap = true;
                break;
            }

            var package = await _platformClient.ExportDeltaAsync(link, link.LastPullCursor, page, 500, ct);

            if (package.Data.KnowledgeItems.Count == 0 &&
                (package.Tombstones == null || package.Tombstones.Count == 0) &&
                page > 1)
                break;

            // Apply remaining budget — truncate so import never exceeds the cap.
            if (package.Data.KnowledgeItems.Count > itemBudget)
            {
                package.Data.KnowledgeItems = package.Data.KnowledgeItems.Take(itemBudget).ToList();
                totalResult.HitItemCap = true;
            }

            // Import using last-write-wins
            var importResult = await ImportSyncDeltaLocallyAsync(package, link.LocalVaultId, ct);
            totalResult.Accepted += importResult.Accepted;
            totalResult.Skipped += importResult.Skipped;
            totalResult.TombstonesApplied += importResult.TombstonesApplied;
            totalResult.Details.AddRange(importResult.Details);
            itemBudget -= (importResult.Accepted + importResult.Skipped);

            // Track cursor from first page
            if (page == 1 && package.SyncCursor.HasValue)
                newCursor = package.SyncCursor;

            // If we got fewer items than page size, we're done
            if (package.Data.KnowledgeItems.Count < 500)
                break;

            if (totalResult.HitItemCap)
                break;

            page++;
        }

        // Update pull cursor only when a full pass completed (no partial truncation).
        // A partial run keeps the previous cursor so the next invocation re-pulls the
        // items that were skipped.
        if (newCursor.HasValue && !totalResult.HitItemCap)
        {
            link.LastPullCursor = newCursor;
            await _db.SaveChangesAsync(ct);
        }

        totalResult.Details.Add($"Pull complete: {totalResult.Accepted} accepted, {totalResult.Skipped} skipped");
        return totalResult;
    }

    private async Task<SyncDeltaImportResult> PushAsync(
        VaultSyncLink link, CancellationToken ct, int itemBudget = int.MaxValue)
    {
        var result = new SyncDeltaImportResult { Success = true };

        // Export local delta
        var package = await _exportService.ExportDeltaAsync(link.LocalVaultId, link.LastPushCursor, link, ct);

        if (package.Data.KnowledgeItems.Count == 0 &&
            (package.Tombstones == null || package.Tombstones.Count == 0))
        {
            result.Details.Add("Push: nothing to push");
            return result;
        }

        // V-SEC-09: apply remaining item budget. Truncate and mark Partial so the caller
        // leaves the cursor where the remaining items can be picked up on the next run.
        if (package.Data.KnowledgeItems.Count > itemBudget)
        {
            package.Data.KnowledgeItems = package.Data.KnowledgeItems.Take(itemBudget).ToList();
            package.Metadata.TotalKnowledgeItems = package.Data.KnowledgeItems.Count;
            result.HitItemCap = true;
        }

        // Push to platform
        var platformResponse = await _platformClient.ImportDeltaAsync(link, package, ct);
        result.Accepted = platformResponse.Accepted;
        result.Skipped = platformResponse.Skipped;

        // Mark tombstones as propagated
        var unpropagated = await _db.SyncTombstones
            .Where(st => st.VaultSyncLinkId == link.Id && !st.Propagated)
            .ToListAsync(ct);

        foreach (var st in unpropagated)
        {
            st.Propagated = true;
            st.PropagatedAt = DateTime.UtcNow;
        }

        // Update push cursor only when a full pass completed. A partial run keeps the
        // previous cursor so the next invocation re-exports the remaining items.
        if (!result.HitItemCap)
        {
            link.LastPushCursor = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        result.Details.Add($"Push complete: {result.Accepted} accepted, {result.Skipped} skipped");
        return result;
    }

    /// <summary>
    /// Import a sync delta package using last-write-wins conflict resolution.
    /// </summary>
    private async Task<SyncDeltaImportResult> ImportSyncDeltaLocallyAsync(
        global::Knowz.Core.Portability.PortableExportPackage package, Guid localVaultId, CancellationToken ct)
    {
        var result = new SyncDeltaImportResult { Success = true };
        var tenantId = _tenantProvider.TenantId;

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // Import reference entities (last-write-wins)
            result.Accepted += await SyncEntities(package.Data.Topics, _db.Topics, tenantId,
                (pt, existing) => { existing.Name = pt.Name; existing.Description = pt.Description; }, ct);
            result.Accepted += await SyncEntities(package.Data.Tags, _db.Tags, tenantId,
                (pt, existing) => { existing.Name = pt.Name; }, ct);
            result.Accepted += await SyncEntities(package.Data.Persons, _db.Persons, tenantId,
                (pp, existing) => { existing.Name = pp.Name; }, ct);
            result.Accepted += await SyncEntities(package.Data.Locations, _db.Locations, tenantId,
                (pl, existing) => { existing.Name = pl.Name; }, ct);
            result.Accepted += await SyncEntities(package.Data.Events, _db.Events, tenantId,
                (pe, existing) => { existing.Name = pe.Name; }, ct);

            await _db.SaveChangesAsync(ct);

            // Import knowledge items. For Commit / CommitHistory items we dedup by
            // Source string (cross-instance-stable) rather than by Guid — NODE-5.
            // Map of pk.Id → receiver's local knowledge Id (used later when processing
            // Relationships to link sender-side source guids to receiver-side guids).
            var importedKnowledgeIdMap = new Dictionary<Guid, Guid>();

            foreach (var pk in package.Data.KnowledgeItems)
            {
                var isCommitScoped =
                    pk.Type == global::Knowz.Core.Enums.KnowledgeType.Commit
                    || pk.Type == global::Knowz.Core.Enums.KnowledgeType.CommitHistory;

                global::Knowz.Core.Entities.Knowledge? existing = null;
                if (isCommitScoped && !string.IsNullOrEmpty(pk.Source))
                {
                    existing = await _db.KnowledgeItems
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(
                            k => k.TenantId == tenantId && k.Source == pk.Source, ct);
                }
                existing ??= await _db.KnowledgeItems
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(k => k.TenantId == tenantId && k.Id == pk.Id, ct);

                if (existing != null)
                {
                    importedKnowledgeIdMap[pk.Id] = existing.Id;
                    if (pk.UpdatedAt > existing.UpdatedAt)
                    {
                        existing.Title = pk.Title;
                        existing.Content = pk.Content;
                        existing.Summary = pk.Summary;
                        existing.Source = pk.Source;
                        existing.TopicId = pk.TopicId;
                        existing.UpdatedAt = pk.UpdatedAt;
                        existing.IsDeleted = false;
                        result.Accepted++;
                    }
                    else result.Skipped++;
                }
                else
                {
                    // Commit-scoped items get a fresh receiver-side GUID because
                    // the sender's Guid is not meaningful cross-instance.
                    var localId = isCommitScoped ? Guid.NewGuid() : pk.Id;
                    importedKnowledgeIdMap[pk.Id] = localId;

                    var newItem = new global::Knowz.Core.Entities.Knowledge
                    {
                        Id = localId,
                        TenantId = tenantId,
                        Title = pk.Title,
                        Content = pk.Content,
                        Summary = pk.Summary,
                        Source = pk.Source,
                        Type = pk.Type,
                        FilePath = pk.FilePath,
                        TopicId = pk.TopicId,
                        CreatedAt = pk.CreatedAt,
                        UpdatedAt = pk.UpdatedAt,
                    };
                    _db.KnowledgeItems.Add(newItem);

                    // Create vault junction
                    var junctionExists = await _db.KnowledgeVaults
                        .IgnoreQueryFilters()
                        .AnyAsync(kv => kv.KnowledgeId == localId && kv.VaultId == localVaultId, ct);
                    if (!junctionExists)
                    {
                        _db.KnowledgeVaults.Add(new global::Knowz.Core.Entities.KnowledgeVault
                        {
                            KnowledgeId = localId,
                            VaultId = localVaultId,
                            IsPrimary = true,
                        });
                    }

                    result.Accepted++;
                }

                // Sync person junctions
                if (pk.PersonLinks != null)
                {
                    var junctionKnowledgeId = importedKnowledgeIdMap[pk.Id];
                    foreach (var link in pk.PersonLinks)
                    {
                        var exists = await _db.KnowledgePersons
                            .IgnoreQueryFilters()
                            .AnyAsync(kp => kp.KnowledgeId == junctionKnowledgeId && kp.PersonId == link.EntityId, ct);
                        if (!exists)
                        {
                            _db.KnowledgePersons.Add(new global::Knowz.Core.Entities.KnowledgePerson
                            {
                                KnowledgeId = junctionKnowledgeId,
                                PersonId = link.EntityId,
                                RelationshipContext = link.RelationshipContext,
                                Role = link.Role,
                                Mentions = link.Mentions,
                                ConfidenceScore = link.ConfidenceScore ?? 0.0,
                            });
                        }
                    }
                }
            }

            // Persist knowledge items + junctions so the relationship resolver below
            // can query the freshly-created commit children by Source / FilePath.
            await _db.SaveChangesAsync(ct);

            // NODE-5: Import commit-history-scoped relationships. Target resolution
            // is by (VaultId, FilePath); orphan targets are recorded under the commit
            // child's PlatformData.unlinkedFiles JSON array.
            await ImportCommitRelationshipsAsync(
                package.Data.KnowledgeItems, tenantId, localVaultId, ct);

            // Import comments
            foreach (var pc in package.Data.Comments)
            {
                var existing = await _db.Comments
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == pc.Id, ct);

                if (existing != null)
                {
                    if (pc.UpdatedAt > existing.UpdatedAt)
                    {
                        existing.Body = pc.Body;
                        existing.AuthorName = pc.AuthorName;
                        existing.UpdatedAt = pc.UpdatedAt;
                        result.Accepted++;
                    }
                    else result.Skipped++;
                }
                else
                {
                    _db.Comments.Add(new global::Knowz.Core.Entities.KnowledgeComment
                    {
                        Id = pc.Id,
                        TenantId = tenantId,
                        KnowledgeId = pc.KnowledgeId,
                        ParentCommentId = pc.ParentCommentId,
                        AuthorName = pc.AuthorName,
                        Body = pc.Body,
                        CreatedAt = pc.CreatedAt,
                        UpdatedAt = pc.UpdatedAt,
                    });
                    result.Accepted++;
                }
            }

            // Process tombstones
            if (package.Tombstones != null)
            {
                foreach (var tombstone in package.Tombstones)
                {
                    if (await ApplyTombstoneAsync(tenantId, tombstone, ct))
                        result.TombstonesApplied++;
                }
            }

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct);
            _logger.LogError(ex, "Sync delta import failed");
            result.Success = false;
            result.Error = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// NODE-5: test-only entry point into the vault-import path. Public so tests can
    /// exercise relationship payload resolution without spinning up the full
    /// rate-limiter / schema-check / audit-log pipeline.
    /// </summary>
    public Task<SyncDeltaImportResult> TestOnly_ImportSyncDeltaLocallyAsync(
        global::Knowz.Core.Portability.PortableExportPackage package,
        Guid localVaultId,
        CancellationToken ct = default)
        => ImportSyncDeltaLocallyAsync(package, localVaultId, ct);

    /// <summary>
    /// NODE-5: resolve and persist commit-history-scoped <c>KnowledgeRelationship</c>
    /// rows from an incoming sync package. Source commit child is resolved via
    /// <c>Knowledge.Source = "{repoUrl}:{branch}:commit:{sha}"</c>; target file knowledge
    /// is resolved via <c>(VaultId, FilePath)</c>. Orphan target paths are appended to
    /// the commit child's <c>PlatformData.unlinkedFiles</c> JSON array (matching the
    /// local-ingestion orphan convention in <c>GitCommitHistoryService</c>).
    ///
    /// Dedup: skip creation when a row with the same
    /// <c>(TenantId, SourceKnowledgeId, TargetKnowledgeId, RelationshipType)</c> already
    /// exists. This handles the "re-sync same commit" case without relying on a DB
    /// unique-index violation.
    /// See <c>knowzcode/specs/SVC_PlatformSyncRelationshipPayload.md</c>.
    /// </summary>
    private async Task ImportCommitRelationshipsAsync(
        List<global::Knowz.Core.Portability.PortableKnowledge> incomingKnowledge,
        Guid tenantId,
        Guid localVaultId,
        CancellationToken ct)
    {
        var commitItems = incomingKnowledge
            .Where(pk => pk.Relationships != null && pk.Relationships.Count > 0
                && (pk.Type == global::Knowz.Core.Enums.KnowledgeType.Commit
                    || pk.Type == global::Knowz.Core.Enums.KnowledgeType.CommitHistory))
            .ToList();

        if (commitItems.Count == 0) return;

        foreach (var pk in commitItems)
        {
            // Resolve the source commit child by its Source string. We use the
            // pk.Source directly because the receiver's commit child should carry
            // the same Source string (the sender just wrote it from GitCommitHistoryService).
            if (string.IsNullOrEmpty(pk.Source))
            {
                _logger.LogDebug(
                    "Skipping commit relationship payload — source knowledge has no Source string");
                continue;
            }

            var sourceChild = await _db.KnowledgeItems
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    k => k.TenantId == tenantId && k.Source == pk.Source && !k.IsDeleted, ct);
            if (sourceChild == null)
            {
                _logger.LogDebug(
                    "Commit relationship payload — source commit child not yet present on receiver: {Source}",
                    pk.Source);
                continue;
            }

            var orphanPaths = new List<string>();
            foreach (var rel in pk.Relationships!)
            {
                // PartOf: resolve the parent CommitHistory via its own Source string
                // (derived from the source child's Source by swapping :commit:{sha} → :commit-history).
                if (rel.RelationshipType == global::Knowz.Core.Enums.KnowledgeRelationshipType.PartOf)
                {
                    var parentSource = BuildCommitHistoryParentSource(pk.Source);
                    if (parentSource == null) continue;

                    var parent = await _db.KnowledgeItems
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(
                            k => k.TenantId == tenantId && k.Source == parentSource && !k.IsDeleted, ct);
                    if (parent == null) continue;

                    await global::Knowz.SelfHosted.Application.Services.Shared.KnowledgeRelationshipHelpers
                        .UpsertRelationshipAsync(
                            _db, tenantId, sourceChild.Id, parent.Id,
                            global::Knowz.Core.Enums.KnowledgeRelationshipType.PartOf, ct);
                }
                else if (rel.RelationshipType == global::Knowz.Core.Enums.KnowledgeRelationshipType.References)
                {
                    if (string.IsNullOrEmpty(rel.TargetFilePath))
                    {
                        continue;
                    }

                    // Target file resolution by (VaultId, FilePath): the knowledge must
                    // be a member of the receiver's local vault.
                    var targetFile = await (
                        from k in _db.KnowledgeItems.IgnoreQueryFilters()
                        join kv in _db.KnowledgeVaults.IgnoreQueryFilters()
                            on k.Id equals kv.KnowledgeId
                        where k.TenantId == tenantId
                            && !k.IsDeleted
                            && k.FilePath == rel.TargetFilePath
                            && kv.VaultId == localVaultId
                        select k).FirstOrDefaultAsync(ct);

                    if (targetFile == null)
                    {
                        orphanPaths.Add(rel.TargetFilePath);
                        continue;
                    }

                    await global::Knowz.SelfHosted.Application.Services.Shared.KnowledgeRelationshipHelpers
                        .UpsertRelationshipAsync(
                            _db, tenantId, sourceChild.Id, targetFile.Id,
                            global::Knowz.Core.Enums.KnowledgeRelationshipType.References, ct);
                }
            }

            if (orphanPaths.Count > 0)
            {
                sourceChild.PlatformData = global::Knowz.SelfHosted.Application.Services.Shared.KnowledgeRelationshipHelpers
                    .MergeUnlinkedFiles(sourceChild.PlatformData, orphanPaths);
                sourceChild.UpdatedAt = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Derive the parent CommitHistory <c>Source</c> string from a child commit's
    /// <c>Source</c> string. "{repoUrl}:{branch}:commit:{sha}" → "{repoUrl}:{branch}:commit-history".
    /// </summary>
    internal static string? BuildCommitHistoryParentSource(string childSource)
    {
        const string marker = ":commit:";
        var idx = childSource.LastIndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        return childSource[..idx] + ":commit-history";
    }

    /// <summary>
    /// Generic last-write-wins sync for reference entities (Topic, Tag, Person, Location, Event).
    /// </summary>
    private async Task<int> SyncEntities<TPortable, TEntity>(
        List<TPortable> incoming,
        DbSet<TEntity> dbSet,
        Guid tenantId,
        Action<TPortable, TEntity> updateAction,
        CancellationToken ct)
        where TPortable : class
        where TEntity : class, global::Knowz.Core.Interfaces.ISelfHostedEntity
    {
        var count = 0;
        foreach (var item in incoming)
        {
            var id = (Guid)item.GetType().GetProperty("Id")!.GetValue(item)!;
            var updatedAt = (DateTime)item.GetType().GetProperty("UpdatedAt")!.GetValue(item)!;

            var existing = await dbSet.IgnoreQueryFilters()
                .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == id, ct);

            if (existing != null)
            {
                if (updatedAt > existing.UpdatedAt)
                {
                    updateAction(item, existing);
                    existing.UpdatedAt = updatedAt;
                    count++;
                }
            }
            else
            {
                var name = (string?)item.GetType().GetProperty("Name")?.GetValue(item) ?? "";
                var createdAt = (DateTime)item.GetType().GetProperty("CreatedAt")!.GetValue(item)!;
                var newEntity = (TEntity)Activator.CreateInstance(typeof(TEntity))!;
                newEntity.Id = id;
                newEntity.TenantId = tenantId;
                newEntity.CreatedAt = createdAt;
                newEntity.UpdatedAt = updatedAt;
                // Set name via reflection
                newEntity.GetType().GetProperty("Name")?.SetValue(newEntity, name);
                // Set description if available
                var desc = item.GetType().GetProperty("Description")?.GetValue(item);
                newEntity.GetType().GetProperty("Description")?.SetValue(newEntity, desc);
                dbSet.Add(newEntity);
                count++;
            }
        }
        return count;
    }

    private async Task<bool> ApplyTombstoneAsync(Guid tenantId, global::Knowz.Core.Portability.SyncTombstoneDto tombstone, CancellationToken ct)
    {
        // ISelfHostedEntity has no DeletedAt — only IsDeleted + UpdatedAt
        switch (tombstone.EntityType)
        {
            case "Knowledge":
                var k = await _db.KnowledgeItems.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == tombstone.EntityId, ct);
                if (k != null && !k.IsDeleted && tombstone.DeletedAt > k.UpdatedAt)
                {
                    k.IsDeleted = true; k.UpdatedAt = tombstone.DeletedAt;
                    return true;
                }
                break;
            case "Topic":
                var t = await _db.Topics.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == tombstone.EntityId, ct);
                if (t != null && !t.IsDeleted && tombstone.DeletedAt > t.UpdatedAt)
                {
                    t.IsDeleted = true; t.UpdatedAt = tombstone.DeletedAt;
                    return true;
                }
                break;
            case "Person":
                var p = await _db.Persons.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == tombstone.EntityId, ct);
                if (p != null && !p.IsDeleted && tombstone.DeletedAt > p.UpdatedAt)
                {
                    p.IsDeleted = true; p.UpdatedAt = tombstone.DeletedAt;
                    return true;
                }
                break;
            case "Location":
                var l = await _db.Locations.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == tombstone.EntityId, ct);
                if (l != null && !l.IsDeleted && tombstone.DeletedAt > l.UpdatedAt)
                {
                    l.IsDeleted = true; l.UpdatedAt = tombstone.DeletedAt;
                    return true;
                }
                break;
            case "Event":
                var e = await _db.Events.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == tombstone.EntityId, ct);
                if (e != null && !e.IsDeleted && tombstone.DeletedAt > e.UpdatedAt)
                {
                    e.IsDeleted = true; e.UpdatedAt = tombstone.DeletedAt;
                    return true;
                }
                break;
        }
        return false;
    }

    public async Task<VaultSyncStatusDto?> GetStatusAsync(Guid localVaultId, CancellationToken ct = default)
    {
        var link = await _db.VaultSyncLinks
            .FirstOrDefaultAsync(l => l.LocalVaultId == localVaultId, ct);

        if (link == null) return null;

        var vault = await _db.Vaults.FindAsync([localVaultId], ct);

        return new VaultSyncStatusDto
        {
            LinkId = link.Id,
            LocalVaultId = link.LocalVaultId,
            LocalVaultName = vault?.Name ?? "Unknown",
            RemoteVaultId = link.RemoteVaultId,
            PlatformApiUrl = link.PlatformApiUrl,
            Status = link.LastSyncStatus.ToString(),
            LastSyncError = link.LastSyncError,
            LastSyncCompletedAt = link.LastSyncCompletedAt,
            LastPullCursor = link.LastPullCursor,
            LastPushCursor = link.LastPushCursor,
            SyncEnabled = link.SyncEnabled,
        };
    }

    public async Task<List<VaultSyncStatusDto>> ListLinksAsync(CancellationToken ct = default)
    {
        var links = await _db.VaultSyncLinks.ToListAsync(ct);
        var vaultIds = links.Select(l => l.LocalVaultId).ToList();
        var vaults = await _db.Vaults
            .Where(v => vaultIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, ct);

        return links.Select(link => new VaultSyncStatusDto
        {
            LinkId = link.Id,
            LocalVaultId = link.LocalVaultId,
            LocalVaultName = vaults.TryGetValue(link.LocalVaultId, out var v) ? v.Name : "Unknown",
            RemoteVaultId = link.RemoteVaultId,
            PlatformApiUrl = link.PlatformApiUrl,
            Status = link.LastSyncStatus.ToString(),
            LastSyncError = link.LastSyncError,
            LastSyncCompletedAt = link.LastSyncCompletedAt,
            LastPullCursor = link.LastPullCursor,
            LastPushCursor = link.LastPushCursor,
            SyncEnabled = link.SyncEnabled,
        }).ToList();
    }

    public async Task<VaultSyncStatusDto> EstablishLinkAsync(EstablishSyncLinkRequest request, CancellationToken ct = default)
    {
        // Verify vault exists
        var vault = await _db.Vaults.FindAsync([request.LocalVaultId], ct);
        if (vault == null)
            throw new InvalidOperationException($"Local vault {request.LocalVaultId} not found");

        // Check for existing link
        var existing = await _db.VaultSyncLinks
            .FirstOrDefaultAsync(l => l.LocalVaultId == request.LocalVaultId, ct);
        if (existing != null)
            throw new InvalidOperationException($"Vault {request.LocalVaultId} already has a sync link");

        // V-SEC-07: audit the Connect operation.
        Guid? auditRunId = null;
        if (_auditLog != null)
        {
            try
            {
                auditRunId = await _auditLog.StartAsync(new PlatformSyncRunStart(
                    UserId: Guid.Empty,
                    UserEmail: null,
                    Operation: PlatformSyncOperation.Connect,
                    Direction: PlatformSyncDirection.None,
                    VaultSyncLinkId: null), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PlatformAuditLog.StartAsync failed for Connect");
            }
        }

        try
        {
            // Resolve or create the per-tenant PlatformConnection (replaces the
            // plaintext VaultSyncLink.ApiKeyEncrypted column and the Guid.Empty
            // RemoteTenantId hack).
            var tenantId = _tenantProvider.TenantId;
            var connection = await _db.PlatformConnections
                .FirstOrDefaultAsync(c => c.TenantId == tenantId, ct);

            if (connection is null && _connectionService is not null)
            {
                await _connectionService.UpsertAsync(
                    new UpsertPlatformConnectionRequest(
                        request.PlatformApiUrl,
                        DisplayName: null,
                        ApiKey: request.ApiKey),
                    createdByUserId: Guid.Empty,
                    ct);

                connection = await _db.PlatformConnections
                    .FirstOrDefaultAsync(c => c.TenantId == tenantId, ct);
            }

            // Fallback for bootstrapped scenarios where no connection service is wired
            // (e.g. tests constructing the orchestrator directly) — write the legacy columns.
            var link = new VaultSyncLink
            {
                LocalVaultId = request.LocalVaultId,
                RemoteVaultId = request.RemoteVaultId,
                PlatformConnectionId = connection?.Id,
#pragma warning disable CS0618 // Legacy columns retained until follow-up drop migration.
                PlatformApiUrl = request.PlatformApiUrl.TrimEnd('/'),
                ApiKeyEncrypted = connection is null ? request.ApiKey : string.Empty,
#pragma warning restore CS0618
            };

            _db.VaultSyncLinks.Add(link);
            await _db.SaveChangesAsync(ct);

            // Register as sync partner AND capture RemoteTenantId from the test response
            // (replaces the Guid.Empty hack).
            try
            {
                await _platformClient.RegisterPartnerAsync(link, $"selfhosted-{vault.Name}", ct);

                if (_connectionService is not null)
                {
                    var testResult = await _connectionService.TestAsync(ct: ct);
                    if (testResult.Status == PlatformConnectionTestStatus.Ok && testResult.RemoteTenantId is Guid rid)
                    {
                        link.RemoteTenantId = rid;
                        await _db.SaveChangesAsync(ct);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to register sync partner on platform (will retry on first sync)");
            }

            if (_auditLog != null && auditRunId.HasValue)
            {
                try
                {
                    await _auditLog.CompleteAsync(
                        auditRunId.Value,
                        new PlatformSyncRunResult(ItemCount: 0, BytesTransferred: 0, Status: PlatformSyncRunStatus.Succeeded),
                        ct);
                }
                catch (Exception auditEx)
                {
                    _logger.LogWarning(auditEx, "PlatformAuditLog.CompleteAsync failed for Connect run {RunId}", auditRunId);
                }
            }

            return new VaultSyncStatusDto
            {
                LinkId = link.Id,
                LocalVaultId = link.LocalVaultId,
                LocalVaultName = vault.Name,
                RemoteVaultId = link.RemoteVaultId,
                PlatformApiUrl = link.PlatformApiUrl,
                Status = link.LastSyncStatus.ToString(),
                SyncEnabled = link.SyncEnabled,
            };
        }
        catch (Exception ex)
        {
            if (_auditLog != null && auditRunId.HasValue)
            {
                try
                {
                    await _auditLog.FailAsync(auditRunId.Value, ex.Message, ct);
                }
                catch (Exception auditEx)
                {
                    _logger.LogWarning(auditEx, "PlatformAuditLog.FailAsync failed for Connect run {RunId}", auditRunId);
                }
            }
            throw;
        }
    }

    public async Task<bool> RemoveLinkAsync(Guid localVaultId, CancellationToken ct = default)
    {
        var link = await _db.VaultSyncLinks
            .FirstOrDefaultAsync(l => l.LocalVaultId == localVaultId, ct);

        if (link == null) return false;

        // Remove associated tombstones
        var tombstones = await _db.SyncTombstones
            .Where(st => st.VaultSyncLinkId == link.Id)
            .ToListAsync(ct);
        _db.SyncTombstones.RemoveRange(tombstones);

        _db.VaultSyncLinks.Remove(link);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // ------------------------------------------------------------------------
    // Single-item sync (NodeID PlatformSyncItemOps) — V-SEC-09, V-SEC-11, V-SEC-12
    // ------------------------------------------------------------------------

    public async Task<SyncItemResult> SyncItemAsync(
        Guid vaultSyncLinkId,
        Guid knowledgeId,
        SyncItemDirection direction,
        bool overwriteLocal = false,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // V-SEC-12: GUIDs are route-validated; belt-and-braces check at service boundary.
        if (knowledgeId == Guid.Empty)
        {
            return new SyncItemResult
            {
                Success = false,
                Outcome = SyncItemOutcome.Failed,
                Message = "Invalid knowledge id.",
                Duration = sw.Elapsed,
            };
        }

        var link = await _db.VaultSyncLinks
            .FirstOrDefaultAsync(l => l.Id == vaultSyncLinkId, ct);
        if (link == null)
        {
            return new SyncItemResult
            {
                Success = false,
                Outcome = SyncItemOutcome.NotFound,
                Message = "Sync link not found.",
                Duration = sw.Elapsed,
            };
        }

        if (!link.SyncEnabled)
        {
            return new SyncItemResult
            {
                Success = false,
                Outcome = SyncItemOutcome.PermissionDenied,
                Message = "Sync is disabled for this link.",
                Duration = sw.Elapsed,
            };
        }

        // V-SEC-09: rate limit check BEFORE any platform HTTP call.
        Guid? rateLimitOpId = null;
        if (_rateLimiter != null)
        {
            var decision = await _rateLimiter.CheckAsync(_tenantProvider.TenantId, itemCount: 1, ct);
            if (!decision.Allowed)
            {
                var message = decision.Reason switch
                {
                    RateLimitReason.HourlyQuotaExceeded => "Rate limit exceeded (10 runs per hour).",
                    RateLimitReason.ConcurrentRunInProgress => "Another sync is already in progress for this tenant.",
                    RateLimitReason.ItemLimitExceeded => "Item limit exceeded.",
                    _ => "Rate limit exceeded.",
                };
                throw new RateLimitExceededException(decision.Reason!.Value, decision.RetryAfter, message);
            }
            rateLimitOpId = await _rateLimiter.RecordOperationAsync(
                _tenantProvider.TenantId, $"SyncItem.{direction}", ct);
        }

        // Begin audit row when Node 4's audit log is wired in.
        Guid? auditRunId = null;
        if (_auditLog != null)
        {
            try
            {
                auditRunId = await _auditLog.StartAsync(new PlatformSyncRunStart(
                    UserId: Guid.Empty,
                    UserEmail: null,
                    Operation: direction == SyncItemDirection.Pull
                        ? PlatformSyncOperation.PullItem
                        : PlatformSyncOperation.PushItem,
                    Direction: direction == SyncItemDirection.Pull
                        ? PlatformSyncDirection.Pull
                        : PlatformSyncDirection.Push,
                    VaultSyncLinkId: link.Id,
                    KnowledgeId: knowledgeId), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PlatformAuditLog.StartAsync failed for SyncItemAsync");
            }
        }

        SyncItemResult result;
        try
        {
            result = direction == SyncItemDirection.Pull
                ? await SyncItemPullAsync(link, knowledgeId, overwriteLocal, sw, ct)
                : await SyncItemPushAsync(link, knowledgeId, sw, ct);
        }
        catch (RateLimitExceededException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Platform unreachable during SyncItemAsync {Direction}", direction);
            result = new SyncItemResult
            {
                Success = false,
                Outcome = SyncItemOutcome.Failed,
                Message = "Platform is unreachable",
                Duration = sw.Elapsed,
            };
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "SyncItemAsync {Direction} failed", direction);
            result = new SyncItemResult
            {
                Success = false,
                Outcome = SyncItemOutcome.Failed,
                Message = ex.Message.Contains("invalid data", StringComparison.OrdinalIgnoreCase)
                    ? "Platform returned invalid data"
                    : ex.Message,
                Duration = sw.Elapsed,
            };
        }
        finally
        {
            if (_rateLimiter != null && rateLimitOpId.HasValue)
                await _rateLimiter.CompleteOperationAsync(rateLimitOpId.Value, ct);
        }

        // Close out the audit row.
        if (_auditLog != null && auditRunId.HasValue)
        {
            try
            {
                if (result.Success)
                {
                    await _auditLog.CompleteAsync(
                        auditRunId.Value,
                        new PlatformSyncRunResult(
                            ItemCount: 1,
                            BytesTransferred: 0,
                            Status: PlatformSyncRunStatus.Succeeded),
                        ct);
                }
                else
                {
                    await _auditLog.FailAsync(
                        auditRunId.Value,
                        result.Message ?? "Unknown failure",
                        ct);
                }
            }
            catch (Exception auditEx)
            {
                _logger.LogWarning(auditEx, "PlatformAuditLog finalization failed for SyncItemAsync");
            }
        }

        return result;
    }

    private async Task<SyncItemResult> SyncItemPullAsync(
        VaultSyncLink link, Guid remoteKnowledgeId, bool overwriteLocal, Stopwatch sw, CancellationToken ct)
    {
        var package = await _platformClient.ExportItemAsync(link, remoteKnowledgeId, ct);
        if (package == null)
        {
            return new SyncItemResult
            {
                Success = false,
                Outcome = SyncItemOutcome.NotFound,
                Message = "Item not found on platform.",
                Duration = sw.Elapsed,
            };
        }

        // V-SEC-12: payload must contain the exact id we asked for.
        if (package.Data.KnowledgeItems.Count != 1 ||
            package.Data.KnowledgeItems[0].Id != remoteKnowledgeId)
        {
            return new SyncItemResult
            {
                Success = false,
                Outcome = SyncItemOutcome.Failed,
                Message = "Platform returned invalid data",
                Duration = sw.Elapsed,
            };
        }

        var tenantId = _tenantProvider.TenantId;
        var existing = await _db.KnowledgeItems
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(k => k.TenantId == tenantId && k.Id == remoteKnowledgeId, ct);

        // V-SEC-11: Skip-by-default when local exists. Overwrite only when explicit.
        if (existing != null && !overwriteLocal)
        {
            return new SyncItemResult
            {
                Success = true,
                Outcome = SyncItemOutcome.Skipped,
                LocalKnowledgeId = existing.Id,
                Message = "Local copy exists — set overwriteLocal=true to overwrite.",
                Duration = sw.Elapsed,
            };
        }

        // Prefer the strategy-aware PortableImportService. Fall back to the orchestrator's
        // internal last-write-wins import only when DI hasn't wired it (legacy tests).
        var strategy = overwriteLocal ? ImportConflictStrategy.Overwrite : ImportConflictStrategy.Skip;
        if (_importService != null)
        {
            var importResult = await _importService.ImportAsync(package, strategy, ct);
            if (!importResult.Success)
            {
                return new SyncItemResult
                {
                    Success = false,
                    Outcome = SyncItemOutcome.Failed,
                    Message = importResult.Error ?? "Import failed.",
                    Duration = sw.Elapsed,
                };
            }
        }
        else
        {
            var importResult = await ImportSyncDeltaLocallyAsync(package, link.LocalVaultId, ct);
            if (!importResult.Success)
            {
                return new SyncItemResult
                {
                    Success = false,
                    Outcome = SyncItemOutcome.Failed,
                    Message = importResult.Error ?? "Import failed.",
                    Duration = sw.Elapsed,
                };
            }
        }

        return new SyncItemResult
        {
            Success = true,
            Outcome = existing == null ? SyncItemOutcome.Created : SyncItemOutcome.Updated,
            LocalKnowledgeId = remoteKnowledgeId,
            Duration = sw.Elapsed,
        };
    }

    private async Task<SyncItemResult> SyncItemPushAsync(
        VaultSyncLink link, Guid localKnowledgeId, Stopwatch sw, CancellationToken ct)
    {
        var isInVault = await _db.KnowledgeVaults
            .IgnoreQueryFilters()
            .AnyAsync(kv => kv.VaultId == link.LocalVaultId && kv.KnowledgeId == localKnowledgeId, ct);

        if (!isInVault)
        {
            return new SyncItemResult
            {
                Success = false,
                Outcome = SyncItemOutcome.NotFound,
                Message = "Knowledge item is not in this vault.",
                Duration = sw.Elapsed,
            };
        }

        PortableExportPackage package;
        try
        {
            package = await _exportService.ExportSingleItemAsync(
                link.LocalVaultId, localKnowledgeId, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return new SyncItemResult
            {
                Success = false,
                Outcome = SyncItemOutcome.NotFound,
                Message = ex.Message,
                Duration = sw.Elapsed,
            };
        }

        var platformResponse = await _platformClient.ImportDeltaAsync(link, package, ct);

        return new SyncItemResult
        {
            Success = true,
            LocalKnowledgeId = localKnowledgeId,
            Outcome = platformResponse.Accepted > 0 ? SyncItemOutcome.Updated : SyncItemOutcome.Unchanged,
            Duration = sw.Elapsed,
        };
    }
}
