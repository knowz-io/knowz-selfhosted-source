using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Knowz.Core.Interfaces;
using Knowz.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// Azure AI Search implementation of ISearchService.
/// Query-only for search_knowledge; also supports index upsert for write tools.
/// </summary>
public class AzureSearchService : ISearchService
{
    private readonly SearchClient _searchClient;
    private readonly SearchIndexClient _indexClient;
    private readonly ILogger<AzureSearchService> _logger;
    private readonly Guid _tenantId;
    private readonly string _indexName;
    private readonly int _vectorDimensions;
    private readonly int _searchTimeoutSeconds;

    private const string SemanticConfigName = "selfhosted-semantic";

    private static readonly SemaphoreSlim _indexEnsureLock = new(1, 1);
    private static volatile bool _indexEnsured;

    public AzureSearchService(
        SearchClient searchClient,
        SearchIndexClient indexClient,
        ILogger<AzureSearchService> logger,
        ITenantProvider tenantProvider,
        IConfiguration configuration)
    {
        _searchClient = searchClient;
        _indexClient = indexClient;
        _logger = logger;
        _tenantId = tenantProvider.TenantId;
        _indexName = searchClient.IndexName;
        _vectorDimensions = configuration.GetValue<int?>("Embedding:Dimensions")
            ?? throw new InvalidOperationException(
                "Embedding:Dimensions is required but was not configured. " +
                "Set Embedding:Dimensions (e.g. 1536 for text-embedding-3-small, " +
                "3072 for text-embedding-3-large) in appsettings / environment. " +
                "See ARCH_EmbeddingConfigOwnership for parity rules with the main platform.");
        _searchTimeoutSeconds = Math.Clamp(
            configuration.GetValue<int?>("AzureSearch:VectorSearchTimeoutSeconds")
                ?? configuration.GetValue<int?>("AzureAISearch:VectorSearchTimeoutSeconds")
                ?? 10,
            2,
            60);
    }

    /// <inheritdoc />
    public virtual async Task<List<SearchResultItem>> HybridSearchAsync(
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
        await EnsureIndexExistsAsync(cancellationToken);

        var filter = BuildFilter(vaultId, includeDescendants, tags, requireAllTags, startDate, endDate);

        var searchOptions = new SearchOptions
        {
            Filter = filter,
            Size = maxResults,
            Select = {
                "knowledgeId", "title", "content", "vaultName", "topicName",
                "tags", "knowledgeTypeId", "filePath", "aiSummary",
                "position", "documentType", "createdAt", "updatedAt"
            },
            QueryType = SearchQueryType.Simple,
            SearchMode = Azure.Search.Documents.Models.SearchMode.Any,
            SearchFields = { "title", "content", "aiSummary", "filePath", "tags", "topicName" },
            IncludeTotalCount = true,
            HighlightFields = { "title", "content", "aiSummary" }
        };

        try
        {
            searchOptions.QueryType = SearchQueryType.Semantic;
            searchOptions.SemanticSearch = new()
            {
                SemanticConfigurationName = SemanticConfigName,
                QueryCaption = new(QueryCaptionType.Extractive),
                MaxWait = TimeSpan.FromSeconds(2)
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Semantic ranking unavailable, using simple query");
            searchOptions.QueryType = SearchQueryType.Simple;
        }

        if (queryEmbedding != null)
        {
            var vectorQuery = new VectorizedQuery(queryEmbedding)
            {
                Fields = { "contentVector" },
                KNearestNeighborsCount = maxResults * 2,
                Exhaustive = false
            };
            searchOptions.VectorSearch = new() { Queries = { vectorQuery } };
        }

        _logger.LogInformation(
            "Hybrid search: query='{Query}', vault={VaultId}, vector={HasVector}, maxResults={Max}",
            query, vaultId, queryEmbedding != null, maxResults);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_searchTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        Azure.Response<SearchResults<SearchDocument>> response;
        try
        {
            response = await _searchClient.SearchAsync<SearchDocument>(
                query, searchOptions, linkedCts.Token);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 400 &&
            ex.Message.Contains("Semantic", StringComparison.OrdinalIgnoreCase))
        {
            // Semantic search not available on this SKU — fall back to simple query
            if (linkedCts.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Azure Search timed out after {TimeoutSeconds}s before semantic fallback; returning empty results for query '{Query}'",
                    _searchTimeoutSeconds,
                    query);
                return new List<SearchResultItem>();
            }

            _logger.LogWarning("Semantic search not available (SKU may be basic), falling back to simple query");
            searchOptions.QueryType = SearchQueryType.Simple;
            searchOptions.SemanticSearch = null;
            try
            {
                response = await _searchClient.SearchAsync<SearchDocument>(
                    query, searchOptions, linkedCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Azure Search semantic fallback timed out after {TimeoutSeconds}s; returning empty results for query '{Query}'",
                    _searchTimeoutSeconds,
                    query);
                return new List<SearchResultItem>();
            }
            catch (RequestFailedException fallbackEx) when (IsTransientSearchFailure(fallbackEx))
            {
                _logger.LogWarning(
                    fallbackEx,
                    "Transient Azure Search semantic fallback failure status={Status}; returning empty results for query '{Query}'",
                    fallbackEx.Status,
                    query);
                return new List<SearchResultItem>();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Azure Search timed out after {TimeoutSeconds}s; returning empty results for query '{Query}'",
                _searchTimeoutSeconds,
                query);
            return new List<SearchResultItem>();
        }
        catch (RequestFailedException ex) when (IsTransientSearchFailure(ex))
        {
            _logger.LogWarning(
                ex,
                "Transient Azure Search failure after {TimeoutSeconds}s budget status={Status}; returning empty results for query '{Query}'",
                _searchTimeoutSeconds,
                ex.Status,
                query);
            return new List<SearchResultItem>();
        }

        var results = new List<SearchResultItem>();
        await foreach (var result in response.Value.GetResultsAsync())
        {
            results.Add(MapResult(result));
        }

        _logger.LogInformation("Search returned {Count} results for query '{Query}'",
            results.Count, query);

        return results;
    }

    /// <inheritdoc />
    public virtual async Task IndexDocumentAsync(
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
        await EnsureIndexExistsAsync(cancellationToken);

        // FEAT_SelfHostedTemporalAwareness: Prefer the entity's real dates.
        // Fall back to DateTimeOffset.UtcNow only when the caller did not
        // pass them (legacy call sites). Without this, re-index operations
        // overwrite the index createdAt with the re-index time, breaking
        // temporal queries like "what did I save yesterday".
        var indexCreatedAt = knowledgeCreatedAt.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(knowledgeCreatedAt.Value, DateTimeKind.Utc), TimeSpan.Zero)
            : DateTimeOffset.UtcNow;
        var indexUpdatedAt = knowledgeUpdatedAt.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(knowledgeUpdatedAt.Value, DateTimeKind.Utc), TimeSpan.Zero)
            : DateTimeOffset.UtcNow;

        var document = new SearchDocument
        {
            ["id"] = BuildDocumentId(knowledgeId, chunkIndex),
            ["tenantId"] = _tenantId.ToString(),
            ["knowledgeId"] = knowledgeId.ToString(),
            ["title"] = title,
            ["content"] = content,
            ["aiSummary"] = summary ?? string.Empty,
            ["vaultName"] = vaultName ?? string.Empty,
            ["vaultId"] = vaultId?.ToString() ?? string.Empty,
            ["vaultIds"] = vaultId.HasValue
                ? new[] { vaultId.Value.ToString() }
                : Array.Empty<string>(),
            ["ancestorVaultIds"] = ancestorVaultIds?.Select(v => v.ToString()).ToArray()
                                   ?? Array.Empty<string>(),
            ["topicName"] = topicName ?? string.Empty,
            ["tags"] = tags?.ToArray() ?? Array.Empty<string>(),
            ["knowledgeTypeId"] = knowledgeType ?? string.Empty,
            ["filePath"] = filePath ?? string.Empty,
            ["createdAt"] = indexCreatedAt,
            ["updatedAt"] = indexUpdatedAt,
            ["documentType"] = chunkIndex.HasValue ? "chunk" : "knowledge",
            ["position"] = chunkIndex ?? 0
        };

        if (contentVector != null)
        {
            document["contentVector"] = contentVector;
        }

        var batch = IndexDocumentsBatch.Upload(new[] { document });
        await _searchClient.IndexDocumentsAsync(batch, cancellationToken: cancellationToken);

        _logger.LogInformation("Indexed document {KnowledgeId} with title '{Title}'",
            knowledgeId, title);
    }

    /// <inheritdoc />
    public virtual async Task DeleteDocumentAsync(
        Guid knowledgeId,
        CancellationToken cancellationToken = default)
    {
        var mainDocId = BuildDocumentId(knowledgeId);
        var idsToDelete = new List<string> { mainDocId };

        try
        {
            var searchOptions = new SearchOptions
            {
                Filter = $"tenantId eq '{_tenantId}' and knowledgeId eq '{knowledgeId}'",
                Select = { "id" },
                Size = 1000
            };
            var response = await _searchClient.SearchAsync<SearchDocument>(
                "*", searchOptions, cancellationToken);

            await foreach (var result in response.Value.GetResultsAsync())
            {
                var docId = result.Document.GetString("id");
                if (docId != null && docId != mainDocId)
                {
                    idsToDelete.Add(docId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to find chunk documents for {KnowledgeId}, deleting main doc only", knowledgeId);
        }

        var batch = IndexDocumentsBatch.Delete("id", idsToDelete);
        await _searchClient.IndexDocumentsAsync(batch, cancellationToken: cancellationToken);

        _logger.LogInformation("Deleted {Count} documents for {KnowledgeId} from search index",
            idsToDelete.Count, knowledgeId);
    }

    /// <inheritdoc />
    public virtual async Task DeleteDocumentAsync(
        Guid knowledgeId,
        IEnumerable<int> chunkPositions,
        CancellationToken cancellationToken = default)
    {
        var mainDocId = BuildDocumentId(knowledgeId);
        var idsToDelete = new List<string> { mainDocId };

        // Deterministically build chunk IDs from known positions (avoids Azure Search propagation delay)
        foreach (var position in chunkPositions)
        {
            var chunkDocId = BuildDocumentId(knowledgeId, position);
            if (!idsToDelete.Contains(chunkDocId))
            {
                idsToDelete.Add(chunkDocId);
            }
        }

        var batch = IndexDocumentsBatch.Delete("id", idsToDelete);
        await _searchClient.IndexDocumentsAsync(batch, cancellationToken: cancellationToken);

        _logger.LogInformation("Deleted {Count} documents for {KnowledgeId} from search index (deterministic)",
            idsToDelete.Count, knowledgeId);
    }

    // --- Index management ---

    internal async Task EnsureIndexExistsAsync(CancellationToken cancellationToken = default)
    {
        if (_indexEnsured) return;

        await _indexEnsureLock.WaitAsync(cancellationToken);
        try
        {
            if (_indexEnsured) return;

            var fields = BuildIndexFields(_vectorDimensions);

            try
            {
                var existingIndex = await _indexClient.GetIndexAsync(_indexName, cancellationToken);

                var existingFieldNames = existingIndex.Value.Fields
                    .Select(f => f.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var missingFields = fields.Where(f => !existingFieldNames.Contains(f.Name)).ToList();

                // Ensure semantic configuration is present on existing index
                var needsSemanticUpdate = false;
                try
                {
                    if (existingIndex.Value.SemanticSearch == null ||
                        !existingIndex.Value.SemanticSearch.Configurations.Any(c => c.Name == SemanticConfigName))
                    {
                        existingIndex.Value.SemanticSearch ??= new SemanticSearch();
                        existingIndex.Value.SemanticSearch.Configurations.Add(BuildSemanticConfiguration());
                        needsSemanticUpdate = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Semantic search configuration skipped on index update");
                }

                if (missingFields.Count > 0 || needsSemanticUpdate)
                {
                    foreach (var field in missingFields)
                    {
                        existingIndex.Value.Fields.Add(field);
                    }

                    await _indexClient.CreateOrUpdateIndexAsync(existingIndex.Value, cancellationToken: cancellationToken);
                    _logger.LogInformation(
                        "Updated search index '{IndexName}' with {Count} new fields: {Fields}",
                        _indexName, missingFields.Count,
                        string.Join(", ", missingFields.Select(f => f.Name)));
                }
                else
                {
                    _logger.LogDebug("Search index '{IndexName}' already up to date", _indexName);
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                var vectorSearch = new VectorSearch();
                vectorSearch.Profiles.Add(new VectorSearchProfile("default-vector-profile", "default-hnsw-config"));
                vectorSearch.Algorithms.Add(new HnswAlgorithmConfiguration("default-hnsw-config")
                {
                    Parameters = new HnswParameters
                    {
                        Metric = VectorSearchAlgorithmMetric.Cosine
                    }
                });

                var index = new SearchIndex(_indexName)
                {
                    Fields = fields,
                    VectorSearch = vectorSearch
                };

                try
                {
                    index.SemanticSearch = new SemanticSearch();
                    index.SemanticSearch.Configurations.Add(BuildSemanticConfiguration());
                }
                catch (Exception semanticEx)
                {
                    _logger.LogWarning(semanticEx, "Semantic search configuration skipped - may require Basic+ tier");
                }

                await _indexClient.CreateIndexAsync(index, cancellationToken);
                _logger.LogInformation(
                    "Created search index '{IndexName}' with {Count} fields",
                    _indexName, fields.Count);
            }

            _indexEnsured = true;
        }
        finally
        {
            _indexEnsureLock.Release();
        }
    }

    internal static List<SearchField> BuildIndexFields(int vectorDimensions)
    {
        if (vectorDimensions <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(vectorDimensions),
                vectorDimensions,
                "vectorDimensions must be a positive integer matching the embedding model (e.g. 1536 or 3072).");
        }

        return new List<SearchField>
        {
            new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
            new SimpleField("tenantId", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("knowledgeId", SearchFieldDataType.String) { IsFilterable = true, IsSortable = true },
            new SearchableField("title"),
            new SearchableField("content"),
            new SearchableField("aiSummary"),
            new SimpleField("vaultName", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("vaultId", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("vaultIds", SearchFieldDataType.Collection(SearchFieldDataType.String)) { IsFilterable = true },
            new SimpleField("ancestorVaultIds", SearchFieldDataType.Collection(SearchFieldDataType.String)) { IsFilterable = true },
            new SearchableField("topicName") { IsFilterable = true },
            new SearchableField("tags", collection: true) { IsFilterable = true },
            new SimpleField("knowledgeTypeId", SearchFieldDataType.String) { IsFilterable = true },
            new SearchableField("filePath"),
            new SearchField("contentVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
            {
                IsSearchable = true,
                VectorSearchDimensions = vectorDimensions,
                VectorSearchProfileName = "default-vector-profile"
            },
            new SimpleField("createdAt", SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },
            new SimpleField("updatedAt", SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },
            new SimpleField("documentType", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("position", SearchFieldDataType.Int32) { IsFilterable = true, IsSortable = true }
        };
    }

    /// <summary>
    /// Builds the semantic configuration for the self-hosted search index.
    /// </summary>
    internal static SemanticConfiguration BuildSemanticConfiguration()
    {
        return new SemanticConfiguration(SemanticConfigName,
            new SemanticPrioritizedFields
            {
                TitleField = new SemanticField("title"),
                ContentFields = { new SemanticField("content"), new SemanticField("aiSummary") },
                KeywordsFields = { new SemanticField("tags") }
            });
    }

    private static bool IsTransientSearchFailure(RequestFailedException ex) =>
        ex.Status is 408 or 429 or 500 or 502 or 503 or 504;

    /// <summary>
    /// Resets the index-ensured flag. For testing only.
    /// </summary>
    internal static void ResetIndexEnsuredFlag()
    {
        _indexEnsured = false;
    }

    // --- Internal helpers ---

    internal string BuildDocumentId(Guid knowledgeId, int? chunkIndex = null)
    {
        var baseId = $"{_tenantId}_{knowledgeId}";
        return chunkIndex.HasValue ? $"{baseId}_chunk_{chunkIndex.Value}" : baseId;
    }

    internal string BuildFilter(
        Guid? vaultId,
        bool includeDescendants,
        IEnumerable<string>? tags,
        bool requireAllTags,
        DateTime? startDate,
        DateTime? endDate)
    {
        var filters = new List<string>
        {
            $"tenantId eq '{_tenantId}'"
        };

        if (vaultId.HasValue)
        {
            if (includeDescendants)
            {
                filters.Add(
                    $"(vaultId eq '{vaultId.Value}' or ancestorVaultIds/any(a: a eq '{vaultId.Value}'))");
            }
            else
            {
                filters.Add($"vaultId eq '{vaultId.Value}'");
            }
        }

        if (tags != null)
        {
            var tagList = tags.ToList();
            if (tagList.Count > 0)
            {
                if (requireAllTags)
                {
                    foreach (var tag in tagList)
                    {
                        filters.Add($"tags/any(t: t eq '{EscapeOData(tag)}')");
                    }
                }
                else
                {
                    var tagFilters = tagList.Select(
                        t => $"tags/any(t: t eq '{EscapeOData(t)}')");
                    filters.Add($"({string.Join(" or ", tagFilters)})");
                }
            }
        }

        if (startDate.HasValue)
        {
            filters.Add($"createdAt ge {startDate.Value:yyyy-MM-ddTHH:mm:ssZ}");
        }
        if (endDate.HasValue)
        {
            filters.Add($"createdAt le {endDate.Value:yyyy-MM-ddTHH:mm:ssZ}");
        }

        return string.Join(" and ", filters);
    }

    private static SearchResultItem MapResult(Azure.Search.Documents.Models.SearchResult<SearchDocument> result)
    {
        var doc = result.Document;
        return new SearchResultItem
        {
            KnowledgeId = Guid.TryParse(doc.GetString("knowledgeId"), out var kid) ? kid : Guid.Empty,
            Title = doc.GetString("title") ?? string.Empty,
            Content = doc.GetString("content") ?? string.Empty,
            Summary = doc.GetString("aiSummary"),
            VaultName = doc.GetString("vaultName"),
            TopicName = doc.GetString("topicName"),
            KnowledgeType = doc.GetString("knowledgeTypeId"),
            FilePath = doc.GetString("filePath"),
            Tags = GetStringCollection(doc, "tags"),
            Score = result.Score ?? 0,
            SemanticScore = result.SemanticSearch?.RerankerScore,
            Highlights = result.Highlights?.SelectMany(h => h.Value).ToList()
                         ?? new List<string>(),
            Position = doc.TryGetValue("position", out var pos) && pos is int posInt ? posInt : 0,
            DocumentType = doc.TryGetValue("documentType", out var dt) ? dt?.ToString() : null,
            CreatedAt = TryGetDateTime(doc, "createdAt"),
            UpdatedAt = TryGetNullableDateTime(doc, "updatedAt")
        };
    }

    // FEAT_SelfHostedTemporalAwareness: Index fields are DateTimeOffset; Core
    // model uses DateTime. Convert via .UtcDateTime and tolerate missing /
    // default values so a stale index row doesn't crash chat.
    private static DateTime TryGetDateTime(SearchDocument doc, string field)
    {
        if (!doc.TryGetValue(field, out var raw) || raw is null)
            return default;
        return raw switch
        {
            DateTimeOffset dto => dto.UtcDateTime,
            DateTime dt => dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime(),
            string s when DateTimeOffset.TryParse(s, out var parsed) => parsed.UtcDateTime,
            _ => default
        };
    }

    private static DateTime? TryGetNullableDateTime(SearchDocument doc, string field)
    {
        var value = TryGetDateTime(doc, field);
        return value == default ? null : value;
    }

    private static List<string> GetStringCollection(SearchDocument doc, string field)
    {
        if (doc.TryGetValue(field, out var value) && value is IEnumerable<object> collection)
        {
            return collection.Select(o => o?.ToString() ?? string.Empty)
                             .Where(s => !string.IsNullOrEmpty(s))
                             .ToList();
        }
        return new List<string>();
    }

    internal static string EscapeOData(string value)
    {
        return value.Replace("'", "''");
    }
}
