namespace Knowz.Core.Interfaces;

/// <summary>
/// Narrow vector-only abstraction extracted from <see cref="ISearchService"/>. Implementations
/// are responsible for storing document embeddings + metadata and returning nearest-neighbour
/// results. Hybrid (vector + keyword) ranking remains in <see cref="ISearchService"/>; this
/// interface is intentionally minimal so future adapters (pgvector, FAISS, Qdrant, etc.) have
/// a clean drop-in target. See spec `SelfHostedAbstractionSeams`.
/// </summary>
public interface IVectorStore
{
    /// <summary>
    /// Upserts a document's embedding vector and metadata into the index. Re-indexing the same
    /// <paramref name="documentId"/> overwrites the existing entry.
    /// </summary>
    Task IndexEmbeddingAsync(
        Guid documentId,
        float[] vector,
        IDictionary<string, object> metadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a document's embedding(s) from the index. Includes any chunk-level embeddings
    /// stored for that document.
    /// </summary>
    Task DeleteByDocumentIdAsync(
        Guid documentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns up to <paramref name="topK"/> nearest neighbours by cosine distance, optionally
    /// filtered. Filter keys are provider-specific (e.g., "vaultId", "tenantId", "modelKey").
    /// </summary>
    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        float[] queryVector,
        int topK,
        IDictionary<string, object>? filters = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result from <see cref="IVectorStore.SearchAsync"/>. <see cref="Score"/> is provider-normalized
/// to [0..1] where 1 is most-similar (typically <c>1 - cosineDistance</c>).
/// </summary>
public sealed record VectorSearchResult(
    Guid DocumentId,
    float Score,
    IDictionary<string, object> Metadata);
