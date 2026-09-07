using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;

namespace Knowz.SelfHosted.Infrastructure.Extensions;

/// <summary>
/// Extension methods for ISearchService that leverage selfhosted-specific capabilities.
/// </summary>
public static class SearchServiceExtensions
{
    /// <summary>
    /// Deletes a document and its chunks using deterministic ID generation from known chunk positions.
    /// Avoids Azure Search eventual-consistency issues with query-based chunk discovery.
    /// Falls back to query-based deletion for non-Azure implementations.
    /// </summary>
    public static Task DeleteDocumentWithChunksAsync(
        this ISearchService searchService,
        Guid knowledgeId,
        IEnumerable<int> chunkPositions,
        CancellationToken cancellationToken = default)
    {
        if (searchService is AzureSearchService azureSearch)
        {
            return azureSearch.DeleteDocumentAsync(knowledgeId, chunkPositions, cancellationToken);
        }

        return searchService.DeleteDocumentAsync(knowledgeId, cancellationToken);
    }
}
