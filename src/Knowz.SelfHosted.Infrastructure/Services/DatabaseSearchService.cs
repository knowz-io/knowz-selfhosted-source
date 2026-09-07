using Knowz.Core.Interfaces;
using Knowz.Core.Models;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// Database-driven search fallback when Azure AI Search is not configured.
/// Uses SQL LIKE queries against title, content, and summary columns.
/// No vector/semantic search — just keyword matching with relevance scoring.
/// </summary>
public class DatabaseSearchService : ISearchService
{
    private readonly SelfHostedDbContext _db;
    private readonly ILogger<DatabaseSearchService> _logger;

    public DatabaseSearchService(SelfHostedDbContext db, ILogger<DatabaseSearchService> logger)
    {
        _db = db;
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
            return [];

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Match any term in any field (OR across terms, OR across fields)
        var dbQuery = _db.KnowledgeItems.AsQueryable();
        foreach (var term in terms)
        {
            var pattern = $"%{term}%";
            dbQuery = dbQuery.Where(k =>
                EF.Functions.Like(k.Title, pattern)
                || (k.Content != null && EF.Functions.Like(k.Content, pattern))
                || (k.Summary != null && EF.Functions.Like(k.Summary, pattern))
                || (k.BriefSummary != null && EF.Functions.Like(k.BriefSummary, pattern)));
        }

        if (vaultId.HasValue)
        {
            dbQuery = dbQuery.Where(k =>
                _db.KnowledgeVaults.Any(kv => kv.KnowledgeId == k.Id && kv.VaultId == vaultId.Value));
        }

        if (startDate.HasValue)
            dbQuery = dbQuery.Where(k => k.CreatedAt >= startDate.Value);

        if (endDate.HasValue)
            dbQuery = dbQuery.Where(k => k.CreatedAt <= endDate.Value);

        var items = await dbQuery
            .Select(k => new
            {
                k.Id,
                k.Title,
                Content = k.Content ?? string.Empty,
                Summary = k.Summary,
                k.BriefSummary,
                k.Type,
                k.FilePath,
                // FEAT_SelfHostedTemporalAwareness: project entity dates
                // directly so chat context can cite accurate timestamps.
                k.CreatedAt,
                k.UpdatedAt,
                VaultName = _db.KnowledgeVaults
                    .Where(kv => kv.KnowledgeId == k.Id)
                    .Select(kv => kv.Vault!.Name)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return items.Select((r, i) =>
        {
            var score = LocalVectorSearchService.ComputeFieldWeightedScore(
                r.Title, r.Summary, r.Content, null, query,
                briefSummary: r.BriefSummary);
            return new SearchResultItem
            {
                KnowledgeId = r.Id,
                Title = r.Title,
                Content = Truncate(r.Content, 500),
                Summary = r.Summary,
                VaultName = r.VaultName,
                KnowledgeType = r.Type.ToString(),
                FilePath = r.FilePath,
                Score = score,
                Position = i,
                DocumentType = "database",
                CreatedAt = r.CreatedAt,
                UpdatedAt = r.UpdatedAt,
            };
        })
        .OrderByDescending(r => r.Score)
        .Take(maxResults)
        .ToList();
    }

    public Task IndexDocumentAsync(
        Guid knowledgeId, string title, string content, string? summary,
        string? vaultName, Guid? vaultId, List<Guid>? ancestorVaultIds,
        string? topicName, IEnumerable<string>? tags, string? knowledgeType,
        string? filePath, float[]? contentVector, int? chunkIndex = null,
        DateTime? knowledgeCreatedAt = null,
        DateTime? knowledgeUpdatedAt = null,
        CancellationToken cancellationToken = default)
    {
        // No external index to update — data is already in the database.
        // knowledgeCreatedAt/knowledgeUpdatedAt are ignored: the EF
        // projection reads them directly from the entity in HybridSearchAsync.
        return Task.CompletedTask;
    }

    public Task DeleteDocumentAsync(Guid knowledgeId, CancellationToken cancellationToken = default)
    {
        // No external index to update — deletion is handled by EF
        return Task.CompletedTask;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
