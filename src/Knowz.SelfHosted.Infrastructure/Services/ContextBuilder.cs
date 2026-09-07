using System.Text;
using Knowz.Core.Models;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// Shared context-building logic for AI services.
/// Extracted from AzureOpenAIService/PlatformAIService to eliminate duplication.
/// </summary>
public static class ContextBuilder
{
    internal const string EnrichmentSentinel = "\n\n---ENRICHMENT---\n";
    internal const int ApproxCharsPerToken = 4;

    /// <summary>
    /// Builds a context string from search results within the given token budget.
    /// </summary>
    public static string BuildContext(
        List<SearchResultItem> results,
        int tokenBudget,
        string userTimezone = "UTC",
        ILogger? logger = null)
    {
        var charBudget = tokenBudget * ApproxCharsPerToken;
        var sb = new StringBuilder();
        var currentLength = 0;

        foreach (var result in results.OrderByDescending(r => r.Score))
        {
            var block = FormatSourceBlock(result, userTimezone, logger);
            if (currentLength + block.Length > charBudget)
            {
                var remaining = charBudget - currentLength;
                if (remaining > 200)
                {
                    sb.AppendLine(block[..remaining] + "...");
                }
                break;
            }
            sb.AppendLine(block);
            sb.AppendLine();
            currentLength += block.Length;
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Formats a single search result into a text block for AI context.
    /// Includes TopicName and Tags when present.
    /// Handles enrichment sentinel: content before sentinel is subject to truncation,
    /// enrichment text after sentinel is always preserved.
    /// FEAT_SelfHostedTemporalAwareness: emits Created/Updated before body.
    /// </summary>
    public static string FormatSourceBlock(
        SearchResultItem result,
        string userTimezone = "UTC",
        ILogger? logger = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### {result.Title} [{result.KnowledgeId}]");

        // FEAT_SelfHostedTemporalAwareness: Created/Updated before other
        // metadata so the LLM sees temporal context in a predictable spot.
        var createdLocal = ChatTimezoneHelper.FormatDateInTimezone(
            result.CreatedAt, userTimezone, logger);
        if (!string.IsNullOrEmpty(createdLocal))
        {
            sb.AppendLine($"Created: {createdLocal}");
        }
        if (result.UpdatedAt.HasValue
            && !ChatTimezoneHelper.SameLocalDay(
                result.CreatedAt, result.UpdatedAt.Value, userTimezone))
        {
            var updatedLocal = ChatTimezoneHelper.FormatDateInTimezone(
                result.UpdatedAt.Value, userTimezone, logger);
            if (!string.IsNullOrEmpty(updatedLocal))
            {
                sb.AppendLine($"Updated: {updatedLocal}");
            }
        }

        if (!string.IsNullOrWhiteSpace(result.TopicName))
        {
            sb.AppendLine($"Topic: {result.TopicName}");
        }

        if (result.Tags != null && result.Tags.Count > 0)
        {
            sb.AppendLine($"Tags: {string.Join(", ", result.Tags)}");
        }

        if (!string.IsNullOrWhiteSpace(result.Summary))
        {
            sb.AppendLine($"Summary: {result.Summary}");
        }

        // Split on enrichment sentinel if present
        var rawContent = result.Content ?? string.Empty;
        string mainContent;
        string? enrichmentText = null;

        var sentinelIndex = rawContent.IndexOf(EnrichmentSentinel, StringComparison.Ordinal);
        if (sentinelIndex >= 0)
        {
            mainContent = rawContent[..sentinelIndex];
            enrichmentText = rawContent[(sentinelIndex + EnrichmentSentinel.Length)..];
        }
        else
        {
            mainContent = rawContent;
        }

        // Apply content truncation only to main content
        if (mainContent.Length > 2000 && !string.IsNullOrWhiteSpace(result.Summary))
        {
            mainContent = result.Summary + "\n\n" + mainContent[..1500] + "...";
        }
        sb.AppendLine(mainContent);

        // Append enrichment text (bypasses truncation)
        if (!string.IsNullOrWhiteSpace(enrichmentText))
        {
            sb.AppendLine(enrichmentText);
        }

        if (!string.IsNullOrWhiteSpace(result.VaultName))
        {
            sb.AppendLine($"Source: {result.VaultName}");
        }

        return sb.ToString();
    }
}
