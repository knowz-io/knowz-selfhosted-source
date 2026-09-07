using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class TextEnrichmentServiceTests
{
    // --- NoOpTextEnrichmentService tests ---

    [Fact]
    public async Task NoOp_GenerateTitleAsync_ReturnsNull()
    {
        var logger = Substitute.For<ILogger<NoOpTextEnrichmentService>>();
        ITextEnrichmentService svc = new NoOpTextEnrichmentService(logger);

        var result = await svc.GenerateTitleAsync("some content");

        Assert.Null(result);
    }

    [Fact]
    public async Task NoOp_SummarizeAsync_ReturnsNull()
    {
        var logger = Substitute.For<ILogger<NoOpTextEnrichmentService>>();
        ITextEnrichmentService svc = new NoOpTextEnrichmentService(logger);

        var result = await svc.SummarizeAsync("some content");

        Assert.Null(result);
    }

    [Fact]
    public async Task NoOp_ExtractTagsAsync_ReturnsEmptyList()
    {
        var logger = Substitute.For<ILogger<NoOpTextEnrichmentService>>();
        ITextEnrichmentService svc = new NoOpTextEnrichmentService(logger);

        var result = await svc.ExtractTagsAsync("title", "some content");

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task NoOp_ExtractTagsAsync_NeverReturnsNull()
    {
        var logger = Substitute.For<ILogger<NoOpTextEnrichmentService>>();
        ITextEnrichmentService svc = new NoOpTextEnrichmentService(logger);

        var result = await svc.ExtractTagsAsync("", "");

        Assert.NotNull(result);
    }

    // --- TextEnrichmentService static helper tests ---

    [Fact]
    public void TruncateContent_ShortContent_ReturnsSame()
    {
        var content = "Short text";
        var result = TextEnrichmentService.TruncateContent(content);
        Assert.Equal(content, result);
    }

    [Fact]
    public void TruncateContent_LongContent_Truncates()
    {
        var content = new string('x', TextEnrichmentService.MaxContentChars + 5_000);
        var result = TextEnrichmentService.TruncateContent(content);
        Assert.Equal(TextEnrichmentService.MaxContentChars, result.Length);
    }

    [Fact]
    public void TruncateContent_ExactlyMaxLength_ReturnsSame()
    {
        var content = new string('a', TextEnrichmentService.MaxContentChars);
        var result = TextEnrichmentService.TruncateContent(content);
        Assert.Equal(content, result);
    }

    [Fact]
    public void TruncateContent_EmptyString_ReturnsEmpty()
    {
        var result = TextEnrichmentService.TruncateContent("");
        Assert.Equal("", result);
    }

    [Fact]
    public void ParseTagsJson_ValidJsonArray_ReturnsTags()
    {
        var json = "[\"machine-learning\", \"python\", \"data-analysis\"]";
        var result = TextEnrichmentService.ParseTagsJson(json, 5);
        Assert.Equal(3, result.Count);
        Assert.Equal("machine-learning", result[0]);
        Assert.Equal("python", result[1]);
        Assert.Equal("data-analysis", result[2]);
    }

    [Fact]
    public void ParseTagsJson_MarkdownCodeBlock_ExtractsJson()
    {
        var response = "```json\n[\"tag1\", \"tag2\"]\n```";
        var result = TextEnrichmentService.ParseTagsJson(response, 5);
        Assert.Equal(2, result.Count);
        Assert.Equal("tag1", result[0]);
        Assert.Equal("tag2", result[1]);
    }

    [Fact]
    public void ParseTagsJson_MarkdownCodeBlockNoLang_ExtractsJson()
    {
        var response = "```\n[\"tag1\"]\n```";
        var result = TextEnrichmentService.ParseTagsJson(response, 5);
        Assert.Single(result);
        Assert.Equal("tag1", result[0]);
    }

    [Fact]
    public void ParseTagsJson_InvalidJson_ReturnsEmptyList()
    {
        var response = "not valid json";
        var result = TextEnrichmentService.ParseTagsJson(response, 5);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseTagsJson_RespectsMaxTags()
    {
        var json = "[\"a\", \"b\", \"c\", \"d\", \"e\", \"f\"]";
        var result = TextEnrichmentService.ParseTagsJson(json, 3);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void ParseTagsJson_FiltersEmptyStrings()
    {
        var json = "[\"valid\", \"\", \"  \", \"also-valid\"]";
        var result = TextEnrichmentService.ParseTagsJson(json, 5);
        Assert.Equal(2, result.Count);
        Assert.Equal("valid", result[0]);
        Assert.Equal("also-valid", result[1]);
    }

    [Fact]
    public void ParseTagsJson_EmptyArray_ReturnsEmptyList()
    {
        var json = "[]";
        var result = TextEnrichmentService.ParseTagsJson(json, 5);
        Assert.Empty(result);
    }

    // ===== Summary Prompt Quality Tests =====

    [Fact]
    public void SummarizePrompt_ContainsTemporalResolutionInstructions()
    {
        Assert.Contains("temporal", DefaultPrompts.SummarizePrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("yesterday", DefaultPrompts.SummarizePrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SummarizePrompt_ContainsAuthorIdentityInstructions()
    {
        Assert.Contains("author", DefaultPrompts.SummarizePrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("first-person", DefaultPrompts.SummarizePrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SummarizePrompt_ContainsAntiHallucinationRules()
    {
        Assert.Contains("hallucination", DefaultPrompts.SummarizePrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NEVER", DefaultPrompts.SummarizePrompt);
    }

    [Fact]
    public void SummarizePrompt_ContainsBrevityMatching()
    {
        Assert.Contains("under 5 words", DefaultPrompts.SummarizePrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("under 20 words", DefaultPrompts.SummarizePrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SummarizePrompt_ContainsMultiVoiceAttribution()
    {
        Assert.Contains("Q&A", DefaultPrompts.SummarizePrompt);
        Assert.Contains("contributor", DefaultPrompts.SummarizePrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SummarizeSystemPrompt_MatchesDefaultPromptContent()
    {
        // Both prompts should have the same base content (SummarizeSystemPrompt uses {0} for maxWords)
        Assert.Contains("temporal", TextEnrichmentService.SummarizeSystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("author", TextEnrichmentService.SummarizeSystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hallucination", TextEnrichmentService.SummarizeSystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SummarizePrompt_RetainsMaxWordsPlaceholder()
    {
        // {0} must still be present for backward compatibility with PromptResolutionService
        Assert.Contains("{0}", DefaultPrompts.SummarizePrompt);
    }

    // ===== SummarizeAsync Context Prefix Tests =====

    [Fact]
    public void BuildUserMessagePrefix_WithCreatedAtAndAuthor()
    {
        var prefix = TextEnrichmentService.BuildUserMessagePrefix(
            new DateTime(2026, 1, 15), "Alex Smith");
        Assert.Contains("Content created on: January 15, 2026", prefix);
        Assert.Contains("Content author: Alex Smith", prefix);
    }

    [Fact]
    public void BuildUserMessagePrefix_WithCreatedAtOnly()
    {
        var prefix = TextEnrichmentService.BuildUserMessagePrefix(
            new DateTime(2026, 3, 5), null);
        Assert.Contains("Content created on: March 5, 2026", prefix);
        Assert.DoesNotContain("Content author:", prefix);
    }

    [Fact]
    public void BuildUserMessagePrefix_WithAuthorOnly()
    {
        var prefix = TextEnrichmentService.BuildUserMessagePrefix(null, "Jane");
        Assert.DoesNotContain("Content created on:", prefix);
        Assert.Contains("Content author: Jane", prefix);
    }

    [Fact]
    public void BuildUserMessagePrefix_NoContext_ReturnsEmpty()
    {
        var prefix = TextEnrichmentService.BuildUserMessagePrefix(null, null);
        Assert.Equal(string.Empty, prefix);
    }

    // ===== BriefSummary Helper Tests =====

    [Fact]
    public void GetFallbackBriefSummary_ReturnsFirst40Words()
    {
        var summary = string.Join(" ", Enumerable.Range(1, 60).Select(i => $"word{i}"));
        var fallback = TextEnrichmentService.GetFallbackBriefSummary(summary);
        var wordCount = fallback.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.True(wordCount <= 40, $"Expected max 40 words, got {wordCount}");
    }

    [Fact]
    public void GetFallbackBriefSummary_ShortSummary_ReturnsSame()
    {
        var summary = "A short summary.";
        var fallback = TextEnrichmentService.GetFallbackBriefSummary(summary);
        Assert.Equal(summary, fallback);
    }

    [Fact]
    public void GetFallbackBriefSummary_NullSummary_ReturnsNull()
    {
        var fallback = TextEnrichmentService.GetFallbackBriefSummary(null);
        Assert.Null(fallback);
    }

    // ===== BuildEmbeddingPrefix Tests =====

    [Fact]
    public void BuildEmbeddingPrefix_AllFields()
    {
        var prefix = TextEnrichmentService.BuildEmbeddingPrefix(
            "My Title", "A brief about the doc", "Context about this chunk", "tag1, tag2");
        Assert.Equal("[Document: My Title. About: A brief about the doc. This chunk: Context about this chunk. Tags: tag1, tag2]", prefix);
    }

    [Fact]
    public void BuildEmbeddingPrefix_NullBriefSummary_UsesAboutClause()
    {
        var prefix = TextEnrichmentService.BuildEmbeddingPrefix(
            "Title", null, "Chunk context", "tag1");
        Assert.DoesNotContain("About:", prefix);
        Assert.Contains("This chunk:", prefix);
    }

    [Fact]
    public void BuildEmbeddingPrefix_NullContextSummary_OmitsChunkClause()
    {
        var prefix = TextEnrichmentService.BuildEmbeddingPrefix(
            "Title", "Brief", null, "tag1");
        Assert.Contains("About: Brief", prefix);
        Assert.DoesNotContain("This chunk:", prefix);
    }

    [Fact]
    public void BuildEmbeddingPrefix_NullTags_OmitsTagsClause()
    {
        var prefix = TextEnrichmentService.BuildEmbeddingPrefix(
            "Title", "Brief", "Context", null);
        Assert.DoesNotContain("Tags:", prefix);
    }
}
