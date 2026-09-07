namespace Knowz.SelfHosted.Infrastructure.Services;

using Knowz.Core.Interfaces;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Default adapter that exposes <see cref="IEmbeddingService"/> over the existing
/// <see cref="IOpenAIService"/> tier (NoOp / Platform / Azure). Implements
/// <c>SelfHostedAbstractionSeams</c> spec — pure delegating wrap, zero behavior change.
/// Future providers (ONNX, Ollama) replace this adapter by registering a different
/// <see cref="IEmbeddingService"/> implementation. The dimensions value is read from
/// <c>Embedding:Dimensions</c> config (already required by <c>AzureSearchService</c>).
/// </summary>
internal sealed class EmbeddingServiceAdapter : IEmbeddingService
{
    private readonly IOpenAIService _inner;
    private readonly int _dimensions;

    public EmbeddingServiceAdapter(IOpenAIService inner, IConfiguration configuration)
    {
        _inner = inner;
        _dimensions = configuration.GetValue<int?>("Embedding:Dimensions") ?? 1536;
    }

    public int Dimensions => _dimensions;

    public Task<float[]?> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        => _inner.GenerateEmbeddingAsync(text, cancellationToken);

    public async Task<IReadOnlyList<float[]?>> GenerateEmbeddingsAsync(
        IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var results = new float[]?[texts.Count];
        for (int i = 0; i < texts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[i] = await _inner.GenerateEmbeddingAsync(texts[i], cancellationToken).ConfigureAwait(false);
        }
        return results;
    }
}
