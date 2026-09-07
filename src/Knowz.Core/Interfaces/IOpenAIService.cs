using Knowz.Core.Models;

namespace Knowz.Core.Interfaces;

/// <summary>
/// Abstraction for OpenAI operations (embedding generation and Q&amp;A).
/// </summary>
public interface IOpenAIService
{
    /// <summary>
    /// Generates an embedding vector for the given text.
    /// Returns null if generation fails (caller should fall back to keyword search).
    /// </summary>
    Task<float[]?> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Answers a question using provided search results as context.
    /// Implements simple RAG: format context -> single LLM call.
    /// </summary>
    /// <param name="userTimezone">
    /// FEAT_SelfHostedTemporalAwareness: IANA timezone ID used to anchor the
    /// system prompt's current-date line and format per-source Created/Updated
    /// lines in the user's local timezone. Defaults to "UTC" for backwards
    /// compatibility; callers that have resolved a user timezone via
    /// UserPreference should pass it explicitly.
    /// </param>
    Task<AnswerResponse> AnswerQuestionAsync(
        string question,
        List<SearchResultItem> searchResults,
        string? vaultSystemPrompt = null,
        bool researchMode = false,
        string userTimezone = "UTC",
        CancellationToken cancellationToken = default);
}
