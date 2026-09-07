using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services.Shared;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Application.Services.GitCommitHistory;

/// <summary>
/// Backfill / repair service for commit → file <see cref="KnowledgeRelationshipType.References"/>
/// edges. Reads <c>changedFilePaths</c> from each commit row's <c>PlatformData</c> JSON
/// (persisted during ingestion by NODE-3) and re-runs the shared file-resolution helper
/// <see cref="GitCommitHistoryService.ResolveAndLinkChangedFilesAsync"/> to create any
/// missing edges.
///
/// <para>
/// Use case: a file <see cref="Knowledge"/> row can be created AFTER the commit row that
/// originally touched it (e.g. a file added in a later sync run, or the edge was deleted).
/// Ingestion at commit-time couldn't write the edge because the target row didn't exist.
/// Without persisted <c>changedFilePaths</c>, the edge was lost forever — the original
/// path list was discarded after ingestion. With NODE-3, the list survives in
/// <c>PlatformData</c>, and this service rebuilds the edges on-demand.
/// </para>
///
/// <para>
/// <b>Pre-NODE-3 rows are unrecoverable from this endpoint.</b> Commit rows ingested
/// before NODE-3 landed have no <c>changedFilePaths</c> key in their <c>PlatformData</c>.
/// These are counted as <see cref="CommitBackfillResult.Skipped"/> (not failures) and the
/// only way to recover them is to re-sync the repository, which re-walks git and re-runs
/// ingestion with the new metadata writer.
/// </para>
///
/// <para>
/// <b>CRIT-2 double gate.</b> Sensitive paths are filtered at write time inside
/// <see cref="GitCommitHistoryService.BuildInitialChildMetadataJson"/> AND at read time
/// inside <see cref="GitCommitHistoryService.ResolveAndLinkChangedFilesAsync"/>. If a
/// path was added to the deny-list after the commit was ingested, the stored JSON still
/// contains it, but the read-time filter rejects it before any edge is written.
/// </para>
///
/// WorkGroupID: kc-feat-commit-history-polish-20260411-051000
/// NodeID: NODE-3 CommitBackfillEndpoint
/// </summary>
public sealed class CommitRelinkService
{
    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly GitCommitHistoryService _commitHistoryService;
    private readonly ILogger<CommitRelinkService> _logger;

    public CommitRelinkService(
        SelfHostedDbContext db,
        ITenantProvider tenantProvider,
        GitCommitHistoryService commitHistoryService,
        ILogger<CommitRelinkService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tenantProvider = tenantProvider ?? throw new ArgumentNullException(nameof(tenantProvider));
        _commitHistoryService = commitHistoryService
            ?? throw new ArgumentNullException(nameof(commitHistoryService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Re-runs the file-resolution loop for every commit knowledge row belonging to the
    /// given repository and rebuilds missing <c>References</c> edges from the persisted
    /// <c>changedFilePaths</c> list.
    /// </summary>
    /// <param name="repositoryId">GitRepository id to relink. Must belong to the current tenant.</param>
    /// <param name="ct">Cancellation token — honored between commits.</param>
    /// <returns>A <see cref="CommitBackfillResult"/> summarising the run.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the repository is not found for the current tenant.</exception>
    public async Task<CommitBackfillResult> RelinkRepositoryAsync(
        Guid repositoryId,
        CancellationToken ct)
    {
        var tenantId = _tenantProvider.TenantId;

        // IgnoreQueryFilters is safe here because we match TenantId explicitly.
        // Mirrors the lookup pattern in GitCommitHistoryService.ProcessCommitsAsync.
        var repo = await _db.GitRepositories
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(g => g.Id == repositoryId && g.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException($"GitRepository {repositoryId} not found");

        // Derive the commit-child Source prefix so we only load rows that belong to this repo.
        // Matches GitCommitHistoryService.BuildChildSource: "{RepoUrl}:{Branch}:commit:{sha}".
        var prefix = $"{repo.RepositoryUrl}:{repo.Branch}:commit:";

        var commits = await _db.KnowledgeItems
            .IgnoreQueryFilters()
            .Where(k => k.TenantId == tenantId
                && k.Type == KnowledgeType.Commit
                && !k.IsDeleted
                && k.Source != null
                && k.Source.StartsWith(prefix))
            .ToListAsync(ct);

        int processed = 0;
        int linkedTotal = 0;
        int skipped = 0;

        foreach (var commit in commits)
        {
            ct.ThrowIfCancellationRequested();
            processed++;

            var paths = TryReadChangedFilePaths(commit.PlatformData);
            if (paths == null)
            {
                // Pre-NODE-3 row — no changedFilePaths key in PlatformData. Not an error,
                // just unrecoverable from this endpoint. Re-sync the repository to repopulate.
                skipped++;
                continue;
            }

            if (paths.Count == 0)
            {
                // Key present but empty — commit touched no files (or only sensitive files at
                // write time). Count as processed, no linking possible. Don't bump UpdatedAt.
                continue;
            }

            var (linked, orphans) = await _commitHistoryService.ResolveAndLinkChangedFilesAsync(
                commit.Id, paths, repo.VaultId, tenantId, ct);
            linkedTotal += linked;

            // Refresh the orphan list on the stored PlatformData so the unlinkedFiles view
            // shrinks as files are created in subsequent syncs. MergeUnlinkedFiles preserves
            // existing keys and overwrites the unlinkedFiles array.
            //
            // NOTE: MergeUnlinkedFiles deduplicates against the EXISTING list, so calling it
            // with an empty orphans list does NOT clear out previously-orphaned paths. We need
            // to overwrite explicitly: read existing, intersect with new orphans, replace.
            commit.PlatformData = ReplaceUnlinkedFiles(commit.PlatformData, orphans);
            commit.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "CommitRelink completed for repository {RepositoryId}: processed={Processed} linked={Linked} skipped={Skipped}",
            repositoryId, processed, linkedTotal, skipped);

        return new CommitBackfillResult(processed, linkedTotal, skipped);
    }

    /// <summary>
    /// Reads the <c>changedFilePaths</c> array from a commit row's <c>PlatformData</c> JSON.
    /// Returns <c>null</c> when the key is absent (pre-NODE-3 row — caller should count as
    /// skipped) and a (possibly empty) list when the key is present.
    /// </summary>
    internal static IReadOnlyList<string>? TryReadChangedFilePaths(string? platformDataJson)
    {
        if (string.IsNullOrEmpty(platformDataJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(platformDataJson);
            if (!doc.RootElement.TryGetProperty("changedFilePaths", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return arr.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? string.Empty)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Overwrites the <c>unlinkedFiles</c> array on a commit row's <c>PlatformData</c> JSON
    /// with the given list (replaces, does not append). Preserves all other keys.
    /// Unlike <see cref="KnowledgeRelationshipHelpers.MergeUnlinkedFiles"/> which appends,
    /// backfill needs replacement semantics so previously-orphaned paths drop out of the
    /// list once their target file row exists.
    /// </summary>
    internal static string ReplaceUnlinkedFiles(string? platformDataJson, IReadOnlyList<string> orphanPaths)
    {
        Dictionary<string, object?> dict;
        try
        {
            dict = string.IsNullOrEmpty(platformDataJson)
                ? new Dictionary<string, object?>()
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(platformDataJson)
                    ?? new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            dict = new Dictionary<string, object?>();
        }

        dict["unlinkedFiles"] = orphanPaths.ToList();
        return JsonSerializer.Serialize(dict);
    }
}
