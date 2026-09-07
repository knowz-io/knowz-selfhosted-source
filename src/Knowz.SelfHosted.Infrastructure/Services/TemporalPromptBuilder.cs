using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// FEAT_SelfHostedTemporalAwareness: Builds the dynamic system prompt that
/// anchors the LLM with the current date and teaches it to use temporal
/// metadata on sources. Replaces the static const
/// <c>AzureOpenAIService.DefaultSystemPrompt</c> with a template rendered
/// per chat request so relative terms ("today", "yesterday", "last week")
/// resolve correctly in the user's timezone.
///
/// When the caller supplies a custom (vault-level) system prompt, the
/// temporal blocks are PREPENDED to the custom prompt rather than
/// replacing it — custom prompts retain their full content without
/// needing to be rewritten to opt in.
/// </summary>
internal static class TemporalPromptBuilder
{
    /// <summary>
    /// The default body used when no custom override is provided.
    /// Kept as an internal constant so tests and reviewers can assert it
    /// was used, and so consumers that still reference the old
    /// <c>AzureOpenAIService.DefaultSystemPrompt</c> shape see parity.
    /// </summary>
    internal const string DefaultBody = """
        You are a helpful knowledge assistant. Answer questions based on the provided context from the knowledge base.

        Guidelines:
        - Answer based on the provided context. If the context doesn't contain enough information, say so.
        - Reference specific sources by their title when citing information.
        - Be concise but thorough. Provide specific details from the sources.
        - If multiple sources provide different perspectives, present them.
        - Format your response using markdown for readability.
        """;

    /// <summary>
    /// Builds the full system prompt for a chat request. The returned
    /// string always contains the literal "CURRENT DATE:" and
    /// "TEMPORAL AWARENESS" markers so VERIFY tests can assert presence.
    /// </summary>
    /// <param name="customPromptOverride">
    /// Vault-level or persona system prompt. When null/whitespace the
    /// <see cref="DefaultBody"/> is used as the body.
    /// </param>
    /// <param name="timezoneId">Resolved IANA timezone ID.</param>
    /// <param name="nowUtc">Current UTC time (injected for testability).</param>
    /// <param name="logger">Optional logger for timezone warnings.</param>
    public static string BuildSystemPrompt(
        string? customPromptOverride,
        string timezoneId,
        DateTime nowUtc,
        ILogger? logger = null)
    {
        var today = ChatTimezoneHelper.TodayInTimezone(nowUtc, timezoneId, logger);
        // If the timezone resolution produced nothing (default/MinValue input),
        // fall back to a raw UTC string so the anchor block is never empty —
        // the LLM benefits from ANY date anchor over none.
        if (string.IsNullOrEmpty(today))
            today = nowUtc.ToString("yyyy-MM-dd");

        var body = string.IsNullOrWhiteSpace(customPromptOverride)
            ? DefaultBody
            : customPromptOverride!;

        // Temporal blocks are prepended. Body is preserved verbatim.
        return $"""
            CURRENT DATE: Today is {today} in {timezoneId}. When the user asks about "today", "yesterday", "this week", "last month", or any other relative date term, resolve it relative to this current date.

            TEMPORAL AWARENESS:
            - Each source may include a "Created:" date and optionally an "Updated:" date.
            - When users ask about dates or "when" something happened, use these metadata fields to answer.
            - Cite the specific source title when reporting dates.

            {body}
            """;
    }
}
