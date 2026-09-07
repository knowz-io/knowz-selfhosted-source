namespace Knowz.Core.Interfaces;

/// <summary>
/// Vector embedding generation. Currently provided by Azure OpenAI via an adapter
/// over <see cref="IOpenAIService"/>; future providers (ONNX local model, Ollama, etc.)
/// can drop in by implementing this interface. See spec `SelfHostedAbstractionSeams`.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// Embedding dimensionality produced by this provider (e.g., 1536 for Azure
    /// text-embedding-3-small/ada-002, 3072 for text-embedding-3-large, 384 for
    /// all-MiniLM-L6-v2). Callers (chunking, search index init, dimension validator)
    /// must read this — never hardcode.
    /// </summary>
    int Dimensions { get; }

    /// <summary>
    /// Generates an embedding vector for the given text. Returns <c>null</c> if generation
    /// fails (caller should fall back to keyword search; matches <see cref="IOpenAIService"/>'s
    /// existing contract for backward compatibility).
    /// </summary>
    Task<float[]?> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Batch variant. Implementations may parallelize or call sequentially per provider limits.
    /// Returns an array where each element is the embedding for the corresponding input, or
    /// <c>null</c> if that input's generation failed.
    /// </summary>
    Task<IReadOnlyList<float[]?>> GenerateEmbeddingsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}
