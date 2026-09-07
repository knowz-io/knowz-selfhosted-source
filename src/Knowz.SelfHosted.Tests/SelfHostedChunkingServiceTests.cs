using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Models;
using Knowz.SelfHosted.Infrastructure.Services;

namespace Knowz.SelfHosted.Tests;

public class SelfHostedChunkingServiceTests
{
    private readonly SelfHostedChunkingService _svc = new();

    // ===== DetermineStrategy Tests =====

    [Theory]
    [InlineData(KnowledgeType.Note, SelfHostedChunkingStrategy.Markdown)]
    [InlineData(KnowledgeType.Document, SelfHostedChunkingStrategy.Markdown)]
    [InlineData(KnowledgeType.QuestionAnswer, SelfHostedChunkingStrategy.Markdown)]
    [InlineData(KnowledgeType.Journal, SelfHostedChunkingStrategy.Markdown)]
    [InlineData(KnowledgeType.Transcript, SelfHostedChunkingStrategy.Sentence)]
    [InlineData(KnowledgeType.Video, SelfHostedChunkingStrategy.Sentence)]
    [InlineData(KnowledgeType.Audio, SelfHostedChunkingStrategy.Sentence)]
    [InlineData(KnowledgeType.Code, SelfHostedChunkingStrategy.Recursive)]
    [InlineData(KnowledgeType.Link, SelfHostedChunkingStrategy.Recursive)]
    [InlineData(KnowledgeType.File, SelfHostedChunkingStrategy.Recursive)]
    [InlineData(KnowledgeType.Prompt, SelfHostedChunkingStrategy.Recursive)]
    [InlineData(KnowledgeType.Image, SelfHostedChunkingStrategy.Recursive)]
    public void DetermineStrategy_MapsKnowledgeTypeCorrectly(KnowledgeType type, SelfHostedChunkingStrategy expected)
    {
        Assert.Equal(expected, _svc.DetermineStrategy(type));
    }

    // ===== ChunkContent: Short Content Tests =====

    [Fact]
    public void ChunkContent_NullContent_ReturnsSingleEmptyChunk()
    {
        var result = _svc.ChunkContent(null!, SelfHostedChunkingStrategy.Prose);
        Assert.Single(result);
        Assert.Equal(0, result[0].Position);
        Assert.Equal(string.Empty, result[0].Content);
    }

    [Fact]
    public void ChunkContent_EmptyContent_ReturnsSingleEmptyChunk()
    {
        var result = _svc.ChunkContent("", SelfHostedChunkingStrategy.Prose);
        Assert.Single(result);
        Assert.Equal(string.Empty, result[0].Content);
    }

    [Fact]
    public void ChunkContent_ShortContent_ReturnsSingleChunk()
    {
        var result = _svc.ChunkContent("Short text", SelfHostedChunkingStrategy.Prose);
        Assert.Single(result);
        Assert.Equal(0, result[0].Position);
        Assert.Equal("Short text", result[0].Content);
    }

    [Fact]
    public void ChunkContent_BelowMinChars_ReturnsSingleChunk()
    {
        var content = new string('a', 300);
        var options = new SelfHostedChunkingOptions { MinChars = 400 };
        var result = _svc.ChunkContent(content, SelfHostedChunkingStrategy.Prose, options);
        Assert.Single(result);
    }

    [Fact]
    public void ChunkContent_SingleChunk_EmbeddingTextEqualsContent()
    {
        var result = _svc.ChunkContent("Short text", SelfHostedChunkingStrategy.Prose);
        Assert.Single(result);
        Assert.Equal(result[0].Content, result[0].EmbeddingText);
    }

    // ===== ChunkContent: Prose Strategy Tests =====

    [Fact]
    public void ChunkProse_SplitsAtParagraphBoundaries()
    {
        var para1 = new string('a', 300);
        var para2 = new string('b', 300);
        var para3 = new string('c', 300);
        var content = $"{para1}\n\n{para2}\n\n{para3}";

        var options = new SelfHostedChunkingOptions { MaxTokens = 100, OverlapTokens = 0, MinTokens = 25 };
        var result = _svc.ChunkContent(content, SelfHostedChunkingStrategy.Prose, options);

        Assert.True(result.Count >= 2, $"Expected at least 2 chunks, got {result.Count}");
        // Check positions are sequential
        for (int i = 0; i < result.Count; i++)
        {
            Assert.Equal(i, result[i].Position);
        }
    }

    [Fact]
    public void ChunkProse_MergesSmallParagraphs()
    {
        var content = "Short para 1.\n\nShort para 2.\n\nShort para 3.";
        var options = new SelfHostedChunkingOptions { MaxTokens = 1000, OverlapTokens = 0, MinTokens = 2 };
        var result = _svc.ChunkContent(content, SelfHostedChunkingStrategy.Prose, options);

        // All paragraphs fit in one chunk
        Assert.Single(result);
        Assert.Contains("Short para 1.", result[0].Content);
        Assert.Contains("Short para 2.", result[0].Content);
    }

    [Fact]
    public void ChunkProse_SplitsOversizedParagraphAtSentenceBoundaries()
    {
        // Create a long paragraph with multiple sentences
        var sentences = Enumerable.Range(1, 20)
            .Select(i => $"This is sentence number {i} in our large paragraph with enough content. ")
            .ToList();
        var content = string.Join("", sentences);

        var options = new SelfHostedChunkingOptions { MaxTokens = 50, OverlapTokens = 0, MinTokens = 12 };
        var result = _svc.ChunkContent(content, SelfHostedChunkingStrategy.Prose, options);

        Assert.True(result.Count > 1, "Should produce multiple chunks");
        foreach (var chunk in result)
        {
            Assert.True(chunk.CharCount <= options.MaxChars + 100,
                $"Chunk at position {chunk.Position} has {chunk.CharCount} chars, expected max ~{options.MaxChars}");
        }
    }

    // ===== ChunkContent: Recursive Strategy Tests =====

    [Fact]
    public void ChunkRecursive_SplitsAtSeparatorHierarchy()
    {
        var section1 = new string('x', 300);
        var section2 = new string('y', 300);
        var content = $"{section1}\n\n{section2}";

        var options = new SelfHostedChunkingOptions { MaxTokens = 100, OverlapTokens = 0, MinTokens = 25 };
        var result = _svc.ChunkContent(content, SelfHostedChunkingStrategy.Recursive, options);

        Assert.True(result.Count >= 2, $"Expected >= 2 chunks, got {result.Count}");
    }

    [Fact]
    public void ChunkRecursive_FallsToSingleNewline()
    {
        var line1 = new string('x', 300);
        var line2 = new string('y', 300);
        var content = $"{line1}\n{line2}";

        var options = new SelfHostedChunkingOptions { MaxTokens = 100, OverlapTokens = 0, MinTokens = 25 };
        var result = _svc.ChunkContent(content, SelfHostedChunkingStrategy.Recursive, options);

        Assert.True(result.Count >= 2, $"Expected >= 2 chunks, got {result.Count}");
    }

    // ===== ChunkContent: Sentence Strategy Tests =====

    [Fact]
    public void ChunkSentence_SplitsAtSentenceBoundaries()
    {
        var sentences = Enumerable.Range(1, 20)
            .Select(i => $"This is test sentence number {i}. ")
            .ToList();
        var content = string.Join("", sentences);

        var options = new SelfHostedChunkingOptions { MaxTokens = 50, OverlapTokens = 0, MinTokens = 12 };
        var result = _svc.ChunkContent(content, SelfHostedChunkingStrategy.Sentence, options);

        Assert.True(result.Count > 1, "Should produce multiple chunks for long sentence content");
    }

    [Fact]
    public void ChunkSentence_GroupsSentencesRespectingMaxChars()
    {
        var content = "First sentence. Second sentence. Third sentence. Fourth sentence. Fifth sentence.";

        var options = new SelfHostedChunkingOptions { MaxTokens = 1250, OverlapTokens = 0, MinTokens = 2 };
        var result = _svc.ChunkContent(content, SelfHostedChunkingStrategy.Sentence, options);

        // All fits in one chunk
        Assert.Single(result);
    }

    // ===== ChunkContent: No chunk exceeds MaxChars =====

    [Theory]
    [InlineData(SelfHostedChunkingStrategy.Prose)]
    [InlineData(SelfHostedChunkingStrategy.Recursive)]
    [InlineData(SelfHostedChunkingStrategy.Sentence)]
    public void ChunkContent_NoChunkExceedsMaxChars(SelfHostedChunkingStrategy strategy)
    {
        // Generate long content with mixed structure
        var paragraphs = Enumerable.Range(1, 20)
            .Select(i => $"Paragraph {i}: " + string.Join(". ", Enumerable.Range(1, 5).Select(j => $"Sentence {j} with some content")))
            .ToList();
        var content = string.Join("\n\n", paragraphs);

        var options = new SelfHostedChunkingOptions { MaxTokens = 125, OverlapTokens = 12, MinTokens = 12 };
        var result = _svc.ChunkContent(content, strategy, options);

        foreach (var chunk in result)
        {
            // Allow some tolerance for forced splits (MaxTokens * 4 = 500 chars approx)
            Assert.True(chunk.CharCount <= options.MaxTokens * 4 * 1.5,
                $"Chunk at position {chunk.Position} has {chunk.CharCount} chars, expected max ~{options.MaxTokens * 4}");
        }
    }

    // ===== ChunkContent: Positions are sequential =====

    [Theory]
    [InlineData(SelfHostedChunkingStrategy.Prose)]
    [InlineData(SelfHostedChunkingStrategy.Recursive)]
    [InlineData(SelfHostedChunkingStrategy.Sentence)]
    public void ChunkContent_PositionsAreSequential(SelfHostedChunkingStrategy strategy)
    {
        var content = string.Join("\n\n", Enumerable.Range(1, 10).Select(i => new string((char)('a' + i % 26), 500)));
        var options = new SelfHostedChunkingOptions { MaxTokens = 150, OverlapTokens = 0, MinTokens = 25 };
        var result = _svc.ChunkContent(content, strategy, options);

        for (int i = 0; i < result.Count; i++)
        {
            Assert.Equal(i, result[i].Position);
        }
    }

    // ===== ChunkWithContext Tests =====

    [Fact]
    public void ChunkWithContext_SingleChunk_NoHeaderPrepended()
    {
        var result = _svc.ChunkWithContext(
            "Short content", "My Title", "A summary", new[] { "tag1", "tag2" });

        Assert.Single(result);
        Assert.Equal("Short content", result[0].Content);
        Assert.Equal(result[0].Content, result[0].EmbeddingText);
    }

    [Fact]
    public void ChunkWithContext_MultiChunk_PrependsHeader()
    {
        var content = string.Join("\n\n", Enumerable.Range(1, 10).Select(i => new string('x', 600)));
        var options = new SelfHostedChunkingOptions { MaxTokens = 175, OverlapTokens = 0, MinTokens = 25 };

        var result = _svc.ChunkWithContext(
            content, "My Title", "A summary", new[] { "tag1", "tag2" },
            SelfHostedChunkingStrategy.Prose, options);

        Assert.True(result.Count > 1, "Should produce multiple chunks");

        foreach (var chunk in result)
        {
            Assert.StartsWith("My Title\n", chunk.EmbeddingText);
            Assert.Contains("Tags: tag1, tag2", chunk.EmbeddingText);
            Assert.Contains("A summary", chunk.EmbeddingText);
            // Content should NOT have header
            Assert.DoesNotContain("My Title", chunk.Content);
        }
    }

    [Fact]
    public void ChunkWithContext_NullSummary_OmitsSummaryLine()
    {
        var content = string.Join("\n\n", Enumerable.Range(1, 10).Select(i => new string('x', 600)));
        var options = new SelfHostedChunkingOptions { MaxTokens = 175, OverlapTokens = 0, MinTokens = 25 };

        var result = _svc.ChunkWithContext(
            content, "My Title", null, new[] { "tag1" },
            SelfHostedChunkingStrategy.Prose, options);

        Assert.True(result.Count > 1);
        // Header should have title and tags but no summary
        var header = result[0].EmbeddingText.Split("\n\n")[0];
        Assert.Contains("My Title", header);
        Assert.Contains("Tags: tag1", header);
    }

    [Fact]
    public void ChunkWithContext_NullTags_OmitsTagsLine()
    {
        var content = string.Join("\n\n", Enumerable.Range(1, 10).Select(i => new string('x', 600)));
        var options = new SelfHostedChunkingOptions { MaxTokens = 175, OverlapTokens = 0, MinTokens = 25 };

        var result = _svc.ChunkWithContext(
            content, "My Title", "A summary", null,
            SelfHostedChunkingStrategy.Prose, options);

        Assert.True(result.Count > 1);
        foreach (var chunk in result)
        {
            Assert.DoesNotContain("Tags:", chunk.EmbeddingText);
        }
    }

    [Fact]
    public void ChunkWithContext_EmptyTags_OmitsTagsLine()
    {
        var content = string.Join("\n\n", Enumerable.Range(1, 10).Select(i => new string('x', 600)));
        var options = new SelfHostedChunkingOptions { MaxTokens = 175, OverlapTokens = 0, MinTokens = 25 };

        var result = _svc.ChunkWithContext(
            content, "My Title", "A summary", Array.Empty<string>(),
            SelfHostedChunkingStrategy.Prose, options);

        Assert.True(result.Count > 1);
        foreach (var chunk in result)
        {
            Assert.DoesNotContain("Tags:", chunk.EmbeddingText);
        }
    }

    [Fact]
    public void ChunkWithContext_TagsLimitedTo10AndSorted()
    {
        var tags = Enumerable.Range(1, 15).Select(i => $"ztag{i:D2}").ToList();
        var content = string.Join("\n\n", Enumerable.Range(1, 10).Select(i => new string('x', 600)));
        var options = new SelfHostedChunkingOptions { MaxTokens = 175, OverlapTokens = 0, MinTokens = 25 };

        var result = _svc.ChunkWithContext(
            content, "Title", null, tags,
            SelfHostedChunkingStrategy.Prose, options);

        Assert.True(result.Count > 1);
        var header = result[0].EmbeddingText.Split("\n\n")[0];
        // Only first 10 tags sorted
        var tagsLine = header.Split('\n').First(l => l.StartsWith("Tags:"));
        var extractedTags = tagsLine.Replace("Tags: ", "").Split(", ");
        Assert.Equal(10, extractedTags.Length);
        // Verify sorted
        Assert.Equal(extractedTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToArray(), extractedTags);
    }

    // ===== BuildContextHeader Tests =====

    [Fact]
    public void BuildContextHeader_TitleOnly()
    {
        var header = SelfHostedChunkingService.BuildContextHeader("My Title", null, null);
        Assert.Equal("My Title", header);
    }

    [Fact]
    public void BuildContextHeader_TitleAndSummary()
    {
        var header = SelfHostedChunkingService.BuildContextHeader("My Title", "A summary", null);
        Assert.Equal("My Title\nA summary", header);
    }

    [Fact]
    public void BuildContextHeader_TitleAndTags()
    {
        var header = SelfHostedChunkingService.BuildContextHeader("My Title", null, new[] { "tag1", "tag2" });
        Assert.Equal("My Title\nTags: tag1, tag2", header);
    }

    [Fact]
    public void BuildContextHeader_AllFields()
    {
        var header = SelfHostedChunkingService.BuildContextHeader("My Title", "Summary text", new[] { "alpha", "beta" });
        Assert.Equal("My Title\nTags: alpha, beta\nSummary text", header);
    }

    [Fact]
    public void BuildContextHeader_WhitespaceSummary_OmitsSummary()
    {
        var header = SelfHostedChunkingService.BuildContextHeader("Title", "   ", null);
        Assert.Equal("Title", header);
    }

    [Fact]
    public void BuildContextHeader_EmptyTitle_OmitsTitle()
    {
        var header = SelfHostedChunkingService.BuildContextHeader("", "Summary", null);
        Assert.Equal("Summary", header);
    }

    [Fact]
    public void BuildContextHeader_EmptyTagList_OmitsTags()
    {
        var header = SelfHostedChunkingService.BuildContextHeader("Title", null, Array.Empty<string>());
        Assert.Equal("Title", header);
    }

    // ===== ContentHasher Tests =====

    [Fact]
    public void ContentHasher_Hash_Deterministic()
    {
        var hash1 = ContentHasher.Hash("test content");
        var hash2 = ContentHasher.Hash("test content");
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ContentHasher_Hash_DifferentContentProducesDifferentHash()
    {
        var hash1 = ContentHasher.Hash("content A");
        var hash2 = ContentHasher.Hash("content B");
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ContentHasher_Hash_ReturnsLowercaseHex()
    {
        var hash = ContentHasher.Hash("test");
        Assert.Matches("^[0-9a-f]+$", hash);
        Assert.Equal(64, hash.Length); // SHA256 = 32 bytes = 64 hex chars
    }

    // ===== Default Options Tests =====

    [Fact]
    public void DefaultOptions_HasCorrectValues()
    {
        var options = new SelfHostedChunkingOptions();
        Assert.Equal(4000, options.MaxChars);
        Assert.Equal(400, options.OverlapChars);
        Assert.Equal(400, options.MinChars);
        Assert.Equal(1024, options.MaxTokens);
        Assert.Equal(128, options.OverlapTokens);
        Assert.Equal(25, options.MinTokens);
    }

    // ===== Entity Schema Tests =====

    [Fact]
    public void ContentChunk_HasContextSummaryProperty()
    {
        var chunk = new ContentChunk();
        Assert.Null(chunk.ContextSummary);
        chunk.ContextSummary = "Test context";
        Assert.Equal("Test context", chunk.ContextSummary);
    }

    [Fact]
    public void ContentChunk_HasIsContextualEmbeddingProperty()
    {
        var chunk = new ContentChunk();
        Assert.False(chunk.IsContextualEmbedding);
        chunk.IsContextualEmbedding = true;
        Assert.True(chunk.IsContextualEmbedding);
    }

    [Fact]
    public void Knowledge_HasBriefSummaryProperty()
    {
        var knowledge = new Knowledge();
        Assert.Null(knowledge.BriefSummary);
        knowledge.BriefSummary = "Brief summary text";
        Assert.Equal("Brief summary text", knowledge.BriefSummary);
    }

    // ===== Markdown Strategy Enum =====

    [Fact]
    public void SelfHostedChunkingStrategy_HasMarkdownValue()
    {
        Assert.Equal(3, (int)SelfHostedChunkingStrategy.Markdown);
    }

    // ===== DetermineStrategy: Markdown Routing =====

    [Theory]
    [InlineData(KnowledgeType.Note, SelfHostedChunkingStrategy.Markdown)]
    [InlineData(KnowledgeType.Document, SelfHostedChunkingStrategy.Markdown)]
    [InlineData(KnowledgeType.QuestionAnswer, SelfHostedChunkingStrategy.Markdown)]
    [InlineData(KnowledgeType.Journal, SelfHostedChunkingStrategy.Markdown)]
    public void DetermineStrategy_RoutesToMarkdown(KnowledgeType type, SelfHostedChunkingStrategy expected)
    {
        Assert.Equal(expected, _svc.DetermineStrategy(type));
    }

    [Theory]
    [InlineData(KnowledgeType.Transcript, SelfHostedChunkingStrategy.Sentence)]
    [InlineData(KnowledgeType.Video, SelfHostedChunkingStrategy.Sentence)]
    [InlineData(KnowledgeType.Audio, SelfHostedChunkingStrategy.Sentence)]
    public void DetermineStrategy_SentenceUnchanged(KnowledgeType type, SelfHostedChunkingStrategy expected)
    {
        Assert.Equal(expected, _svc.DetermineStrategy(type));
    }

    [Theory]
    [InlineData(KnowledgeType.Code, SelfHostedChunkingStrategy.Recursive)]
    [InlineData(KnowledgeType.Link, SelfHostedChunkingStrategy.Recursive)]
    [InlineData(KnowledgeType.File, SelfHostedChunkingStrategy.Recursive)]
    [InlineData(KnowledgeType.Prompt, SelfHostedChunkingStrategy.Recursive)]
    [InlineData(KnowledgeType.Image, SelfHostedChunkingStrategy.Recursive)]
    public void DetermineStrategy_RecursiveUnchanged(KnowledgeType type, SelfHostedChunkingStrategy expected)
    {
        Assert.Equal(expected, _svc.DetermineStrategy(type));
    }

    // ===== ChunkMarkdown Tests =====

    [Fact]
    public void ChunkMarkdown_SplitsAtHeaderBoundaries()
    {
        var content = "# Section 1\nContent for section one.\n\n# Section 2\nContent for section two.";
        var options = new SelfHostedChunkingOptions { MaxTokens = 1024, MinTokens = 1 };
        var result = _svc.ChunkContent(content, SelfHostedChunkingStrategy.Markdown, options);

        Assert.Equal(2, result.Count);
        Assert.StartsWith("# Section 1", result[0].Content);
        Assert.StartsWith("# Section 2", result[1].Content);
    }

    [Fact]
    public void ChunkMarkdown_KeepsHeaderInChunk()
    {
        var content = "# My Header\nSome content here.";
        var options = new SelfHostedChunkingOptions { MaxTokens = 1024, MinTokens = 1 };
        var result = _svc.ChunkContent(content, SelfHostedChunkingStrategy.Markdown, options);

        Assert.Single(result);
        Assert.Contains("# My Header", result[0].Content);
        Assert.Contains("Some content here.", result[0].Content);
    }

    [Fact]
    public void ChunkMarkdown_HandlesAllHeaderLevels()
    {
        var content = "# H1\nContent1\n## H2\nContent2\n### H3\nContent3\n#### H4\nContent4\n##### H5\nContent5\n###### H6\nContent6";
        var options = new SelfHostedChunkingOptions { MaxTokens = 1024, MinTokens = 1 };
        var result = _svc.ChunkContent(content, SelfHostedChunkingStrategy.Markdown, options);

        Assert.Equal(6, result.Count);
    }

    [Fact]
    public void ChunkMarkdown_MergesSmallSections()
    {
        var content = "# A\nTiny.\n# B\nAlso tiny.";
        var options = new SelfHostedChunkingOptions { MaxTokens = 1024, MinTokens = 25 };
        var result = _svc.ChunkContent(content, SelfHostedChunkingStrategy.Markdown, options);

        // Both sections are under MinTokens (25 tokens ~ 100 chars), should merge
        Assert.Single(result);
        Assert.Contains("# A", result[0].Content);
        Assert.Contains("# B", result[0].Content);
    }

    [Fact]
    public void ChunkMarkdown_RecursivelySplitsOversizedSections()
    {
        var longContent = string.Join(". ", Enumerable.Range(1, 100).Select(i => $"Sentence {i} with more words to pad it out"));
        var content = $"# Big Section\n{longContent}";
        var options = new SelfHostedChunkingOptions { MaxTokens = 50, MinTokens = 1 };
        var result = _svc.ChunkContent(content, SelfHostedChunkingStrategy.Markdown, options);

        Assert.True(result.Count > 1, $"Expected multiple chunks for oversized section, got {result.Count}");
    }

    [Fact]
    public void ChunkMarkdown_NoHeaders_TreatsAsOneSection()
    {
        var content = "This is content with no markdown headers at all. Just regular paragraphs.\n\nAnother paragraph here.";
        var options = new SelfHostedChunkingOptions { MaxTokens = 1024, MinTokens = 1 };
        var result = _svc.ChunkContent(content, SelfHostedChunkingStrategy.Markdown, options);

        Assert.Single(result);
        Assert.Contains("This is content", result[0].Content);
    }

    [Fact]
    public void ChunkMarkdown_NoHeaders_OversizedFallsBackToRecursive()
    {
        var longContent = string.Join("\n\n", Enumerable.Range(1, 50).Select(i => $"Paragraph {i} with enough content to matter."));
        var options = new SelfHostedChunkingOptions { MaxTokens = 30, MinTokens = 1 };
        var result = _svc.ChunkContent(longContent, SelfHostedChunkingStrategy.Markdown, options);

        Assert.True(result.Count > 1, "Should split via recursive fallback when no headers and oversized");
    }

    [Fact]
    public void ChunkMarkdown_PositionsAreSequential()
    {
        var content = "# S1\nContent1\n# S2\nContent2\n# S3\nContent3";
        var options = new SelfHostedChunkingOptions { MaxTokens = 1024, MinTokens = 1 };
        var result = _svc.ChunkContent(content, SelfHostedChunkingStrategy.Markdown, options);

        for (int i = 0; i < result.Count; i++)
        {
            Assert.Equal(i, result[i].Position);
        }
    }

    // ===== Token-aware sizing =====

    [Fact]
    public void ChunkContent_TokenAwareSizing_UsesMaxTokens()
    {
        // MaxTokens = 50, which means ~200 chars. Content > 200 chars should split.
        var content = string.Join("\n\n", Enumerable.Range(1, 20).Select(i => $"Paragraph {i} with some additional content here."));
        var options = new SelfHostedChunkingOptions { MaxTokens = 50, MinTokens = 1 };
        var result = _svc.ChunkContent(content, SelfHostedChunkingStrategy.Prose, options);

        Assert.True(result.Count > 1, "Should split based on token-aware sizing");
    }

    // ===== TextChunker Updated Defaults =====

    [Fact]
    public void TextChunker_DefaultMaxChunkChars_Is4000()
    {
        Assert.Equal(4000, Core.Services.TextChunker.DefaultMaxChunkChars);
    }

    [Fact]
    public void TextChunker_DefaultOverlapChars_Is400()
    {
        Assert.Equal(400, Core.Services.TextChunker.DefaultOverlapChars);
    }

    [Fact]
    public void TextChunker_ChunkingThreshold_Is5000()
    {
        Assert.Equal(5000, Core.Services.TextChunker.ChunkingThreshold);
    }

    [Fact]
    public void TextChunker_ChunkText_StillWorks()
    {
        var content = "Some short content that fits in one chunk.";
        var result = Core.Services.TextChunker.ChunkText(content);
        Assert.Single(result);
        Assert.Equal(content, result[0].Content);
    }

    [Fact]
    public void TextChunker_ChunkWithTitle_StillWorks()
    {
        var content = "Short content.";
        var result = Core.Services.TextChunker.ChunkWithTitle("Title", content);
        Assert.Single(result);
        Assert.Equal(content, result[0].Content);
    }

    // ===== SearchResultItem Position & DocumentType =====

    [Fact]
    public void SearchResultItem_HasPositionProperty()
    {
        var item = new SearchResultItem { Position = 5 };
        Assert.Equal(5, item.Position);
    }

    [Fact]
    public void SearchResultItem_HasDocumentTypeProperty()
    {
        var item = new SearchResultItem { DocumentType = "chunk" };
        Assert.Equal("chunk", item.DocumentType);
    }

    [Fact]
    public void SearchResultItem_PositionDefaultsToZero()
    {
        var item = new SearchResultItem();
        Assert.Equal(0, item.Position);
    }

    [Fact]
    public void SearchResultItem_DocumentTypeDefaultsToNull()
    {
        var item = new SearchResultItem();
        Assert.Null(item.DocumentType);
    }
}
