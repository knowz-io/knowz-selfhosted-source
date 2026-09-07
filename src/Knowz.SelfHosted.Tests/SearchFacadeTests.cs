using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.Core.Models;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class SearchFacadeTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly ISearchService _searchService;
    private readonly IOpenAIService _openAIService;
    private readonly SearchFacade _svc;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public SearchFacadeTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);

        _searchService = Substitute.For<ISearchService>();
        _openAIService = Substitute.For<IOpenAIService>();

        _openAIService.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f, 0.2f, 0.3f });

        var streamingOpenAIService = Substitute.For<IStreamingOpenAIService>();
        var logger = Substitute.For<ILogger<SearchFacade>>();

        _svc = new SearchFacade(_db, _searchService, _openAIService, streamingOpenAIService, logger);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public async Task SearchKnowledgeAsync_ReturnsSearchResponse()
    {
        var searchResults = new List<SearchResultItem>
        {
            new()
            {
                KnowledgeId = Guid.NewGuid(),
                Title = "Test Result",
                Content = "Found content",
                Score = 0.95
            }
        };

        _searchService.HybridSearchAsync(
            Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<Guid?>(), Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(),
            Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(searchResults);

        var result = await _svc.SearchKnowledgeAsync(
            "test query", 10, null, false, new List<string>(), false, null, null, null, CancellationToken.None);

        Assert.IsType<SearchResponse>(result);
        Assert.Equal(1, result.TotalResults);
    }

    [Fact]
    public async Task AskQuestionAsync_ReturnsAskAnswerResponse()
    {
        var searchResults = new List<SearchResultItem>
        {
            new()
            {
                KnowledgeId = Guid.NewGuid(),
                Title = "Source",
                Content = "Relevant content",
                Score = 0.9
            }
        };

        _searchService.HybridSearchAsync(
            Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<Guid?>(), Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(),
            Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(searchResults);

        _openAIService.AnswerQuestionAsync(
            Arg.Any<string>(), Arg.Any<List<SearchResultItem>>(),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AnswerResponse
            {
                Answer = "The answer is 42",
                SourceKnowledgeIds = new List<Guid> { searchResults[0].KnowledgeId },
                Confidence = 0.85
            });

        var result = await _svc.AskQuestionAsync("What is the answer?", null, false, CancellationToken.None);

        Assert.IsType<AskAnswerResponse>(result);
        Assert.Equal("The answer is 42", result.Answer);
        // Confidence is normalized from the search result score, not the OpenAI mock value.
        Assert.Equal(0.9, result.Confidence);
        Assert.Single(result.Sources);
    }

    [Fact]
    public async Task SearchByFilePatternAsync_CountOnly_ReturnsCount()
    {
        _db.KnowledgeItems.AddRange(
            new Knowledge { TenantId = TenantId, Title = "File 1", Content = "C1", FilePath = "docs/readme.md" },
            new Knowledge { TenantId = TenantId, Title = "File 2", Content = "C2", FilePath = "docs/guide.md" },
            new Knowledge { TenantId = TenantId, Title = "File 3", Content = "C3", FilePath = "src/main.cs" });
        await _db.SaveChangesAsync();

        var result = await _svc.SearchByFilePatternAsync("docs/*", countOnly: true, 50, CancellationToken.None);

        Assert.IsType<FilePatternResponse>(result);
        Assert.Equal(2, result.Count);
        Assert.Null(result.Items);
    }

    [Fact]
    public async Task SearchByFilePatternAsync_ReturnsItems()
    {
        _db.KnowledgeItems.AddRange(
            new Knowledge { TenantId = TenantId, Title = "File 1", Content = "C1", FilePath = "docs/readme.md" },
            new Knowledge { TenantId = TenantId, Title = "File 2", Content = "C2", FilePath = "src/main.cs" });
        await _db.SaveChangesAsync();

        var result = await _svc.SearchByFilePatternAsync("docs/*", countOnly: false, 50, CancellationToken.None);

        Assert.NotNull(result.Items);
        Assert.Single(result.Items);
        Assert.Equal("File 1", result.Items[0].Title);
    }

    [Fact]
    public async Task SearchByTitlePatternAsync_ReturnsItems()
    {
        _db.KnowledgeItems.AddRange(
            new Knowledge { TenantId = TenantId, Title = "Getting Started Guide", Content = "C1" },
            new Knowledge { TenantId = TenantId, Title = "API Reference", Content = "C2" },
            new Knowledge { TenantId = TenantId, Title = "Getting Help", Content = "C3" });
        await _db.SaveChangesAsync();

        var result = await _svc.SearchByTitlePatternAsync("Getting*", countOnly: false, 50, CancellationToken.None);

        Assert.NotNull(result.Items);
        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public async Task SearchKnowledgeAsync_TruncatesLongContent()
    {
        var longContent = new string('x', 1000);
        var searchResults = new List<SearchResultItem>
        {
            new()
            {
                KnowledgeId = Guid.NewGuid(),
                Title = "Long Content",
                Content = longContent,
                Score = 0.9
            }
        };

        _searchService.HybridSearchAsync(
            Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<Guid?>(), Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(),
            Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(searchResults);

        var result = await _svc.SearchKnowledgeAsync(
            "test", 10, null, false, new List<string>(), false, null, null, null, CancellationToken.None);

        var item = result.Items.First();
        // Content should be truncated to 500 + "..."
        Assert.NotNull(item.Content);
        Assert.Equal(503, item.Content.Length);
        Assert.EndsWith("...", item.Content);
    }

    [Fact]
    public async Task SearchKnowledgeAsync_DoesNotTruncateShortContent()
    {
        var shortContent = "Short";
        var searchResults = new List<SearchResultItem>
        {
            new()
            {
                KnowledgeId = Guid.NewGuid(),
                Title = "Short Content",
                Content = shortContent,
                Score = 0.9
            }
        };

        _searchService.HybridSearchAsync(
            Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<Guid?>(), Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(),
            Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(searchResults);

        var result = await _svc.SearchKnowledgeAsync(
            "test", 10, null, false, new List<string>(), false, null, null, null, CancellationToken.None);

        var item = result.Items.First();
        Assert.Equal("Short", item.Content);
    }

    [Fact]
    public async Task SearchKnowledgeAsync_HandlesNullContent()
    {
        var searchResults = new List<SearchResultItem>
        {
            new()
            {
                KnowledgeId = Guid.NewGuid(),
                Title = "Null Content",
                Content = null!,
                Score = 0.9
            }
        };

        _searchService.HybridSearchAsync(
            Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<Guid?>(), Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(),
            Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(searchResults);

        var result = await _svc.SearchKnowledgeAsync(
            "test", 10, null, false, new List<string>(), false, null, null, null, CancellationToken.None);

        var item = result.Items.First();
        Assert.Null(item.Content);
    }

    // ===== ChatWithHistoryAsync Tests =====

    [Fact]
    public async Task Should_ReturnChatResponse_WhenCalledWithQuestionAndNoHistory()
    {
        // VERIFY: POST /api/chat with { question: "hello" } and no history returns a ChatResponse with non-empty answer
        var searchResults = new List<SearchResultItem>
        {
            new()
            {
                KnowledgeId = Guid.NewGuid(),
                Title = "Source",
                Content = "Relevant content",
                Score = 0.9
            }
        };

        _searchService.HybridSearchAsync(
            Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<Guid?>(), Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(),
            Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(searchResults);

        _openAIService.AnswerQuestionAsync(
            Arg.Any<string>(), Arg.Any<List<SearchResultItem>>(),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AnswerResponse
            {
                Answer = "Hello! How can I help you?",
                SourceKnowledgeIds = new List<Guid> { searchResults[0].KnowledgeId },
                Confidence = 0.8
            });

        var result = await _svc.ChatWithHistoryAsync(
            "hello", new List<ChatMessageDto>(), null, false, 10, CancellationToken.None);

        Assert.IsType<ChatResponse>(result);
        Assert.NotEmpty(result.Answer);
        // Confidence is normalized from the search result score, not the OpenAI mock value.
        Assert.Equal(0.9, result.Confidence);
        Assert.Single(result.Sources);
    }

    [Fact]
    public async Task Should_BehaveLikeAskQuestion_WhenHistoryIsNullOrEmpty()
    {
        // VERIFY: ChatWithHistoryAsync with null/empty history behaves identically to AskQuestionAsync
        var sourceId = Guid.NewGuid();
        var searchResults = new List<SearchResultItem>
        {
            new()
            {
                KnowledgeId = sourceId,
                Title = "Source",
                Content = "Content about the topic",
                Score = 0.9
            }
        };

        _searchService.HybridSearchAsync(
            Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<Guid?>(), Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(),
            Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(searchResults);

        _openAIService.AnswerQuestionAsync(
            Arg.Any<string>(), Arg.Any<List<SearchResultItem>>(),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AnswerResponse
            {
                Answer = "The answer",
                SourceKnowledgeIds = new List<Guid> { sourceId },
                Confidence = 0.9
            });

        // With null history
        var result1 = await _svc.ChatWithHistoryAsync(
            "What is X?", null, null, false, 10, CancellationToken.None);

        // With empty history
        var result2 = await _svc.ChatWithHistoryAsync(
            "What is X?", new List<ChatMessageDto>(), null, false, 10, CancellationToken.None);

        // When history is null/empty, the question passed to AnswerQuestionAsync should be just the raw question
        // (no "Previous conversation:" prefix)
        await _openAIService.Received().AnswerQuestionAsync(
            "What is X?",
            Arg.Any<List<SearchResultItem>>(),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        Assert.Equal("The answer", result1.Answer);
        Assert.Equal("The answer", result2.Answer);
    }

    [Fact]
    public async Task Should_BuildCompositeQuestion_WhenHistoryHasPriorTurns()
    {
        // VERIFY: POST /api/chat with conversationHistory containing 5 prior turns correctly passes
        // composite question to IOpenAIService.AnswerQuestionAsync that includes the conversation transcript
        var searchResults = new List<SearchResultItem>
        {
            new() { KnowledgeId = Guid.NewGuid(), Title = "S", Content = "C", Score = 0.9 }
        };

        _searchService.HybridSearchAsync(
            Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<Guid?>(), Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(),
            Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(searchResults);

        string? capturedQuestion = null;
        _openAIService.AnswerQuestionAsync(
            Arg.Do<string>(q => capturedQuestion = q),
            Arg.Any<List<SearchResultItem>>(),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AnswerResponse
            {
                Answer = "Follow-up answer",
                SourceKnowledgeIds = new List<Guid>(),
                Confidence = 0.85
            });

        var history = new List<ChatMessageDto>
        {
            new("user", "What is AI?"),
            new("assistant", "AI is artificial intelligence."),
            new("user", "Tell me more"),
            new("assistant", "AI includes machine learning and deep learning."),
            new("user", "What about NLP?"),
            new("assistant", "NLP is natural language processing."),
            new("user", "How does it work?"),
            new("assistant", "NLP uses statistical models and neural networks."),
            new("user", "Give examples"),
            new("assistant", "Examples include chatbots, translation, and sentiment analysis."),
        };

        await _svc.ChatWithHistoryAsync(
            "What are the latest advances?", history, null, false, 10, CancellationToken.None);

        Assert.NotNull(capturedQuestion);
        Assert.Contains("Previous conversation:", capturedQuestion);
        Assert.Contains("User: What is AI?", capturedQuestion);
        Assert.Contains("Assistant: AI is artificial intelligence.", capturedQuestion);
        Assert.Contains("Current question: What are the latest advances?", capturedQuestion);
    }

    [Fact]
    public async Task Should_TruncateHistory_WhenMaxTurnsExceeded()
    {
        // VERIFY: History truncation works -- when maxTurns=2 and 10 turns are sent,
        // only the last 4 messages (2 turns) appear in the composite question
        var searchResults = new List<SearchResultItem>
        {
            new() { KnowledgeId = Guid.NewGuid(), Title = "S", Content = "C", Score = 0.9 }
        };

        _searchService.HybridSearchAsync(
            Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<Guid?>(), Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(),
            Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(searchResults);

        string? capturedQuestion = null;
        _openAIService.AnswerQuestionAsync(
            Arg.Do<string>(q => capturedQuestion = q),
            Arg.Any<List<SearchResultItem>>(),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AnswerResponse
            {
                Answer = "Answer",
                SourceKnowledgeIds = new List<Guid>(),
                Confidence = 0.7
            });

        // 10 turns = 20 messages, using padded numbers to avoid substring matches
        var history = new List<ChatMessageDto>();
        for (int i = 1; i <= 10; i++)
        {
            history.Add(new ChatMessageDto("user", $"Question-turn-{i:D2}"));
            history.Add(new ChatMessageDto("assistant", $"Answer-turn-{i:D2}"));
        }

        await _svc.ChatWithHistoryAsync(
            "Final question?", history, null, false, maxTurns: 2, CancellationToken.None);

        Assert.NotNull(capturedQuestion);

        // Should contain the last 2 turns (turns 9 and 10)
        Assert.Contains("Question-turn-09", capturedQuestion);
        Assert.Contains("Answer-turn-09", capturedQuestion);
        Assert.Contains("Question-turn-10", capturedQuestion);
        Assert.Contains("Answer-turn-10", capturedQuestion);

        // Should NOT contain early turns (turns 1-8)
        Assert.DoesNotContain("Question-turn-01", capturedQuestion);
        Assert.DoesNotContain("Answer-turn-01", capturedQuestion);
        Assert.DoesNotContain("Question-turn-08", capturedQuestion);
        Assert.DoesNotContain("Answer-turn-08", capturedQuestion);

        Assert.Contains("Current question: Final question?", capturedQuestion);
    }

    [Fact]
    public async Task Should_GenerateEmbeddingFromCurrentQuestionOnly_NotCompositeString()
    {
        // VERIFY: Embedding generation uses only the current question text, not the full composite string with history
        var searchResults = new List<SearchResultItem>
        {
            new() { KnowledgeId = Guid.NewGuid(), Title = "S", Content = "C", Score = 0.9 }
        };

        _searchService.HybridSearchAsync(
            Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<Guid?>(), Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(),
            Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(searchResults);

        _openAIService.AnswerQuestionAsync(
            Arg.Any<string>(), Arg.Any<List<SearchResultItem>>(),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AnswerResponse
            {
                Answer = "Answer",
                SourceKnowledgeIds = new List<Guid>(),
                Confidence = 0.7
            });

        var history = new List<ChatMessageDto>
        {
            new("user", "Previous question about something completely different"),
            new("assistant", "Previous answer about that other thing"),
        };

        await _svc.ChatWithHistoryAsync(
            "Current specific question", history, null, false, 10, CancellationToken.None);

        // Embedding should be generated from the current question only
        await _openAIService.Received(1).GenerateEmbeddingAsync(
            "Current specific question", Arg.Any<CancellationToken>());

        // Ensure it was NOT called with the composite string
        await _openAIService.DidNotReceive().GenerateEmbeddingAsync(
            Arg.Is<string>(s => s.Contains("Previous conversation:")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_PassVaultIdToSearch_WhenVaultIdProvided()
    {
        // VERIFY: ChatPage vault selector value is included in the API request and scopes search results
        var vaultId = Guid.NewGuid();
        var searchResults = new List<SearchResultItem>
        {
            new() { KnowledgeId = Guid.NewGuid(), Title = "S", Content = "C", Score = 0.9 }
        };

        _searchService.HybridSearchAsync(
            Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<Guid?>(), Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(),
            Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(searchResults);

        _openAIService.AnswerQuestionAsync(
            Arg.Any<string>(), Arg.Any<List<SearchResultItem>>(),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AnswerResponse
            {
                Answer = "Scoped answer",
                SourceKnowledgeIds = new List<Guid>(),
                Confidence = 0.9
            });

        await _svc.ChatWithHistoryAsync(
            "Question?", new List<ChatMessageDto>(), vaultId, false, 10, CancellationToken.None);

        // VaultId should be passed through to HybridSearchAsync
        await _searchService.Received(1).HybridSearchAsync(
            Arg.Any<string>(), Arg.Any<float[]?>(), vaultId, Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(),
            Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_UseResearchModeSettings_WhenResearchModeEnabled()
    {
        var searchResults = new List<SearchResultItem>
        {
            new() { KnowledgeId = Guid.NewGuid(), Title = "S", Content = "C", Score = 0.9 }
        };

        _searchService.HybridSearchAsync(
            Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<Guid?>(), Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(),
            Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(searchResults);

        _openAIService.AnswerQuestionAsync(
            Arg.Any<string>(), Arg.Any<List<SearchResultItem>>(),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AnswerResponse
            {
                Answer = "Research answer",
                SourceKnowledgeIds = new List<Guid>(),
                Confidence = 0.9
            });

        await _svc.ChatWithHistoryAsync(
            "Question?", new List<ChatMessageDto>(), null, researchMode: true, 10, CancellationToken.None);

        // Research mode should use 15 max results (same as AskQuestionAsync)
        await _searchService.Received(1).HybridSearchAsync(
            Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<Guid?>(), Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(),
            Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
            15, // research mode uses 15
            Arg.Any<CancellationToken>());

        // Research mode flag should be passed to AnswerQuestionAsync
        await _openAIService.Received(1).AnswerQuestionAsync(
            Arg.Any<string>(), Arg.Any<List<SearchResultItem>>(),
            Arg.Any<string?>(), true, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
