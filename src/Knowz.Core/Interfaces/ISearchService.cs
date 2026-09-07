using Knowz.Core.Models;

namespace Knowz.Core.Interfaces;

/// <summary>
/// Abstraction for search operations against the knowledge index.
/// </summary>
public interface ISearchService
{
    /// <summary>
    /// Performs hybrid search (keyword + vector) against the search index.
    /// </summary>
    Task<List<SearchResultItem>> HybridSearchAsync(
        string query,
        float[]? queryEmbedding = null,
        Guid? vaultId = null,
        bool includeDescendants = true,
        IEnumerable<string>? tags = null,
        bool requireAllTags = false,
        DateTime? startDate = null,
        DateTime? endDate = null,
        int maxResults = 10,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Indexes a document in the search index.
    /// </summary>
    /// <param name="knowledgeCreatedAt">
    /// FEAT_SelfHostedTemporalAwareness: Pass the knowledge entity's real
    /// CreatedAt so the chat temporal-awareness feature can cite accurate
    /// dates. If null, implementations MUST fall back to DateTimeOffset.UtcNow
    /// for backwards compatibility with existing callers.
    /// </param>
    /// <param name="knowledgeUpdatedAt">
    /// FEAT_SelfHostedTemporalAwareness: Pass the knowledge entity's real
    /// UpdatedAt. If null, implementations MUST fall back to
    /// DateTimeOffset.UtcNow. Re-index operations should still pass the
    /// original UpdatedAt so "when was this last edited" questions return
    /// the content edit time, not the index time.
    /// </param>
    Task IndexDocumentAsync(
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
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a document (and all its chunks) from the search index.
    /// </summary>
    Task DeleteDocumentAsync(
        Guid knowledgeId,
        CancellationToken cancellationToken = default);
}
