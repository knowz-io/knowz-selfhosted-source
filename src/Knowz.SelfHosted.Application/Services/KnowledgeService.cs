using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.Core.Models;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Specifications;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Extensions;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Application.Services;

/// <summary>
/// Service for CRUD operations on knowledge items.
/// Uses ISelfHostedRepository for primary entity operations and DbContext for complex joins.
/// </summary>
public class KnowledgeService
{
    private readonly ISelfHostedRepository<Knowledge> _knowledgeRepo;
    private readonly ISelfHostedRepository<Tag> _tagRepo;
    private readonly SelfHostedDbContext _db;
    private readonly ISearchService _searchService;
    private readonly IOpenAIService _openAIService;
    private readonly ISelfHostedChunkingService _chunkingService;
    private readonly ITenantProvider _tenantProvider;
    private readonly ILogger<KnowledgeService> _logger;
    private readonly IEnrichmentOutboxWriter? _enrichmentWriter;
    private readonly IVersioningService? _versioningService;

    public KnowledgeService(
        ISelfHostedRepository<Knowledge> knowledgeRepo,
        ISelfHostedRepository<Tag> tagRepo,
        SelfHostedDbContext db,
        ISearchService searchService,
        IOpenAIService openAIService,
        ISelfHostedChunkingService chunkingService,
        ITenantProvider tenantProvider,
        ILogger<KnowledgeService> logger,
        IEnrichmentOutboxWriter? enrichmentWriter = null,
        IVersioningService? versioningService = null)
    {
        _knowledgeRepo = knowledgeRepo;
        _tagRepo = tagRepo;
        _db = db;
        _searchService = searchService;
        _openAIService = openAIService;
        _chunkingService = chunkingService;
        _tenantProvider = tenantProvider;
        _logger = logger;
        _enrichmentWriter = enrichmentWriter;
        _versioningService = versioningService;
    }

    public async Task<CreateKnowledgeResult> CreateKnowledgeAsync(
        string content, string title, string typeStr, string? vaultIdStr,
        List<string> tagNames, string? source, CancellationToken ct,
        Guid? createdByUserId = null)
    {
        var tenantId = _tenantProvider.TenantId;

        var item = new Knowledge
        {
            TenantId = tenantId,
            Title = title,
            Content = content,
            Type = Enum.TryParse<KnowledgeType>(typeStr, true, out var t) ? t : KnowledgeType.Note,
            Source = source,
            CreatedByUserId = createdByUserId
        };

        _db.KnowledgeItems.Add(item);

        Guid? vaultId = ParseGuid(vaultIdStr);
        if (!vaultId.HasValue)
        {
            // Fallback 1: tenant's IsDefault vault
            var defaultVaultId = await _db.Vaults
                .Where(v => v.TenantId == tenantId && !v.IsDeleted && v.IsDefault)
                .Select(v => v.Id)
                .FirstOrDefaultAsync(ct);

            if (defaultVaultId != Guid.Empty)
            {
                vaultId = defaultVaultId;
                _logger.LogInformation(
                    "CreateKnowledge: no VaultId provided, falling back to default vault {VaultId} for tenant {TenantId}",
                    vaultId, tenantId);
            }
            else
            {
                // Fallback 2: sole non-deleted vault for the tenant
                var tenantVaultIds = await _db.Vaults
                    .Where(v => v.TenantId == tenantId && !v.IsDeleted)
                    .Select(v => v.Id)
                    .Take(2)
                    .ToListAsync(ct);

                if (tenantVaultIds.Count == 1)
                {
                    vaultId = tenantVaultIds[0];
                    _logger.LogInformation(
                        "CreateKnowledge: no VaultId provided, falling back to default vault {VaultId} for tenant {TenantId}",
                        vaultId, tenantId);
                }
            }
        }

        if (vaultId.HasValue)
        {
            _db.KnowledgeVaults.Add(new KnowledgeVault
            {
                TenantId = tenantId,
                KnowledgeId = item.Id,
                VaultId = vaultId.Value,
                IsPrimary = true
            });
        }

        var existingTags = await _tagRepo.ListAsync(new TagsByNamesSpec(tagNames), ct);
        foreach (var tagName in tagNames)
        {
            var tag = existingTags.FirstOrDefault(t => t.Name == tagName);
            if (tag == null)
            {
                tag = new Tag { TenantId = tenantId, Name = tagName };
                _db.Tags.Add(tag);
            }
            item.Tags.Add(tag);
        }

        await _db.SaveChangesAsync(ct);

        // Index in search with chunking (non-critical failure)
        try
        {
            var vault = vaultId.HasValue
                ? await _db.Vaults.FindAsync(new object[] { vaultId.Value }, ct)
                : null;

            List<Guid>? ancestorVaultIds = null;
            if (vaultId.HasValue)
            {
                ancestorVaultIds = await _db.VaultAncestors
                    .Where(va => va.DescendantVaultId == vaultId.Value)
                    .Select(va => va.AncestorVaultId)
                    .ToListAsync(ct);
            }

            var strategy = _chunkingService.DetermineStrategy(item.Type);
            var chunks = _chunkingService.ChunkWithContext(
                content, title, summary: null, tags: null, strategy);
            _logger.LogInformation("Indexing knowledge {Id} as {ChunkCount} chunk(s)", item.Id, chunks.Count);

            foreach (var chunk in chunks)
            {
                var embedding = await _openAIService.GenerateEmbeddingAsync(chunk.EmbeddingText, ct);

                // FEAT_SelfHostedTemporalAwareness: pass the entity's real
                // creation/update dates so the chat feature can cite them.
                await _searchService.IndexDocumentAsync(
                    item.Id, title, chunk.Content, null,
                    vault?.Name, vaultId, ancestorVaultIds, null,
                    tagNames, typeStr, null, embedding,
                    chunkIndex: chunks.Count > 1 ? chunk.Position : null,
                    knowledgeCreatedAt: item.CreatedAt,
                    knowledgeUpdatedAt: item.UpdatedAt,
                    cancellationToken: ct);
            }

            // Persist chunks
            try
            {
                foreach (var chunk in chunks)
                {
                    _db.ContentChunks.Add(new ContentChunk
                    {
                        TenantId = tenantId,
                        KnowledgeId = item.Id,
                        Position = chunk.Position,
                        Content = chunk.Content,
                        ContentHash = ContentHasher.Hash(chunk.Content)
                    });
                }
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception chunkEx)
            {
                _logger.LogWarning(chunkEx, "Failed to persist chunks for knowledge {Id}", item.Id);
            }

            item.IsIndexed = true;
            item.IndexedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to index knowledge item {Id} in search", item.Id);
        }

        // Create initial version 1 so version history starts non-empty.
        // Without this, history is empty until the first edit, which is confusing.
        if (_versioningService != null)
        {
            try
            {
                await _versioningService.CreateVersionAsync(item.Id, createdByUserId, "Initial version", ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create initial version for knowledge {Id}", item.Id);
            }
        }

        // Enqueue for background AI enrichment (title, summary, tags)
        try
        {
            if (_enrichmentWriter != null)
            {
                await _enrichmentWriter.EnqueueAsync(item.Id, tenantId, ct);
                _logger.LogDebug("Enqueued knowledge {Id} for enrichment", item.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enqueue knowledge {Id} for enrichment", item.Id);
        }

        return new CreateKnowledgeResult(item.Id, item.Title, true);
    }

    public async Task<UpdateKnowledgeResult?> UpdateKnowledgeAsync(
        Guid id, string? title, string? content, string? source,
        List<string>? tagNames, string? vaultIdStr, CancellationToken ct,
        Guid? updatedByUserId = null, string? summaryRefinementGuidance = null)
    {
        var item = await _knowledgeRepo.FirstOrDefaultAsync(new KnowledgeByIdWithRelationsSpec(id), ct);

        if (item == null)
            return null;

        // Capture original values for change-summary computation (post-update snapshot)
        var originalTitle = item.Title;
        var originalContent = item.Content;

        if (title != null) item.Title = title;
        if (content != null) item.Content = content;
        if (source != null) item.Source = source;
        if (summaryRefinementGuidance != null) item.SummaryRefinementGuidance = summaryRefinementGuidance;

        if (tagNames != null)
        {
            var tenantId = _tenantProvider.TenantId;
            item.Tags.Clear();
            var existingTags = await _tagRepo.ListAsync(new TagsByNamesSpec(tagNames), ct);
            foreach (var tagName in tagNames)
            {
                var tag = existingTags.FirstOrDefault(t => t.Name == tagName);
                if (tag == null)
                {
                    tag = new Tag { TenantId = tenantId, Name = tagName };
                    _db.Tags.Add(tag);
                }
                item.Tags.Add(tag);
            }
        }

        // Vault move: reassign knowledge to a different vault
        bool vaultChanged = false;
        Guid? targetVaultId = ParseGuid(vaultIdStr);
        if (targetVaultId.HasValue)
        {
            var targetVault = await _db.Vaults.FindAsync(new object[] { targetVaultId.Value }, ct);
            if (targetVault == null)
                return null; // Target vault not found

            var existingKvs = await _db.KnowledgeVaults
                .Where(kv => kv.KnowledgeId == item.Id)
                .ToListAsync(ct);
            _db.KnowledgeVaults.RemoveRange(existingKvs);

            _db.KnowledgeVaults.Add(new KnowledgeVault
            {
                TenantId = item.TenantId,
                KnowledgeId = item.Id,
                VaultId = targetVaultId.Value,
                IsPrimary = true
            });
            vaultChanged = true;
        }

        item.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Snapshot the post-update state so the latest version row reflects current content.
        // Compute a meaningful change description from what actually changed.
        if (_versioningService != null)
        {
            try
            {
                var changeDescription = BuildChangeDescription(
                    titleChanged: title != null && originalTitle != item.Title,
                    contentChanged: content != null && originalContent != item.Content,
                    tagsChanged: tagNames != null,
                    vaultChanged: vaultChanged,
                    originalContent: originalContent,
                    newContent: item.Content);

                await _versioningService.CreateVersionAsync(id, updatedByUserId, changeDescription, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create version snapshot for knowledge {Id}", id);
            }
        }

        if (content != null || title != null || vaultChanged)
        {
            await ReindexAndEnrichAsync(item, ct);
        }

        return new UpdateKnowledgeResult(item.Id, item.Title, true);
    }

    /// <summary>
    /// Builds a human-readable change description summarizing what was modified in an update.
    /// </summary>
    internal static string BuildChangeDescription(
        bool titleChanged, bool contentChanged, bool tagsChanged, bool vaultChanged,
        string? originalContent, string? newContent)
    {
        var parts = new List<string>();
        if (titleChanged) parts.Add("title");
        if (contentChanged)
        {
            var oldLen = originalContent?.Length ?? 0;
            var newLen = newContent?.Length ?? 0;
            var delta = newLen - oldLen;
            var sign = delta >= 0 ? "+" : "";
            parts.Add($"content ({sign}{delta} chars)");
        }
        if (tagsChanged) parts.Add("tags");
        if (vaultChanged) parts.Add("vault");

        return parts.Count == 0
            ? "Updated"
            : "Updated " + string.Join(", ", parts);
    }

    public async Task<BatchMoveResult> BatchMoveToVaultAsync(
        List<Guid> knowledgeIds, Guid targetVaultId, CancellationToken ct)
    {
        var targetVault = await _db.Vaults.FindAsync(new object[] { targetVaultId }, ct);
        if (targetVault == null)
            return new BatchMoveResult(knowledgeIds.Count, 0, new List<Guid>(), knowledgeIds);

        var items = await _db.KnowledgeItems
            .Where(k => knowledgeIds.Contains(k.Id))
            .ToListAsync(ct);

        var foundIds = items.Select(i => i.Id).ToHashSet();
        var notFoundIds = knowledgeIds.Where(id => !foundIds.Contains(id)).ToList();
        var movedIds = new List<Guid>();

        // Remove old vault assignments and add new ones in a single save
        foreach (var item in items)
        {
            var existingKvs = await _db.KnowledgeVaults
                .Where(kv => kv.KnowledgeId == item.Id)
                .ToListAsync(ct);
            _db.KnowledgeVaults.RemoveRange(existingKvs);

            _db.KnowledgeVaults.Add(new KnowledgeVault
            {
                TenantId = item.TenantId,
                KnowledgeId = item.Id,
                VaultId = targetVaultId,
                IsPrimary = true
            });

            item.UpdatedAt = DateTime.UtcNow;
            movedIds.Add(item.Id);
        }

        await _db.SaveChangesAsync(ct);

        // Re-index each moved item (non-critical — the DB move already succeeded)
        foreach (var item in items)
        {
            try
            {
                var fullItem = await _knowledgeRepo.FirstOrDefaultAsync(new KnowledgeByIdWithRelationsSpec(item.Id), ct);
                if (fullItem != null)
                    await ReindexAndEnrichAsync(fullItem, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to re-index knowledge {Id} after batch move", item.Id);
            }
        }

        return new BatchMoveResult(knowledgeIds.Count, movedIds.Count, movedIds, notFoundIds);
    }

    public async Task<KnowledgeItemResponse?> GetKnowledgeItemAsync(Guid id, CancellationToken ct)
    {
        var item = await _knowledgeRepo.FirstOrDefaultAsync(new KnowledgeByIdWithRelationsSpec(id), ct);

        if (item == null)
            return null;

        return MapToResponse(item);
    }

    public async Task<DeleteResult?> DeleteKnowledgeAsync(Guid id, CancellationToken ct)
    {
        var item = await _knowledgeRepo.GetByIdAsync(id, ct);
        if (item == null)
            return null;

        await _knowledgeRepo.SoftDeleteAsync(item, ct);
        await _knowledgeRepo.SaveChangesAsync(ct);

        try
        {
            await _searchService.DeleteDocumentAsync(id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove knowledge item {Id} from search index", id);
        }

        return new DeleteResult(id, true);
    }

    public async Task<ReprocessResult?> ReprocessKnowledgeAsync(Guid id, CancellationToken ct)
    {
        var item = await _knowledgeRepo.FirstOrDefaultAsync(new KnowledgeByIdWithRelationsSpec(id), ct);
        if (item == null)
            return null;

        await ReindexAndEnrichAsync(item, ct, forceReset: true);

        _logger.LogInformation(
            "Reprocess completed enqueue for knowledge {KnowledgeId}, isIndexed={IsIndexed}, forceReset=true",
            item.Id, item.IsIndexed);

        return new ReprocessResult(item.Id, item.Title, item.IsIndexed);
    }

    public async Task<BulkGetResponse> BulkGetKnowledgeItemsAsync(List<Guid> ids, CancellationToken ct)
    {
        var items = await _knowledgeRepo.ListAsync(new KnowledgeByIdsSpec(ids), ct);

        var responses = items.Select(MapToResponse).ToList();
        return new BulkGetResponse(responses, ids.Count, responses.Count);
    }

    public async Task<KnowledgeListResponse> ListKnowledgeItemsAsync(
        int page, int pageSize, string sortBy, string sortDir,
        string? knowledgeType, string? titlePattern, string? fileNamePattern,
        string? startDateStr, string? endDateStr, CancellationToken ct,
        List<Guid>? accessibleVaultIds = null,
        Guid? filterVaultId = null, Guid? filterCreatedByUserId = null,
        string? filterTag = null)
    {
        var query = BuildFilteredQuery(knowledgeType, titlePattern, fileNamePattern, startDateStr, endDateStr);

        if (accessibleVaultIds != null)
            query = ApplyVaultAccessFilter(query, accessibleVaultIds);

        if (filterVaultId.HasValue)
            query = query.Where(k => _db.KnowledgeVaults.Any(kv => kv.KnowledgeId == k.Id && kv.VaultId == filterVaultId.Value));

        if (filterCreatedByUserId.HasValue)
            query = query.Where(k => k.CreatedByUserId == filterCreatedByUserId.Value);

        if (!string.IsNullOrWhiteSpace(filterTag))
            query = query.Where(k => k.Tags.Any(t => t.Name == filterTag));

        query = (sortBy, sortDir) switch
        {
            ("title", "asc") => query.OrderBy(k => k.Title),
            ("title", _) => query.OrderByDescending(k => k.Title),
            ("updated", "asc") => query.OrderBy(k => k.UpdatedAt),
            ("updated", _) => query.OrderByDescending(k => k.UpdatedAt),
            (_, "asc") => query.OrderBy(k => k.CreatedAt),
            _ => query.OrderByDescending(k => k.CreatedAt)
        };

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(k => new KnowledgeListItem(
                k.Id, k.Title,
                k.Summary ?? (k.Content.Length > 200 ? k.Content.Substring(0, 200) : k.Content),
                k.Type.ToString(),
                k.FilePath,
                _db.KnowledgeVaults
                    .Where(kv => kv.KnowledgeId == k.Id)
                    .OrderByDescending(kv => kv.IsPrimary)
                    .Select(kv => (Guid?)kv.VaultId)
                    .FirstOrDefault(),
                _db.KnowledgeVaults
                    .Where(kv => kv.KnowledgeId == k.Id)
                    .OrderByDescending(kv => kv.IsPrimary)
                    .Join(_db.Vaults, kv => kv.VaultId, v => v.Id, (kv, v) => v.Name)
                    .FirstOrDefault(),
                k.CreatedByUserId,
                k.CreatedByUserId.HasValue
                    ? _db.Users
                        .Where(u => u.Id == k.CreatedByUserId.Value)
                        .Select(u => u.DisplayName ?? u.Username)
                        .FirstOrDefault()
                    : null,
                k.CreatedAt, k.UpdatedAt,
                k.IsIndexed))
            .ToListAsync(ct);

        var totalPages = pageSize > 0 ? (int)Math.Ceiling((double)total / pageSize) : 0;
        return new KnowledgeListResponse(items, page, pageSize, total, totalPages);
    }

    public async Task<CountResponse> CountKnowledgeAsync(
        string? knowledgeType, string? titlePattern, string? fileNamePattern,
        string? startDateStr, string? endDateStr, CancellationToken ct)
    {
        var query = BuildFilteredQuery(knowledgeType, titlePattern, fileNamePattern, startDateStr, endDateStr);
        var count = await query.CountAsync(ct);
        return new CountResponse(count);
    }

    public async Task<KnowledgeStatsResponse> GetStatisticsAsync(CancellationToken ct, List<Guid>? accessibleVaultIds = null)
    {
        var query = _db.KnowledgeItems.AsQueryable();
        if (accessibleVaultIds != null)
            query = ApplyVaultAccessFilter(query, accessibleVaultIds);

        var total = await query.CountAsync(ct);

        var byType = (await query
            .GroupBy(k => k.Type)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToListAsync(ct))
            .Select(x => new TypeCount(x.Type.ToString(), x.Count))
            .ToList();

        var filteredKnowledgeIds = query.Select(k => k.Id);
        var byVault = (await _db.KnowledgeVaults
            .Where(kv => filteredKnowledgeIds.Contains(kv.KnowledgeId))
            .Join(_db.Vaults, kv => kv.VaultId, v => v.Id, (kv, v) => v.Name)
            .GroupBy(name => name)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(20)
            .ToListAsync(ct))
            .Select(x => new VaultCount(x.Name, x.Count))
            .ToList();

        DateRange? dateRange = total > 0
            ? new DateRange(
                await query.MinAsync(k => k.CreatedAt, ct),
                await query.MaxAsync(k => k.CreatedAt, ct))
            : null;

        return new KnowledgeStatsResponse(total, byType, byVault, dateRange);
    }

    /// <summary>
    /// Returns the vault IDs associated with a knowledge item (for access checks).
    /// </summary>
    public async Task<List<Guid>> GetKnowledgeVaultIdsAsync(Guid knowledgeId, CancellationToken ct)
    {
        return await _db.KnowledgeVaults
            .Where(kv => kv.KnowledgeId == knowledgeId)
            .Select(kv => kv.VaultId)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns the paginated list of commit children whose <c>References</c> edges
    /// target the given knowledge item, ordered most-recent-first. Reads the
    /// <see cref="KnowledgeRelationship"/> edges written by the selfhosted
    /// commit-history ingestion path and hydrates each source (commit child)
    /// into a compact <see cref="CommitHistoryEntry"/> DTO.
    ///
    /// Tenant scoping is automatic via the DbContext query filter on
    /// <see cref="KnowledgeRelationship"/> and <see cref="Knowledge"/>.
    /// Authorization (vault-access gate) is the endpoint's responsibility,
    /// not this method.
    ///
    /// WorkGroupID: kc-feat-commit-knowledge-link-20260410-230500
    /// NodeID: SelfHostedKnowledgeCommitHistoryQuery
    /// </summary>
    public async Task<(IReadOnlyList<CommitHistoryEntry> items, int total)> GetCommitHistoryForItemAsync(
        Guid knowledgeId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var baseQuery =
            from rel in _db.KnowledgeRelationships
            where rel.TargetKnowledgeId == knowledgeId
                && rel.RelationshipType == KnowledgeRelationshipType.References
                && !rel.IsDeleted
            join k in _db.KnowledgeItems on rel.SourceKnowledgeId equals k.Id
            where k.Type == KnowledgeType.Commit && !k.IsDeleted
            select k;

        var total = await baseQuery.CountAsync(ct);

        // NODE-2: Sort by CommittedAt column first (true commit timestamp) with
        // CreatedAt as the fallback when the column is NULL (pre-NODE-2 rows).
        // COALESCE translates to ORDER BY COALESCE(CommittedAt, CreatedAt) DESC in SQL.
        // WorkGroupID: kc-feat-commit-history-polish-20260411-051000
        var rows = await baseQuery
            .OrderByDescending(k => k.CommittedAt ?? k.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var items = rows.Select(MapCommitHistoryEntry).ToList();
        return (items, total);
    }

    private static CommitHistoryEntry MapCommitHistoryEntry(Knowledge k)
    {
        string sha = string.Empty;
        string authorName = string.Empty;
        // R-5 NODE-2 precedence: column wins > JSON > CreatedAt final fallback.
        // Start with column-or-CreatedAt; JSON only overrides when the column is NULL.
        // WorkGroupID: kc-feat-commit-history-polish-20260411-051000
        DateTime committedAt = k.CommittedAt ?? k.CreatedAt;
        int changedFileCount = 0;
        int linesAdded = 0;
        int linesDeleted = 0;

        if (!string.IsNullOrEmpty(k.PlatformData))
        {
            try
            {
                using var doc = JsonDocument.Parse(k.PlatformData);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("commitSha", out var shaEl) && shaEl.ValueKind == JsonValueKind.String)
                    {
                        sha = shaEl.GetString() ?? string.Empty;
                    }
                    if (root.TryGetProperty("authorName", out var aEl) && aEl.ValueKind == JsonValueKind.String)
                    {
                        authorName = aEl.GetString() ?? string.Empty;
                    }
                    // Only fall back to JSON when the column is NULL (NODE-2 R-5 precedence).
                    if (!k.CommittedAt.HasValue
                        && root.TryGetProperty("committedAt", out var cEl)
                        && cEl.ValueKind == JsonValueKind.String
                        && DateTime.TryParse(cEl.GetString(), out var parsed))
                    {
                        committedAt = parsed;
                    }
                    if (root.TryGetProperty("changedFileCount", out var fcEl) && fcEl.TryGetInt32(out var fc))
                    {
                        changedFileCount = fc;
                    }
                    if (root.TryGetProperty("linesAddedTotal", out var laEl) && laEl.TryGetInt32(out var la))
                    {
                        linesAdded = la;
                    }
                    if (root.TryGetProperty("linesDeletedTotal", out var ldEl) && ldEl.TryGetInt32(out var ld))
                    {
                        linesDeleted = ld;
                    }
                }
            }
            catch (JsonException)
            {
                // Defensive fallback — keep the CommittedAt/CreatedAt + empty defaults.
            }
        }

        var shortSha = sha.Length >= 7 ? sha[..7] : sha;

        return new CommitHistoryEntry(
            KnowledgeId: k.Id,
            Sha: sha,
            ShortSha: shortSha,
            Title: k.Title,
            AuthorName: authorName,
            CommittedAt: committedAt,
            ChangedFileCount: changedFileCount,
            LinesAdded: linesAdded,
            LinesDeleted: linesDeleted,
            Content: k.Content);
    }

    public async Task<List<CreatorRef>> GetKnowledgeCreatorsAsync(CancellationToken ct)
    {
        return await _db.KnowledgeItems
            .Where(k => k.CreatedByUserId.HasValue)
            .Select(k => k.CreatedByUserId!.Value)
            .Distinct()
            .Join(_db.Users, uid => uid, u => u.Id, (uid, u) => new { u.Id, Name = u.DisplayName ?? u.Username })
            .OrderBy(c => c.Name)
            .Select(c => new CreatorRef(c.Id, c.Name))
            .ToListAsync(ct);
    }

    // --- Helpers ---

    /// <summary>
    /// Filters a knowledge query to only include items in the given vaults (or items with no vault).
    /// </summary>
    private IQueryable<Knowledge> ApplyVaultAccessFilter(IQueryable<Knowledge> query, List<Guid> accessibleVaultIds)
    {
        return query.Where(k =>
            _db.KnowledgeVaults.Any(kv => kv.KnowledgeId == k.Id && accessibleVaultIds.Contains(kv.VaultId))
            || !_db.KnowledgeVaults.Any(kv => kv.KnowledgeId == k.Id));
    }

    private async Task ReindexAndEnrichAsync(Knowledge item, CancellationToken ct, bool forceReset = false)
    {
        try
        {
            var primaryKv = await _db.KnowledgeVaults
                .Include(kv => kv.Vault)
                .Where(kv => kv.KnowledgeId == item.Id)
                .OrderByDescending(kv => kv.IsPrimary)
                .FirstOrDefaultAsync(ct);

            string? vaultName = primaryKv?.Vault?.Name;
            Guid? vaultId = primaryKv?.VaultId;

            List<Guid>? ancestorVaultIds = null;
            if (vaultId.HasValue)
            {
                ancestorVaultIds = await _db.VaultAncestors
                    .Where(va => va.DescendantVaultId == vaultId.Value)
                    .Select(va => va.AncestorVaultId)
                    .ToListAsync(ct);
            }

            var topicName = await _db.KnowledgeItems
                .Where(k => k.Id == item.Id)
                .Select(k => k.Topic != null ? k.Topic.Name : null)
                .FirstOrDefaultAsync(ct);

            var currentTags = item.Tags.Select(t => t.Name).ToList();

            // Load existing chunks BEFORE delete (DB is immediately consistent, unlike search index)
            var existingChunks = await _db.ContentChunks
                .Where(c => c.KnowledgeId == item.Id)
                .ToListAsync(ct);
            var existingHashMap = existingChunks
                .Where(c => c.EmbeddingVectorJson != null)
                .ToDictionary(c => c.ContentHash, c => c.EmbeddingVectorJson!);

            await _searchService.DeleteDocumentWithChunksAsync(item.Id,
                existingChunks.Select(c => c.Position), ct);

            // Gather attachment text for indexing (matches EnrichmentBackgroundService.ReindexAsync pattern)
            var attachmentText = string.Empty;
            try
            {
                attachmentText = await EnrichmentBackgroundService.GetAllAttachmentTextAsync(_db, item.Id, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load attachment text for {Id}, indexing content only", item.Id);
            }

            var contentForChunking = string.IsNullOrEmpty(attachmentText)
                ? item.Content
                : $"{item.Content}\n\n{attachmentText}";

            var strategy = _chunkingService.DetermineStrategy(item.Type);
            var chunks = _chunkingService.ChunkWithContext(
                contentForChunking, item.Title, item.Summary,
                currentTags, strategy);
            _logger.LogInformation("Re-indexing knowledge {Id} as {ChunkCount} chunk(s)", item.Id, chunks.Count);

            var chunkData = new Dictionary<int, (string Hash, string? EmbeddingJson)>();

            foreach (var chunk in chunks)
            {
                var hash = ContentHasher.Hash(chunk.Content);
                float[]? embedding;
                string? embeddingJson;

                if (existingHashMap.TryGetValue(hash, out var cachedVector))
                {
                    embeddingJson = cachedVector;
                    try
                    {
                        embedding = JsonSerializer.Deserialize<float[]>(cachedVector);
                    }
                    catch (JsonException)
                    {
                        embedding = await _openAIService.GenerateEmbeddingAsync(chunk.EmbeddingText, ct);
                        embeddingJson = embedding != null ? JsonSerializer.Serialize(embedding) : null;
                    }
                }
                else
                {
                    embedding = await _openAIService.GenerateEmbeddingAsync(chunk.EmbeddingText, ct);
                    embeddingJson = embedding != null ? JsonSerializer.Serialize(embedding) : null;
                }

                chunkData[chunk.Position] = (hash, embeddingJson);

                // FEAT_SelfHostedTemporalAwareness: thread entity dates
                await _searchService.IndexDocumentAsync(
                    item.Id, item.Title, chunk.Content, item.Summary,
                    vaultName, vaultId, ancestorVaultIds, topicName,
                    currentTags, item.Type.ToString(),
                    item.FilePath, embedding,
                    chunkIndex: chunks.Count > 1 ? chunk.Position : null,
                    knowledgeCreatedAt: item.CreatedAt,
                    knowledgeUpdatedAt: item.UpdatedAt,
                    cancellationToken: ct);
            }

            // Replace persisted chunks (now includes freshly generated embeddings)
            try
            {
                _db.ContentChunks.RemoveRange(existingChunks);
                foreach (var chunk in chunks)
                {
                    var (hash, embeddingJson) = chunkData[chunk.Position];
                    _db.ContentChunks.Add(new ContentChunk
                    {
                        TenantId = item.TenantId,
                        KnowledgeId = item.Id,
                        Position = chunk.Position,
                        Content = chunk.Content,
                        ContentHash = hash,
                        EmbeddingVectorJson = embeddingJson,
                        EmbeddedAt = embeddingJson != null ? DateTime.UtcNow : null
                    });
                }
            }
            catch (Exception chunkEx)
            {
                _logger.LogWarning(chunkEx, "Failed to persist chunks for knowledge {Id}", item.Id);
            }

            item.IsIndexed = true;
            item.IndexedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to re-index knowledge item {Id}", item.Id);
        }

        // Enqueue for background AI re-enrichment
        try
        {
            if (_enrichmentWriter != null)
            {
                await _enrichmentWriter.EnqueueAsync(item.Id, item.TenantId, forceReset, ct);
                _logger.LogDebug("Enqueued knowledge {Id} for re-enrichment (forceReset={ForceReset})", item.Id, forceReset);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enqueue knowledge {Id} for re-enrichment", item.Id);
        }
    }

    internal IQueryable<Knowledge> BuildFilteredQuery(
        string? knowledgeType, string? titlePattern, string? fileNamePattern,
        string? startDateStr, string? endDateStr)
    {
        var query = _db.KnowledgeItems.AsQueryable();

        if (!string.IsNullOrEmpty(knowledgeType) && Enum.TryParse<KnowledgeType>(knowledgeType, true, out var kt))
            query = query.Where(k => k.Type == kt);

        if (!string.IsNullOrEmpty(titlePattern))
        {
            // Auto-wrap plain text with wildcards for substring matching
            var likePattern = titlePattern.Contains('*') || titlePattern.Contains('?')
                ? ConvertWildcardToLike(titlePattern)
                : $"%{titlePattern}%";
            query = query.Where(k => EF.Functions.Like(k.Title, likePattern));
        }

        if (!string.IsNullOrEmpty(fileNamePattern))
            query = query.Where(k => k.FilePath != null && EF.Functions.Like(k.FilePath, ConvertWildcardToLike(fileNamePattern)));

        if (ParseDateTime(startDateStr) is { } startDate)
            query = query.Where(k => k.CreatedAt >= startDate);

        if (ParseDateTime(endDateStr) is { } endDate)
            query = query.Where(k => k.CreatedAt <= endDate);

        return query;
    }

    internal static string ConvertWildcardToLike(string pattern)
    {
        return pattern
            .Replace("**", "%")
            .Replace("*", "%")
            .Replace("?", "_");
    }

    internal static Guid? ParseGuid(string? value)
        => Guid.TryParse(value, out var g) ? g : null;

    internal static DateTime? ParseDateTime(string? value)
        => DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
            ? d : null;

    private static KnowledgeItemResponse MapToResponse(Knowledge item) => new(
        item.Id, item.Title, item.Content, item.Summary,
        item.BriefSummary,
        item.SummaryRefinementGuidance,
        item.Type.ToString(), item.Source, item.FilePath,
        item.Topic != null ? new TopicRef(item.Topic.Id, item.Topic.Name) : null,
        item.Tags.Select(t => t.Name),
        item.KnowledgeVaults.Select(kv => new VaultRef(kv.Vault.Id, kv.Vault.Name, kv.IsPrimary)),
        item.CreatedAt, item.UpdatedAt,
        item.IsIndexed, item.IndexedAt);
}
