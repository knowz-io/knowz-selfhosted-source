using System.Runtime.CompilerServices;
using Knowz.Core.Interfaces;
using Knowz.Core.Models;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// No-op OpenAI service used when Azure OpenAI is not configured.
/// Returns null embeddings and error responses so the API can still start
/// and serve auth/admin/CRUD features without Azure credentials.
/// </summary>
public class NoOpOpenAIService : IOpenAIService, IContentAmendmentService, IStreamingOpenAIService
{
    private readonly ILogger<NoOpOpenAIService> _logger;

    public NoOpOpenAIService(ILogger<NoOpOpenAIService> logger)
    {
        _logger = logger;
    }

    public Task<float[]?> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("OpenAI is not configured. Embedding generation is unavailable.");
        return Task.FromResult<float[]?>(null);
    }

    public Task<AnswerResponse> AnswerQuestionAsync(
        string question,
        List<SearchResultItem> searchResults,
        string? vaultSystemPrompt = null,
        bool researchMode = false,
        string userTimezone = "UTC",
        CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("OpenAI is not configured. Q&A is unavailable.");
        return Task.FromResult(new AnswerResponse
        {
            Answer = string.Empty,
            SourceKnowledgeIds = new List<Guid>(),
            Confidence = 0
        });
    }

    public async IAsyncEnumerable<string> AnswerQuestionStreamingAsync(
        string question,
        List<SearchResultItem> searchResults,
        string? vaultSystemPrompt = null,
        bool researchMode = false,
        string userTimezone = "UTC",
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("OpenAI is not configured. Streaming Q&A is unavailable.");
        // SH_OfflineAiHonesty R1: yield nothing. The unavailable state travels as a
        // typed flag on the response envelope, never as prose in the user's transcript.
        await Task.CompletedTask; // Satisfy async requirement
        yield break;
    }

    public Task<string> ApplyContentUpdateAsync(
        string existingContent,
        string instruction,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(
            "AI is not configured on this instance.");
    }
}
