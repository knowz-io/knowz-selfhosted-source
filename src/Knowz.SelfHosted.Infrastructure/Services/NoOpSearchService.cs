using Knowz.Core.Interfaces;
using Knowz.Core.Models;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// No-op search service used when Azure AI Search is not configured.
/// Returns empty results and logs warnings so the API can still start
/// and serve auth/admin/CRUD features without Azure credentials.
/// </summary>
public class NoOpSearchService : ISearchService
{
    private readonly ILogger<NoOpSearchService> _logger;

    public NoOpSearchService(ILogger<NoOpSearchService> logger)
    {
        _logger = logger;
    }

    public Task<List<SearchResultItem>> HybridSearchAsync(
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
        _logger.LogWarning("Search is not configured. Configure AzureAISearch settings to enable search.");
        return Task.FromResult(new List<SearchResultItem>());
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
        _logger.LogWarning("Search indexing is not configured. Document '{KnowledgeId}' was not indexed.", knowledgeId);
        return Task.CompletedTask;
    }

    public Task DeleteDocumentAsync(
        Guid knowledgeId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("Search is not configured. Document '{KnowledgeId}' was not deleted from index.", knowledgeId);
        return Task.CompletedTask;
    }
}
