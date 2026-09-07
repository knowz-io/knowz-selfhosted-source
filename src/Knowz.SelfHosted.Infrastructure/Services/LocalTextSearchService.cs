using Knowz.Core.Interfaces;
using Knowz.Core.Models;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// EF Core keyword-based search implementation of ISearchService.
/// Uses SQL LIKE queries against the Knowledge table for basic text search
/// when Azure AI Search is not configured.
/// </summary>
public class LocalTextSearchService : ISearchService
{
    private readonly SelfHostedDbContext _db;
    private readonly Guid _tenantId;
    private readonly ILogger<LocalTextSearchService> _logger;

    public LocalTextSearchService(
        SelfHostedDbContext db,
        ITenantProvider tenantProvider,
        ILogger<LocalTextSearchService> logger)
    {
        _db = db;
        _tenantId = tenantProvider.TenantId;
        _logger = logger;
    }

    public async Task<List<SearchResultItem>> HybridSearchAsync(
        string query,
        float[]? queryEmbedding = null,
        Guid? vaultId = null,
        bool includeDescendants = true,
        IEnumerable<string>? tags = null,
        bool requireAllTags = false,
        DateTime? startDate = null,
        DateTime? endDate = null,
        int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<SearchResultItem>();

        try
        {
            var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (terms.Length == 0)
                return new List<SearchResultItem>();

            // Base query with per-term keyword filter including BriefSummary and ContextSummary from ContentChunks
            var baseQuery = _db.KnowledgeItems.AsQueryable();
            foreach (var term in terms)
            {
                var pattern = $"%{term}%";
                baseQuery = baseQuery.Where(k =>
                    EF.Functions.Like(k.Title, pattern) ||
                    EF.Functions.Like(k.Content, pattern) ||
                    (k.Summary != null && EF.Functions.Like(k.Summary, pattern)) ||
                    (k.BriefSummary != null && EF.Functions.Like(k.BriefSummary, pattern)) ||
                    _db.ContentChunks.Any(c => c.KnowledgeId == k.Id &&
                        c.ContextSummary != null && EF.Functions.Like(c.ContextSummary, pattern)));
            }

            // Apply vault filter
            if (vaultId.HasValue)
            {
                var vaultIds = new List<Guid> { vaultId.Value };

                if (includeDescendants)
                {
                    var descendantIds = await _db.VaultAncestors
                        .Where(va => va.AncestorVaultId == vaultId.Value)
                        .Select(va => va.DescendantVaultId)
                        .ToListAsync(cancellationToken);
                    vaultIds.AddRange(descendantIds);
                }

                var knowledgeIdsInVaults = _db.KnowledgeVaults
                    .Where(kv => vaultIds.Contains(kv.VaultId))
                    .Select(kv => kv.KnowledgeId);

                baseQuery = baseQuery.Where(k => knowledgeIdsInVaults.Contains(k.Id));
            }

            // Apply tag filter
            var tagList = tags?.ToList();
            if (tagList is { Count: > 0 })
            {
                if (requireAllTags)
                {
                    foreach (var tagName in tagList)
                    {
                        baseQuery = baseQuery.Where(k =>
                            k.Tags.Any(t => t.Name == tagName));
                    }
                }
                else
                {
                    baseQuery = baseQuery.Where(k =>
                        k.Tags.Any(t => tagList.Contains(t.Name)));
                }
            }

            // Apply date filters
            if (startDate.HasValue)
                baseQuery = baseQuery.Where(k => k.CreatedAt >= startDate.Value);

            if (endDate.HasValue)
                baseQuery = baseQuery.Where(k => k.CreatedAt <= endDate.Value);

            // Materialize results for client-side scoring
            // FEAT_SelfHostedTemporalAwareness: also project UpdatedAt
            var items = await baseQuery
                .Select(k => new
                {
                    k.Id,
                    k.Title,
                    k.Content,
                    k.Summary,
                    k.BriefSummary,
                    k.FilePath,
                    k.CreatedAt,
                    k.UpdatedAt
                })
                .ToListAsync(cancellationToken);

            // Load ContextSummary from ContentChunks for matched items
            var itemIds = items.Select(k => k.Id).ToList();
            var contextSummaryMap = (await _db.ContentChunks
                .Where(c => itemIds.Contains(c.KnowledgeId) && c.ContextSummary != null)
                .Select(c => new { c.KnowledgeId, c.ContextSummary })
                .ToListAsync(cancellationToken))
                .GroupBy(c => c.KnowledgeId)
                .ToDictionary(
                    g => g.Key,
                    g => string.Join(" ", g.Select(c => c.ContextSummary)));

            // Compute field-weighted scores using shared scoring logic
            var results = items.Select(k =>
            {
                contextSummaryMap.TryGetValue(k.Id, out var contextSummary);
                var score = LocalVectorSearchService.ComputeFieldWeightedScore(
                    k.Title, k.Summary, k.Content, contextSummary, query,
                    briefSummary: k.BriefSummary);
                return new SearchResultItem
                {
                    KnowledgeId = k.Id,
                    Title = k.Title,
                    Content = k.Content,
                    Summary = k.Summary,
                    FilePath = k.FilePath,
                    Score = score,
                    Highlights = new List<string>(),
                    Tags = new List<string>(),
                    CreatedAt = k.CreatedAt,
                    UpdatedAt = k.UpdatedAt,
                };
            })
            .OrderByDescending(r => r.Score)
            .Take(maxResults)
            .ToList();

            _logger.LogInformation(
                "Local text search returned {Count} results for query '{Query}' (tenant {TenantId})",
                results.Count, query, _tenantId);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Local text search failed for query '{Query}' (tenant {TenantId})",
                query, _tenantId);
            return new List<SearchResultItem>();
        }
    }

    public Task IndexDocumentAsync(
        Guid knowledgeId,
        string title,
        string content,
        string? summary,
        string? vaultName,
        Guid? vaultId,
        List<Guid>? ancestorVaultIds,
        string? topicName,
        IEnumerable<string>? tags,
        string? knowledgeType,
        string? filePath,
        float[]? contentVector,
        int? chunkIndex = null,
        DateTime? knowledgeCreatedAt = null,
        DateTime? knowledgeUpdatedAt = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "IndexDocumentAsync is a no-op for LocalTextSearchService. Document '{KnowledgeId}' is already in the database.",
            knowledgeId);
        return Task.CompletedTask;
    }

    public Task DeleteDocumentAsync(
        Guid knowledgeId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "DeleteDocumentAsync is a no-op for LocalTextSearchService. Document '{KnowledgeId}' deletion is handled by the database.",
            knowledgeId);
        return Task.CompletedTask;
    }

}
