using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Knowz.Core.Interfaces;
using Knowz.Core.Models;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// Vector + keyword hybrid search using embeddings stored in ContentChunks.
/// Used in Platform proxy mode where embeddings are generated via PlatformAIService
/// and stored locally in the database. Computes cosine similarity between the query
/// embedding and chunk embeddings, then fuses with keyword scores.
/// </summary>
public class LocalVectorSearchService : ISearchService
{
    private readonly SelfHostedDbContext _db;
    private readonly Guid _tenantId;
    private readonly ILogger<LocalVectorSearchService> _logger;

    internal const double VectorWeight = 0.7;
    internal const double KeywordWeight = 0.3;

    public LocalVectorSearchService(
        SelfHostedDbContext db,
        ITenantProvider tenantProvider,
        ILogger<LocalVectorSearchService> logger)
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
            // Build base query for knowledge items (tenant-scoped via global query filter)
            var baseQuery = _db.KnowledgeItems.AsQueryable();

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

            // Get matching knowledge item IDs
            var knowledgeIds = await baseQuery
                .Select(k => k.Id)
                .ToListAsync(cancellationToken);

            if (knowledgeIds.Count == 0)
                return new List<SearchResultItem>();

            // Load chunks for matching knowledge items (include ContextSummary for keyword scoring)
            var chunks = await _db.ContentChunks
                .Where(c => knowledgeIds.Contains(c.KnowledgeId))
                .Select(c => new
                {
                    c.KnowledgeId,
                    c.Content,
                    c.EmbeddingVectorJson,
                    c.Position,
                    c.ContextSummary
                })
                .ToListAsync(cancellationToken);

            // Load knowledge item metadata for results
            // FEAT_SelfHostedTemporalAwareness: also project UpdatedAt
            // FEAT_SelfHostedRetrievalPolicy: also project BriefSummary for field-weighted scoring
            var knowledgeItems = await baseQuery
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

            var knowledgeMap = knowledgeItems.ToDictionary(k => k.Id);

            // Aggregate best ContextSummary per knowledge item from chunks
            var contextSummaryMap = chunks
                .Where(c => c.ContextSummary != null)
                .GroupBy(c => c.KnowledgeId)
                .ToDictionary(
                    g => g.Key,
                    g => string.Join(" ", g.Select(c => c.ContextSummary)));

            // Score each knowledge item using vector similarity + keyword matching
            var scoredResults = new Dictionary<Guid, (double VectorScore, double KeywordScore)>();

            foreach (var item in knowledgeItems)
            {
                contextSummaryMap.TryGetValue(item.Id, out var contextSummary);
                var keywordScore = ComputeFieldWeightedScore(item.Title, item.Summary, item.Content, contextSummary, query,
                    briefSummary: item.BriefSummary);
                scoredResults[item.Id] = (0.0, keywordScore);
            }

            // Prefer pgvector ANN when the provider is Postgres and a query vector is present.
            // Failures / empty ranks fall back to in-process JSON cosine (not keyword-only).
            var rankedByPgvector = false;
            if (queryEmbedding != null
                && string.Equals(_db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
            {
                try
                {
                    rankedByPgvector = await TryApplyPgvectorRanksAsync(
                        knowledgeIds, queryEmbedding, scoredResults, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "pgvector rank failed for query '{Query}' (tenant {TenantId}); falling back to JSON cosine",
                        query, _tenantId);
                    rankedByPgvector = false;
                }
            }

            // Compute vector scores from chunks (only those with embeddings)
            if (!rankedByPgvector && queryEmbedding != null && chunks.Count > 0)
            {
                foreach (var chunk in chunks.Where(c => c.EmbeddingVectorJson != null))
                {
                    float[]? chunkEmbedding;
                    try
                    {
                        chunkEmbedding = JsonSerializer.Deserialize<float[]>(chunk.EmbeddingVectorJson!);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (chunkEmbedding == null)
                        continue;

                    var similarity = CosineSimilarity(queryEmbedding, chunkEmbedding);

                    // Keep the best chunk score per knowledge item
                    if (scoredResults.TryGetValue(chunk.KnowledgeId, out var existing))
                    {
                        if (similarity > existing.VectorScore)
                        {
                            scoredResults[chunk.KnowledgeId] = (similarity, existing.KeywordScore);
                        }
                    }
                }
            }

            // Fuse scores and build results
            var results = scoredResults
                .Where(kvp => knowledgeMap.ContainsKey(kvp.Key))
                .Select(kvp =>
                {
                    var item = knowledgeMap[kvp.Key];
                    var (vectorScore, keywordScore) = kvp.Value;

                    // If we have vector scores, use weighted fusion. Otherwise, keyword only.
                    var hasVector = queryEmbedding != null && vectorScore > 0;
                    var fusedScore = hasVector
                        ? (vectorScore * VectorWeight) + (keywordScore * KeywordWeight)
                        : keywordScore;

                    return new SearchResultItem
                    {
                        KnowledgeId = item.Id,
                        Title = item.Title,
                        Content = item.Content,
                        Summary = item.Summary,
                        FilePath = item.FilePath,
                        Score = fusedScore,
                        Highlights = new List<string>(),
                        Tags = new List<string>(),
                        CreatedAt = item.CreatedAt,
                        UpdatedAt = item.UpdatedAt,
                    };
                })
                .Where(r => r.Score > 0)
                .OrderByDescending(r => r.Score)
                .Take(maxResults)
                .ToList();

            _logger.LogInformation(
                "Local vector search returned {Count} results for query '{Query}' (tenant {TenantId}, {ChunkCount} chunks scored)",
                results.Count, query, _tenantId, chunks.Count);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Local vector search failed for query '{Query}' (tenant {TenantId}), falling back to keyword search",
                query, _tenantId);

            // Fallback to keyword-only search on failure
            return await KeywordOnlySearchAsync(query, vaultId, includeDescendants,
                tags, requireAllTags, startDate, endDate, maxResults, cancellationToken);
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
        // No-op: embeddings are stored directly in ContentChunks by the enrichment pipeline
        _logger.LogDebug(
            "IndexDocumentAsync is a no-op for LocalVectorSearchService. Document '{KnowledgeId}' embeddings are in ContentChunks.",
            knowledgeId);
        return Task.CompletedTask;
    }

    public Task DeleteDocumentAsync(
        Guid knowledgeId,
        CancellationToken cancellationToken = default)
    {
        // No-op: ContentChunks cascade-delete with KnowledgeItem
        _logger.LogDebug(
            "DeleteDocumentAsync is a no-op for LocalVectorSearchService. Document '{KnowledgeId}' deletion is handled by the database.",
            knowledgeId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Ranks already-filtered knowledge IDs with pgvector cosine distance and writes
    /// similarity = 1 - distance into <paramref name="scoredResults"/>. Returns false
    /// when no rows are returned so the caller can fall back to JSON cosine.
    /// </summary>
    private async Task<bool> TryApplyPgvectorRanksAsync(
        IReadOnlyCollection<Guid> knowledgeIds,
        float[] queryEmbedding,
        Dictionary<Guid, (double VectorScore, double KeywordScore)> scoredResults,
        CancellationToken cancellationToken)
    {
        if (knowledgeIds.Count == 0)
            return false;

        var connection = _db.Database.GetDbConnection();
        var openedHere = false;
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
            openedHere = true;
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = SelfHostedPgvectorDdl.RankKnowledgeIdsSql(queryEmbedding.Length);
            command.Parameters.Add(new NpgsqlParameter("qvec", FormatPgvectorLiteral(queryEmbedding)));
            command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            {
                Value = knowledgeIds as Guid[] ?? knowledgeIds.ToArray()
            });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var idOrdinal = reader.GetOrdinal("KnowledgeId");
            var distanceOrdinal = reader.GetOrdinal("Distance");
            var applied = false;

            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(idOrdinal) || reader.IsDBNull(distanceOrdinal))
                    continue;

                var id = reader.GetGuid(idOrdinal);
                var distance = Convert.ToDouble(reader.GetValue(distanceOrdinal), CultureInfo.InvariantCulture);
                var similarity = 1.0 - distance;
                if (scoredResults.TryGetValue(id, out var existing) && similarity > existing.VectorScore)
                    scoredResults[id] = (similarity, existing.KeywordScore);
                applied = true;
            }

            return applied;
        }
        finally
        {
            if (openedHere && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    internal static string FormatPgvectorLiteral(float[] values)
    {
        var sb = new StringBuilder(2 + values.Length * 12);
        sb.Append('[');
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append(values[i].ToString("G9", CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// Computes cosine similarity between two vectors.
    /// Returns a value between -1 and 1, where 1 means identical direction.
    /// </summary>
    internal static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0;

        double dotProduct = 0;
        double normA = 0;
        double normB = 0;

        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * (double)b[i];
            normA += a[i] * (double)a[i];
            normB += b[i] * (double)b[i];
        }

        var denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        if (denominator == 0)
            return 0;

        return dotProduct / denominator;
    }

    /// <summary>
    /// Computes a term-frequency field-weighted score.
    /// Splits query into terms, counts occurrences per field, normalizes by field length,
    /// applies field boost weights, and averages across terms.
    /// Field boosts: title=3.0, content=2.5, briefSummary=2.5, summary=2.0, contextSummary=1.5.
    /// </summary>
    internal static double ComputeFieldWeightedScore(
        string title, string? summary, string content, string? contextSummary, string query,
        string? briefSummary = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return 0.0;

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
            return 0.0;

        var totalScore = 0.0;

        foreach (var term in terms)
        {
            var termScore = 0.0;
            termScore += ComputeFieldTermScore(title, term, 3.0);
            termScore += ComputeFieldTermScore(content, term, 2.5);
            // BriefSummary: high-signal condensed summary for search quality
            // WorkGroupID: kc-feat-selfhosted-retrieval-policy-20260413-030000
            termScore += ComputeFieldTermScore(briefSummary, term, 2.5);
            termScore += ComputeFieldTermScore(summary, term, 2.0);
            termScore += ComputeFieldTermScore(contextSummary, term, 1.5);
            totalScore += termScore;
        }

        return totalScore / terms.Length;
    }

    private static double ComputeFieldTermScore(string? field, string term, double boost)
    {
        if (string.IsNullOrEmpty(field))
            return 0.0;

        var occurrences = CountOccurrences(field, term);
        if (occurrences == 0)
            return 0.0;

        // Normalize: occurrences per 100 characters of field length
        var normalizedLength = Math.Max(field.Length / 100.0, 1.0);
        var termFrequency = occurrences / normalizedLength;

        return termFrequency * boost;
    }

    private static int CountOccurrences(string text, string term)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += term.Length;
        }
        return count;
    }

    /// <summary>
    /// Keyword-only fallback when vector search fails.
    /// </summary>
    private async Task<List<SearchResultItem>> KeywordOnlySearchAsync(
        string query,
        Guid? vaultId,
        bool includeDescendants,
        IEnumerable<string>? tags,
        bool requireAllTags,
        DateTime? startDate,
        DateTime? endDate,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
            return new List<SearchResultItem>();

        var baseQuery = _db.KnowledgeItems.AsQueryable();
        foreach (var term in terms)
        {
            var pattern = $"%{term}%";
            baseQuery = baseQuery.Where(k =>
                EF.Functions.Like(k.Title, pattern) ||
                EF.Functions.Like(k.Content, pattern) ||
                (k.Summary != null && EF.Functions.Like(k.Summary, pattern)) ||
                (k.BriefSummary != null && EF.Functions.Like(k.BriefSummary, pattern)));
        }

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

        var tagList = tags?.ToList();
        if (tagList is { Count: > 0 })
        {
            if (requireAllTags)
            {
                foreach (var tagName in tagList)
                    baseQuery = baseQuery.Where(k => k.Tags.Any(t => t.Name == tagName));
            }
            else
            {
                baseQuery = baseQuery.Where(k => k.Tags.Any(t => tagList.Contains(t.Name)));
            }
        }

        if (startDate.HasValue)
            baseQuery = baseQuery.Where(k => k.CreatedAt >= startDate.Value);
        if (endDate.HasValue)
            baseQuery = baseQuery.Where(k => k.CreatedAt <= endDate.Value);

        var items = await baseQuery
            .Select(k => new { k.Id, k.Title, k.Content, k.Summary, k.BriefSummary, k.FilePath, k.CreatedAt, k.UpdatedAt })
            .ToListAsync(cancellationToken);

        return items.Select(k =>
            {
                var score = ComputeFieldWeightedScore(k.Title, k.Summary, k.Content, null, query,
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
    }
}
