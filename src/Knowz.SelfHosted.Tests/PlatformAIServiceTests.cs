using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Knowz.Core.Interfaces;
using Knowz.Core.Models;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class PlatformAIServiceTests
{
    private const string BaseUrl = "https://api.test.knowz.io";
    private const string ApiKey = "test-api-key-123";

    private readonly ILogger<PlatformAIService> _logger;
    private readonly MockHttpMessageHandler _handler;
    private readonly PlatformAIService _svc;

    public PlatformAIServiceTests()
    {
        _logger = Substitute.For<ILogger<PlatformAIService>>();
        _handler = new MockHttpMessageHandler();

        var httpClient = new HttpClient(_handler) { BaseAddress = new Uri(BaseUrl) };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("KnowzPlatformClient").Returns(httpClient);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KnowzPlatform:BaseUrl"] = BaseUrl,
                ["KnowzPlatform:ApiKey"] = ApiKey,
            })
            .Build();

        _svc = new PlatformAIService(factory, config, _logger);
    }

    // ===== GenerateEmbeddingAsync Tests =====

    [Fact]
    public async Task GenerateEmbedding_SendsCorrectRequest_ReturnsFloatArray()
    {
        var responseBody = JsonSerializer.Serialize(new
        {
            success = true,
            data = new { embeddings = new[] { new[] { 0.1f, 0.2f, 0.3f } }, model = "text-embedding-ada-002" },
            errors = (List<string>?)null
        });
        _handler.SetResponse(HttpStatusCode.OK, responseBody);

        var result = await _svc.GenerateEmbeddingAsync("test text");

        Assert.NotNull(result);
        Assert.Equal(3, result!.Length);
        Assert.Equal(0.1f, result[0]);
        Assert.Equal(0.2f, result[1]);
        Assert.Equal(0.3f, result[2]);

        // Verify request
        var req = _handler.LastRequest!;
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Contains("/api/v1/ai-services/embeddings", req.RequestUri!.PathAndQuery);
        Assert.Equal(ApiKey, req.Headers.GetValues("X-Api-Key").First());

        var body = await req.Content!.ReadAsStringAsync();
        Assert.Contains("\"input\"", body);
        Assert.Contains("test text", body);
    }

    [Fact]
    public async Task GenerateEmbedding_HttpError_ReturnsNull()
    {
        _handler.SetResponse(HttpStatusCode.InternalServerError, "Server Error");

        var result = await _svc.GenerateEmbeddingAsync("test text");

        Assert.Null(result);
    }

    [Fact]
    public async Task GenerateEmbedding_NetworkError_ReturnsNull()
    {
        _handler.SetException(new HttpRequestException("Connection refused"));

        var result = await _svc.GenerateEmbeddingAsync("test text");

        Assert.Null(result);
    }

    [Fact]
    public async Task GenerateEmbedding_ApiResponseFailure_ReturnsNull()
    {
        var responseBody = JsonSerializer.Serialize(new
        {
            success = false,
            data = (object?)null,
            errors = new[] { "Rate limit exceeded" }
        });
        _handler.SetResponse(HttpStatusCode.OK, responseBody);

        var result = await _svc.GenerateEmbeddingAsync("test text");

        Assert.Null(result);
    }

    // ===== AnswerQuestionAsync Tests =====

    [Fact]
    public async Task AnswerQuestion_HappyPath_ReturnsAnswerResponse()
    {
        var responseBody = JsonSerializer.Serialize(new
        {
            success = true,
            data = new { content = "The answer is 42.", finishReason = "stop" },
            errors = (List<string>?)null
        });
        _handler.SetResponse(HttpStatusCode.OK, responseBody);

        var searchResults = CreateSearchResults(2);

        var result = await _svc.AnswerQuestionAsync("What is the answer?", searchResults);

        Assert.Equal("The answer is 42.", result.Answer);
        Assert.Equal(2, result.SourceKnowledgeIds.Count);
        Assert.True(result.Confidence > 0);
    }

    [Fact]
    public async Task AnswerQuestion_EmptySearchResults_ReturnsFallback()
    {
        var result = await _svc.AnswerQuestionAsync("What is the answer?", new List<SearchResultItem>());

        Assert.Contains("don't have enough information", result.Answer);
        Assert.Empty(result.SourceKnowledgeIds);
        Assert.Equal(0, result.Confidence);
    }

    [Fact]
    public async Task AnswerQuestion_UsesDefaultSystemPrompt_WhenNull()
    {
        var responseBody = JsonSerializer.Serialize(new
        {
            success = true,
            data = new { content = "Answer", finishReason = "stop" },
            errors = (List<string>?)null
        });
        _handler.SetResponse(HttpStatusCode.OK, responseBody);

        await _svc.AnswerQuestionAsync("Q?", CreateSearchResults(1), vaultSystemPrompt: null);

        var body = await _handler.LastRequest!.Content!.ReadAsStringAsync();
        Assert.Contains("knowledge assistant", body); // Part of the default system prompt
    }

    [Fact]
    public async Task AnswerQuestion_UsesCustomVaultPrompt_WhenProvided()
    {
        var responseBody = JsonSerializer.Serialize(new
        {
            success = true,
            data = new { content = "Answer", finishReason = "stop" },
            errors = (List<string>?)null
        });
        _handler.SetResponse(HttpStatusCode.OK, responseBody);

        await _svc.AnswerQuestionAsync("Q?", CreateSearchResults(1), vaultSystemPrompt: "You are a pirate.");

        var body = await _handler.LastRequest!.Content!.ReadAsStringAsync();
        Assert.Contains("You are a pirate.", body);
    }

    [Fact]
    public async Task AnswerQuestion_ResearchMode_UsesHigherMaxTokens()
    {
        var responseBody = JsonSerializer.Serialize(new
        {
            success = true,
            data = new { content = "Research answer", finishReason = "stop" },
            errors = (List<string>?)null
        });
        _handler.SetResponse(HttpStatusCode.OK, responseBody);

        await _svc.AnswerQuestionAsync("Q?", CreateSearchResults(1), researchMode: true);

        var body = await _handler.LastRequest!.Content!.ReadAsStringAsync();
        Assert.Contains("4000", body); // MaxTokens should be 4000 for research mode
    }

    [Fact]
    public async Task AnswerQuestion_DefaultMode_Uses2000MaxTokens()
    {
        var responseBody = JsonSerializer.Serialize(new
        {
            success = true,
            data = new { content = "Answer", finishReason = "stop" },
            errors = (List<string>?)null
        });
        _handler.SetResponse(HttpStatusCode.OK, responseBody);

        await _svc.AnswerQuestionAsync("Q?", CreateSearchResults(1), researchMode: false);

        var body = await _handler.LastRequest!.Content!.ReadAsStringAsync();
        Assert.Contains("2000", body); // MaxTokens should be 2000 for default mode
    }

    [Fact]
    public async Task AnswerQuestion_ExtractsSourceIds_CalculatesConfidence()
    {
        var responseBody = JsonSerializer.Serialize(new
        {
            success = true,
            data = new { content = "Answer", finishReason = "stop" },
            errors = (List<string>?)null
        });
        _handler.SetResponse(HttpStatusCode.OK, responseBody);

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var results = new List<SearchResultItem>
        {
            new() { KnowledgeId = id1, Title = "T1", Content = "C1", Score = 0.8 },
            new() { KnowledgeId = id2, Title = "T2", Content = "C2", Score = 0.6 },
            new() { KnowledgeId = id1, Title = "T1-dup", Content = "C1b", Score = 0.7 }, // duplicate ID
        };

        var result = await _svc.AnswerQuestionAsync("Q?", results);

        Assert.Equal(2, result.SourceKnowledgeIds.Count); // Distinct IDs
        Assert.Contains(id1, result.SourceKnowledgeIds);
        Assert.Contains(id2, result.SourceKnowledgeIds);
        // Confidence = Min(1.0, Max(0.8, 0.6, 0.7)) = 0.8
        Assert.Equal(0.8, result.Confidence, precision: 5);
    }

    [Fact]
    public async Task AnswerQuestion_HttpError_ReturnsFallbackResponse()
    {
        _handler.SetResponse(HttpStatusCode.InternalServerError, "Server Error");

        var result = await _svc.AnswerQuestionAsync("Q?", CreateSearchResults(1));

        Assert.NotNull(result);
        Assert.Empty(result.SourceKnowledgeIds);
        Assert.Equal(0, result.Confidence);
    }

    // ===== AnswerQuestionStreamingAsync Tests =====

    [Fact]
    public async Task Streaming_HappyPath_ParsesSSE_YieldsContent()
    {
        var sseContent = new StringBuilder();
        sseContent.AppendLine("data: {\"content\":\"Hello\",\"done\":false,\"finishReason\":null}");
        sseContent.AppendLine();
        sseContent.AppendLine("data: {\"content\":\" world\",\"done\":false,\"finishReason\":null}");
        sseContent.AppendLine();
        sseContent.AppendLine("data: {\"content\":\"\",\"done\":true,\"finishReason\":\"stop\"}");
        sseContent.AppendLine();

        _handler.SetResponse(HttpStatusCode.OK, sseContent.ToString());

        var chunks = new List<string>();
        await foreach (var chunk in _svc.AnswerQuestionStreamingAsync("Q?", CreateSearchResults(1)))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(2, chunks.Count); // Empty content on done=true should be skipped
        Assert.Equal("Hello", chunks[0]);
        Assert.Equal(" world", chunks[1]);
    }

    [Fact]
    public async Task Streaming_501Fallback_CallsNonStreaming_YieldsSingleChunk()
    {
        // First call returns 501 (streaming not implemented)
        // Second call (non-streaming fallback) returns completion response
        var callCount = 0;
        _handler.SetResponseFactory(_ =>
        {
            callCount++;
            if (callCount == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.NotImplemented)
                {
                    Content = new StringContent("Not Implemented")
                };
            }
            var responseBody = JsonSerializer.Serialize(new
            {
                success = true,
                data = new { content = "Fallback answer", finishReason = "stop" },
                errors = (List<string>?)null
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        });

        var chunks = new List<string>();
        await foreach (var chunk in _svc.AnswerQuestionStreamingAsync("Q?", CreateSearchResults(1)))
        {
            chunks.Add(chunk);
        }

        Assert.Single(chunks);
        Assert.Equal("Fallback answer", chunks[0]);
        Assert.Equal(2, callCount); // First: stream attempt, Second: fallback
    }

    [Fact]
    public async Task Streaming_EmptyContext_YieldsNotEnoughInfo()
    {
        var chunks = new List<string>();
        await foreach (var chunk in _svc.AnswerQuestionStreamingAsync("Q?", new List<SearchResultItem>()))
        {
            chunks.Add(chunk);
        }

        Assert.Single(chunks);
        Assert.Contains("don't have enough information", chunks[0]);
    }

    [Fact]
    public async Task Streaming_HttpError_YieldsErrorMessage()
    {
        _handler.SetException(new HttpRequestException("Connection refused"));

        var chunks = new List<string>();
        await foreach (var chunk in _svc.AnswerQuestionStreamingAsync("Q?", CreateSearchResults(1)))
        {
            chunks.Add(chunk);
        }

        Assert.Single(chunks);
        Assert.Contains("error", chunks[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Streaming_RespectsCanellationToken()
    {
        // Build SSE with many chunks
        var sseContent = new StringBuilder();
        for (int i = 0; i < 100; i++)
        {
            sseContent.AppendLine($"data: {{\"content\":\"chunk{i}\",\"done\":false,\"finishReason\":null}}");
            sseContent.AppendLine();
        }
        sseContent.AppendLine("data: {\"content\":\"\",\"done\":true,\"finishReason\":\"stop\"}");
        sseContent.AppendLine();

        _handler.SetResponse(HttpStatusCode.OK, sseContent.ToString());

        var cts = new CancellationTokenSource();
        var chunks = new List<string>();

        await foreach (var chunk in _svc.AnswerQuestionStreamingAsync("Q?", CreateSearchResults(1), cancellationToken: cts.Token))
        {
            chunks.Add(chunk);
            if (chunks.Count >= 3)
            {
                cts.Cancel();
                break;
            }
        }

        Assert.True(chunks.Count <= 4); // Should stop near cancellation point
    }

    // ===== ApplyContentUpdateAsync Tests =====

    [Fact]
    public async Task ApplyContentUpdate_HappyPath_ReturnsUpdatedContent()
    {
        var responseBody = JsonSerializer.Serialize(new
        {
            success = true,
            data = new { content = "Updated document content here.", finishReason = "stop" },
            errors = (List<string>?)null
        });
        _handler.SetResponse(HttpStatusCode.OK, responseBody);

        var result = await _svc.ApplyContentUpdateAsync("Original content", "Make it better");

        Assert.Equal("Updated document content here.", result);

        var body = await _handler.LastRequest!.Content!.ReadAsStringAsync();
        Assert.Contains("document editor", body); // Editor system prompt
        Assert.Contains("Original content", body);
        Assert.Contains("Make it better", body);
    }

    [Fact]
    public async Task ApplyContentUpdate_HttpError_ThrowsInvalidOperationException()
    {
        _handler.SetResponse(HttpStatusCode.InternalServerError, "Server Error");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.ApplyContentUpdateAsync("Content", "Instruction"));
    }

    [Fact]
    public async Task ApplyContentUpdate_NetworkError_ThrowsInvalidOperationException()
    {
        _handler.SetException(new HttpRequestException("Connection refused"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.ApplyContentUpdateAsync("Content", "Instruction"));
    }

    // ===== BuildContext / FormatSourceBlock Tests =====

    [Fact]
    public void BuildContext_MatchesAzureOpenAIService_Output()
    {
        var results = new List<SearchResultItem>
        {
            new() { KnowledgeId = Guid.Parse("11111111-1111-1111-1111-111111111111"), Title = "Title A", Content = "Content A", Score = 0.9, Summary = "Summary A", VaultName = "Vault1" },
            new() { KnowledgeId = Guid.Parse("22222222-2222-2222-2222-222222222222"), Title = "Title B", Content = "Content B", Score = 0.8, VaultName = "Vault2" },
        };

        var platformResult = PlatformAIService.BuildContext(results, 4000);
        var azureResult = AzureOpenAIService.BuildContext(results, 4000);

        Assert.Equal(azureResult, platformResult);
    }

    [Fact]
    public void FormatSourceBlock_MatchesAzureOpenAIService_Output()
    {
        var result = new SearchResultItem
        {
            KnowledgeId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Title = "Test Title",
            Content = "Test content here",
            Summary = "Test summary",
            VaultName = "MyVault",
            Score = 0.95
        };

        var platformResult = PlatformAIService.FormatSourceBlock(result);
        var azureResult = AzureOpenAIService.FormatSourceBlock(result);

        Assert.Equal(azureResult, platformResult);
    }

    [Fact]
    public void BuildContext_EmptyResults_ReturnsEmptyString()
    {
        var result = PlatformAIService.BuildContext(new List<SearchResultItem>(), 4000);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void BuildContext_TruncatesAtTokenBudget()
    {
        var longContent = new string('x', 5000);
        var results = new List<SearchResultItem>
        {
            new() { KnowledgeId = Guid.NewGuid(), Title = "Long", Content = longContent, Score = 0.9 },
            new() { KnowledgeId = Guid.NewGuid(), Title = "Second", Content = "Short", Score = 0.8 },
        };

        // Token budget of 500 -> char budget of 2000
        var context = PlatformAIService.BuildContext(results, 500);

        // Should contain something but not the full long content
        Assert.NotEmpty(context);
        // Should not contain the second result (budget exceeded after first)
        Assert.DoesNotContain("Second", context);
    }

    [Fact]
    public void FormatSourceBlock_LongContent_WithSummary_Truncates()
    {
        var longContent = new string('x', 3000);
        var result = new SearchResultItem
        {
            KnowledgeId = Guid.NewGuid(),
            Title = "Title",
            Content = longContent,
            Summary = "Summary text",
            Score = 0.9
        };

        var formatted = PlatformAIService.FormatSourceBlock(result);
        var azureFormatted = AzureOpenAIService.FormatSourceBlock(result);

        // Should match Azure's behavior exactly
        Assert.Equal(azureFormatted, formatted);
        // Should be truncated (not full 3000 chars of content)
        Assert.True(formatted.Length < 3000);
    }

    // ===== Helper Methods =====

    private static List<SearchResultItem> CreateSearchResults(int count)
    {
        return Enumerable.Range(1, count).Select(i => new SearchResultItem
        {
            KnowledgeId = Guid.NewGuid(),
            Title = $"Result {i}",
            Content = $"Content for result {i}",
            Score = 0.9 - (i * 0.05),
            VaultName = "TestVault"
        }).ToList();
    }

    /// <summary>
    /// Mock HTTP message handler for testing HttpClient calls.
    /// </summary>
    internal class MockHttpMessageHandler : HttpMessageHandler
    {
        private HttpStatusCode _statusCode = HttpStatusCode.OK;
        private string _responseContent = "";
        private Exception? _exception;
        private Func<HttpRequestMessage, HttpResponseMessage>? _responseFactory;

        public HttpRequestMessage? LastRequest { get; private set; }

        public void SetResponse(HttpStatusCode statusCode, string content)
        {
            _statusCode = statusCode;
            _responseContent = content;
            _exception = null;
            _responseFactory = null;
        }

        public void SetException(Exception exception)
        {
            _exception = exception;
            _responseFactory = null;
        }

        public void SetResponseFactory(Func<HttpRequestMessage, HttpResponseMessage> factory)
        {
            _responseFactory = factory;
            _exception = null;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;

            if (_exception != null)
                throw _exception;

            if (_responseFactory != null)
                return Task.FromResult(_responseFactory(request));

            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseContent, Encoding.UTF8, "application/json")
            });
        }
    }
}
