using Knowz.SelfHosted.Infrastructure.Services;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// Tests for NodeID 1: SummaryPromptUpgrade.
/// VERIFY: DetailedSummarizePrompt contains FACTS-FIRST, DEPTH SCALING,
/// TEMPORAL REFERENCE RESOLUTION, Anti-Hallucination sections.
/// </summary>
public class SummaryPromptUpgradeTests
{
    // ===== DefaultPrompts.DetailedSummarizePrompt existence =====

    [Fact]
    public void DetailedSummarizePrompt_Exists_InDefaultPrompts()
    {
        var prompt = DefaultPrompts.DetailedSummarizePrompt;
        Assert.NotNull(prompt);
        Assert.NotEmpty(prompt);
    }

    [Fact]
    public void DetailedSummarizePrompt_ContainsFactsFirstPrinciple()
    {
        Assert.Contains("FACTS-FIRST", DefaultPrompts.DetailedSummarizePrompt);
    }

    [Fact]
    public void DetailedSummarizePrompt_ContainsDepthScaling()
    {
        Assert.Contains("DEPTH SCALING", DefaultPrompts.DetailedSummarizePrompt);
        Assert.Contains("under 10 words", DefaultPrompts.DetailedSummarizePrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("under 50 words", DefaultPrompts.DetailedSummarizePrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DetailedSummarizePrompt_ContainsTemporalReferenceResolution()
    {
        Assert.Contains("TEMPORAL REFERENCE RESOLUTION", DefaultPrompts.DetailedSummarizePrompt);
        Assert.Contains("yesterday", DefaultPrompts.DetailedSummarizePrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("creation_date", DefaultPrompts.DetailedSummarizePrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DetailedSummarizePrompt_ContainsAntiHallucinationRules()
    {
        Assert.Contains("Anti-Hallucination", DefaultPrompts.DetailedSummarizePrompt);
        Assert.Contains("NEVER", DefaultPrompts.DetailedSummarizePrompt);
        Assert.Contains("speculation", DefaultPrompts.DetailedSummarizePrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DetailedSummarizePrompt_ContainsMultimediaSection()
    {
        Assert.Contains("MULTIMEDIA", DefaultPrompts.DetailedSummarizePrompt);
        Assert.Contains("HAPPENING", DefaultPrompts.DetailedSummarizePrompt);
    }

    [Fact]
    public void DetailedSummarizePrompt_ContainsMultiSourceSynthesis()
    {
        Assert.Contains("MULTI-SOURCE SYNTHESIS", DefaultPrompts.DetailedSummarizePrompt);
    }

    [Fact]
    public void DetailedSummarizePrompt_ContainsCommentsAndContributions()
    {
        Assert.Contains("COMMENTS AND CONTRIBUTIONS", DefaultPrompts.DetailedSummarizePrompt);
    }

    [Fact]
    public void DetailedSummarizePrompt_ContainsEmbeddedContentInstructions()
    {
        Assert.Contains("EMBEDDED CONTENT INSTRUCTIONS", DefaultPrompts.DetailedSummarizePrompt);
    }

    [Fact]
    public void DetailedSummarizePrompt_ContainsAuthorIdentitySection()
    {
        Assert.Contains("AUTHOR IDENTITY", DefaultPrompts.DetailedSummarizePrompt);
    }

    [Fact]
    public void DetailedSummarizePrompt_HasMaxWordsPlaceholder()
    {
        // {0} is the max_words placeholder for string.Format
        Assert.Contains("{0}", DefaultPrompts.DetailedSummarizePrompt);
    }

    [Fact]
    public void DetailedSummarizePrompt_OutputsPlainMarkdownNotJson()
    {
        Assert.Contains("Plain markdown", DefaultPrompts.DetailedSummarizePrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT JSON", DefaultPrompts.DetailedSummarizePrompt);
    }

    [Fact]
    public void DetailedSummarizePrompt_DoesNotContainHandlebarsSyntax()
    {
        // Selfhosted uses string.Format, not Handlebars
        Assert.DoesNotContain("{{content}}", DefaultPrompts.DetailedSummarizePrompt);
        Assert.DoesNotContain("{{creation_date}}", DefaultPrompts.DetailedSummarizePrompt);
        Assert.DoesNotContain("{{max_words}}", DefaultPrompts.DetailedSummarizePrompt);
        Assert.DoesNotContain("{{user_instructions}}", DefaultPrompts.DetailedSummarizePrompt);
    }

    [Fact]
    public void DetailedSummarizePrompt_DoesNotContainVaultContextBoundary()
    {
        // Selfhosted doesn't have vault context in enrichment
        Assert.DoesNotContain("VAULT CONTEXT BOUNDARY", DefaultPrompts.DetailedSummarizePrompt);
        Assert.DoesNotContain("VAULT CONTEXT", DefaultPrompts.DetailedSummarizePrompt);
    }

    [Fact]
    public void DetailedSummarizePrompt_DoesNotContainQASourceContext()
    {
        // Selfhosted doesn't have Q&A mode yet
        Assert.DoesNotContain("Q&A SOURCE CONTEXT", DefaultPrompts.DetailedSummarizePrompt);
    }

    [Fact]
    public void DetailedSummarizePrompt_DoesNotContainNameIdentityAwareness()
    {
        // uid tags not applicable to selfhosted
        Assert.DoesNotContain("[uid:", DefaultPrompts.DetailedSummarizePrompt);
        Assert.DoesNotContain("NAME IDENTITY AWARENESS", DefaultPrompts.DetailedSummarizePrompt);
    }

    [Fact]
    public void DetailedSummarizePrompt_DoesNotContainActivityHistory()
    {
        Assert.DoesNotContain("ACTIVITY HISTORY", DefaultPrompts.DetailedSummarizePrompt);
    }

    [Fact]
    public void DetailedSummarizePrompt_DoesNotContainTimezoneFormatting()
    {
        // Handlebars timezone helpers not applicable
        Assert.DoesNotContain("{{#if user_timezone}}", DefaultPrompts.DetailedSummarizePrompt);
        Assert.DoesNotContain("{{timezone_abbr}}", DefaultPrompts.DetailedSummarizePrompt);
    }

    // ===== TextEnrichmentService.DetailedSummarizeSystemPrompt =====

    [Fact]
    public void DetailedSummarizeSystemPrompt_Exists_InTextEnrichmentService()
    {
        var prompt = TextEnrichmentService.DetailedSummarizeSystemPrompt;
        Assert.NotNull(prompt);
        Assert.NotEmpty(prompt);
    }

    [Fact]
    public void DetailedSummarizeSystemPrompt_ContainsFactsFirst()
    {
        Assert.Contains("FACTS-FIRST", TextEnrichmentService.DetailedSummarizeSystemPrompt);
    }

    [Fact]
    public void DetailedSummarizeSystemPrompt_HasMaxWordsPlaceholder()
    {
        // The system prompt should also use {0} for maxWords
        Assert.Contains("{0}", TextEnrichmentService.DetailedSummarizeSystemPrompt);
    }

    // ===== MaxContentChars increased for detailed summary =====

    [Fact]
    public void MaxContentChars_IsAtLeast50000()
    {
        // For the detailed summary prompt, content truncation should allow at least 50K chars
        Assert.True(TextEnrichmentService.MaxContentChars >= 50_000,
            $"MaxContentChars should be at least 50,000 but was {TextEnrichmentService.MaxContentChars}");
    }

    [Fact]
    public void TruncateContent_50KContent_NotTruncated()
    {
        var content = new string('x', 50_000);
        var result = TextEnrichmentService.TruncateContent(content);
        Assert.Equal(50_000, result.Length);
    }

    // ===== Old SummarizePrompt still exists for backward compatibility =====

    [Fact]
    public void SummarizePrompt_StillExists_ForBackwardCompatibility()
    {
        Assert.NotNull(DefaultPrompts.SummarizePrompt);
        Assert.Contains("{0}", DefaultPrompts.SummarizePrompt);
    }
}
