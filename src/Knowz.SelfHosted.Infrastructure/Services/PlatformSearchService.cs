using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Knowz.Core.Interfaces;
using Knowz.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// Proxies search operations to the Knowz Platform API.
/// Used in Platform proxy mode (Tier 1) when KnowzPlatform:Enabled is true.
/// </summary>
public class PlatformSearchService : ISearchService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly ILogger<PlatformSearchService> _logger;

    private static readonly JsonSerializerOptions s_serializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions s_deserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public PlatformSearchService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<PlatformSearchService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _baseUrl = configuration["KnowzPlatform:BaseUrl"]
            ?? throw new InvalidOperationException("KnowzPlatform:BaseUrl is required");
        _apiKey = configuration["KnowzPlatform:ApiKey"]
            ?? throw new InvalidOperationException("KnowzPlatform:ApiKey is required");
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
        try
        {
            var requestBody = new SearchQueryRequest(
                Query: query,
                QueryVector: queryEmbedding,
                VaultId: vaultId,
                MaxResults: maxResults,
                Tags: tags?.ToList(),
                RequireAllTags: requireAllTags);

            var request = CreateRequest(HttpMethod.Post, "/api/v1/search-proxy/query", requestBody);

            using var client = _httpClientFactory.CreateClient("KnowzPlatformClient");
            var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<SearchQueryResponse>(body, s_deserializeOptions);

            // REFACTOR_PlatformSearchProxyTemporal: CreatedAt/UpdatedAt populate here
            // only if the platform's /api/v1/search-proxy/query response includes those
            // fields. If not, FormatSourceBlock suppresses them via default(DateTime)
            // check. Tracked in knowzcode/knowzcode_tracker.md.
            var items = result?.Items ?? new List<SearchResultItem>();

            _logger.LogInformation(
                "Platform search returned {Count} results for query '{Query}'",
                items.Count, query.Length > 50 ? query[..50] + "..." : query);

            return items;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "Platform search proxy failed for query '{Query}', returning empty results",
                query.Length > 50 ? query[..50] + "..." : query);
            return new List<SearchResultItem>();
        }
    }

    public async Task IndexDocumentAsync(
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
        try
        {
            // FEAT_SelfHostedTemporalAwareness: knowledgeCreatedAt/UpdatedAt
            // are ignored on this proxy path. The main platform's
            // /api/v1/search-proxy/index endpoint owns date management on its
            // own index. If parity is needed in a future WG, extend
            // IndexDocumentRequest below.
            var requestBody = new IndexDocumentRequest(
                KnowledgeId: knowledgeId,
                Title: title,
                Content: content,
                Summary: summary,
                VaultName: vaultName,
                VaultId: vaultId,
                AncestorVaultIds: ancestorVaultIds,
                TopicName: topicName,
                Tags: tags?.ToList(),
                KnowledgeType: knowledgeType,
                FilePath: filePath,
                ContentVector: contentVector,
                ChunkIndex: chunkIndex);

            var request = CreateRequest(HttpMethod.Post, "/api/v1/search-proxy/index", requestBody);

            using var client = _httpClientFactory.CreateClient("KnowzPlatformClient");
            var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            _logger.LogInformation(
                "Indexed document '{KnowledgeId}' via platform search proxy", knowledgeId);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "Failed to index document '{KnowledgeId}' via platform search proxy", knowledgeId);
        }
    }

    public async Task DeleteDocumentAsync(
        Guid knowledgeId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = CreateRequest(HttpMethod.Delete, $"/api/v1/search-proxy/remove/{knowledgeId}");

            using var client = _httpClientFactory.CreateClient("KnowzPlatformClient");
            var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            _logger.LogInformation(
                "Deleted document '{KnowledgeId}' via platform search proxy", knowledgeId);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "Failed to delete document '{KnowledgeId}' via platform search proxy", knowledgeId);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, object? body = null)
    {
        var uri = new Uri(new Uri(_baseUrl), path);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-Api-Key", _apiKey);

        if (body != null)
        {
            var json = JsonSerializer.Serialize(body, s_serializeOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    // --- Internal DTOs ---

    internal record SearchQueryRequest(
        string Query,
        float[]? QueryVector,
        Guid? VaultId,
        int MaxResults,
        List<string>? Tags,
        bool RequireAllTags);

    internal record SearchQueryResponse(
        List<SearchResultItem>? Items,
        int TotalResults);

    internal record IndexDocumentRequest(
        Guid KnowledgeId,
        string Title,
        string Content,
        string? Summary,
        string? VaultName,
        Guid? VaultId,
        List<Guid>? AncestorVaultIds,
        string? TopicName,
        List<string>? Tags,
        string? KnowledgeType,
        string? FilePath,
        float[]? ContentVector,
        int? ChunkIndex);
}
