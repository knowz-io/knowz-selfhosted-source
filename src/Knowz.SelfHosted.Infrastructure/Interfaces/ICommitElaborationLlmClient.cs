namespace Knowz.SelfHosted.Infrastructure.Interfaces;

/// <summary>
/// Narrow LLM abstraction used exclusively by the commit-history elaboration path.
///
/// Deliberately kept separate from <see cref="Knowz.Core.Interfaces.IOpenAIService"/>
/// (which only exposes embeddings + Q&amp;A) and from
/// <see cref="ITextEnrichmentService"/> / <see cref="IContentAmendmentService"/> (which have
/// their own baked-in system prompts). Commit elaboration needs full control of BOTH the
/// system prompt and user prompt (CRIT-1 injection defense) and needs a signal when platform
/// AI is unavailable (HIGH-3 fallback to stub content).
///
/// Implementations:
///   • <c>NoOpCommitElaborationLlmClient</c> — used when <c>KnowzPlatform:Enabled</c> is false
///     or config is missing. <see cref="IsAvailable"/> returns false; <see cref="ElaborateAsync"/>
///     returns null (caller short-circuits and persists a stub).
///   • <c>PlatformCommitElaborationLlmClient</c> — calls the platform
///     <c>/api/v1/ai-services/completion</c> endpoint via <c>IHttpClientFactory</c>.
///
/// Lives in the Infrastructure layer because implementations are infrastructure concerns
/// (HTTP clients, external API calls). The Application-layer <c>GitCommitHistoryService</c>
/// depends on this interface via its Infrastructure project reference.
///
/// WorkGroupID: kc-feat-git-commit-knowledge-sync-20260410
/// NodeID: SelfHostedCommitHistoryParity (NODE-4)
/// </summary>
public interface ICommitElaborationLlmClient
{
    /// <summary>
    /// True when the client is wired to a real LLM endpoint. Selfhosted deployments
    /// without <c>KnowzPlatform</c> enabled return false, and <c>GitCommitHistoryService</c>
    /// short-circuits to metadata-only stubs for every child.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Invoke the LLM with a fully-assembled system + user prompt pair.
    /// Returns the model's response text, or null on failure (caller persists stub).
    /// </summary>
    Task<string?> ElaborateAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken);
}
