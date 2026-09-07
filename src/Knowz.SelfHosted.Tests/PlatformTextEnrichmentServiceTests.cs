using System.Net;
using System.Text;
using System.Text.Json;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class PlatformTextEnrichmentServiceTests
{
    private readonly ILogger<PlatformTextEnrichmentService> _logger;
    private readonly IConfiguration _configuration;
    private readonly PromptResolutionService _promptResolution;

    public PlatformTextEnrichmentServiceTests()
    {
        _logger = Substitute.For<ILogger<PlatformTextEnrichmentService>>();
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KnowzPlatform:BaseUrl"] = "https://api.knowz.io",
                ["KnowzPlatform:ApiKey"] = "test-api-key-123"
            })
            .Build();

        // Create a PromptResolutionService that returns defaults (no DB)
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var promptLogger = Substitute.For<ILogger<PromptResolutionService>>();
        _promptResolution = new PromptResolutionService(scopeFactory, promptLogger);
    }

    private PlatformTextEnrichmentService CreateService(MockHttpMessageHandler handler)
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.knowz.io") };
        httpClientFactory.CreateClient("KnowzPlatformClient").Returns(httpClient);
        return new PlatformTextEnrichmentService(httpClientFactory, _configuration, _promptResolution, _logger);
    }

    private static string WrapInApiResponse<T>(T data) =>
        JsonSerializer.Serialize(new { success = true, data });

    private static string WrapInErrorResponse(string error) =>
        JsonSerializer.Serialize(new { success = false, data = (object?)null, errors = new[] { error } });

    // === GenerateTitleAsync Tests ===

    [Fact]
    public async Task GenerateTitle_SendsCompletionRequestWithTitleSystemPrompt()
    {
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler((request, ct) =>
        {
            capturedBody = request.Content!.ReadAsStringAsync(ct).Result;
            var response = WrapInApiResponse(new { content = "Generated Title", finishReason = "stop" });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });

        var svc = CreateService(handler);
        await svc.GenerateTitleAsync("Some test content");

        Assert.NotNull(capturedBody);
        Assert.Contains("title generator", capturedBody);
        Assert.Contains("Some test content", capturedBody);
    }

    [Fact]
    public async Task GenerateTitle_ReturnsTrimmedTitle()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            var response = WrapInApiResponse(new { content = "  A Clean Title  ", finishReason = "stop" });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });

        var svc = CreateService(handler);
        var result = await svc.GenerateTitleAsync("Some content");

        Assert.Equal("A Clean Title", result);
    }

    [Fact]
    public async Task GenerateTitle_StripsQuotesFromResponse()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            var response = WrapInApiResponse(new { content = "\"Quoted Title\"", finishReason = "stop" });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });

        var svc = CreateService(handler);
        var result = await svc.GenerateTitleAsync("Some content");

        Assert.Equal("Quoted Title", result);
    }

    [Fact]
    public async Task GenerateTitle_ReturnsNullOnHttpError()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var svc = CreateService(handler);
        var result = await svc.GenerateTitleAsync("Some content");

        Assert.Null(result);
    }

    [Fact]
    public async Task GenerateTitle_ReturnsNullOnEmptyResponseContent()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            var response = WrapInApiResponse(new { content = "   ", finishReason = "stop" });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });

        var svc = CreateService(handler);
        var result = await svc.GenerateTitleAsync("Some content");

        Assert.Null(result);
    }

    [Fact]
    public async Task GenerateTitle_TruncatesContentBefore_Sending()
    {
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler((request, ct) =>
        {
            capturedBody = request.Content!.ReadAsStringAsync(ct).Result;
            var response = WrapInApiResponse(new { content = "Title", finishReason = "stop" });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });

        var svc = CreateService(handler);
        var longContent = new string('x', 15_000);
        await svc.GenerateTitleAsync(longContent);

        Assert.NotNull(capturedBody);
        // The body should not contain the full 15,000 chars
        Assert.DoesNotContain(longContent, capturedBody);
        // But should contain exactly MaxContentChars worth of x's
        var truncated = new string('x', PlatformTextEnrichmentService.MaxContentChars);
        Assert.Contains(truncated, capturedBody);
    }

    [Fact]
    public async Task GenerateTitle_SendsMaxTokens50()
    {
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler((request, ct) =>
        {
            capturedBody = request.Content!.ReadAsStringAsync(ct).Result;
            var response = WrapInApiResponse(new { content = "Title", finishReason = "stop" });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });

        var svc = CreateService(handler);
        await svc.GenerateTitleAsync("Content");

        Assert.NotNull(capturedBody);
        Assert.Contains("\"maxTokens\":50", capturedBody);
    }

    // === SummarizeAsync Tests ===

    [Fact]
    public async Task Summarize_SendsRequestToSummarizeEndpoint()
    {
        string? capturedUri = null;
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler((request, ct) =>
        {
            capturedUri = request.RequestUri?.PathAndQuery;
            capturedBody = request.Content!.ReadAsStringAsync(ct).Result;
            var response = WrapInApiResponse(new { summary = "A concise summary." });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });

        var svc = CreateService(handler);
        await svc.SummarizeAsync("Long content here", 150);

        Assert.Equal("/api/v1/ai-services/summarize", capturedUri);
        Assert.NotNull(capturedBody);
        Assert.Contains("Long content here", capturedBody);
        Assert.Contains("150", capturedBody);
        Assert.Contains("concise", capturedBody);
    }

    [Fact]
    public async Task Summarize_ReturnsSummaryFromResponse()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            var response = WrapInApiResponse(new { summary = "This is the summary." });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });

        var svc = CreateService(handler);
        var result = await svc.SummarizeAsync("Some content");

        Assert.Equal("This is the summary.", result);
    }

    [Fact]
    public async Task Summarize_ReturnsNullOnHttpError()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var svc = CreateService(handler);
        var result = await svc.SummarizeAsync("Some content");

        Assert.Null(result);
    }

    [Fact]
    public async Task Summarize_ReturnsNullOnEmptyResponse()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            var response = WrapInApiResponse(new { summary = "" });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });

        var svc = CreateService(handler);
        var result = await svc.SummarizeAsync("Some content");

        Assert.Null(result);
    }

    [Fact]
    public async Task Summarize_TruncatesContentBeforeSending()
    {
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler((request, ct) =>
        {
            capturedBody = request.Content!.ReadAsStringAsync(ct).Result;
            var response = WrapInApiResponse(new { summary = "Summary" });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });

        var svc = CreateService(handler);
        await svc.SummarizeAsync(new string('y', 15_000));

        Assert.NotNull(capturedBody);
        Assert.DoesNotContain(new string('y', 15_000), capturedBody);
    }

    // === ExtractTagsAsync Tests ===

    [Fact]
    public async Task ExtractTags_SendsCompletionRequestWithTagsSystemPrompt()
    {
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler((request, ct) =>
        {
            capturedBody = request.Content!.ReadAsStringAsync(ct).Result;
            var response = WrapInApiResponse(new { content = "[\"tag1\", \"tag2\"]", finishReason = "stop" });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });

        var svc = CreateService(handler);
        await svc.ExtractTagsAsync("Title", "Content", 5);

        Assert.NotNull(capturedBody);
        Assert.Contains("tag extraction", capturedBody);
        Assert.Contains("5", capturedBody);
    }

    [Fact]
    public async Task ExtractTags_IncludesTitleInUserMessage()
    {
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler((request, ct) =>
        {
            capturedBody = request.Content!.ReadAsStringAsync(ct).Result;
            var response = WrapInApiResponse(new { content = "[\"tag1\"]", finishReason = "stop" });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });

        var svc = CreateService(handler);
        await svc.ExtractTagsAsync("My Document Title", "Some content");

        Assert.NotNull(capturedBody);
        Assert.Contains("Title: My Document Title", capturedBody);
        Assert.Contains("Some content", capturedBody);
    }

    [Fact]
    public async Task ExtractTags_OmitsTitleWhenEmpty()
    {
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler((request, ct) =>
        {
            capturedBody = request.Content!.ReadAsStringAsync(ct).Result;
            var response = WrapInApiResponse(new { content = "[\"tag1\"]", finishReason = "stop" });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });

        var svc = CreateService(handler);
        await svc.ExtractTagsAsync("", "Just the content");

        Assert.NotNull(capturedBody);
        Assert.DoesNotContain("Title:", capturedBody);
        Assert.Contains("Just the content", capturedBody);
    }

    [Fact]
    public async Task ExtractTags_ParsesJsonArrayResponse()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            var response = WrapInApiResponse(new { content = "[\"machine-learning\", \"python\", \"data\"]", finishReason = "stop" });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });

        var svc = CreateService(handler);
        var result = await svc.ExtractTagsAsync("Title", "Content");

        Assert.Equal(3, result.Count);
        Assert.Equal("machine-learning", result[0]);
        Assert.Equal("python", result[1]);
        Assert.Equal("data", result[2]);
    }

    [Fact]
    public async Task ExtractTags_ReturnsEmptyListOnHttpError()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.BadGateway));

        var svc = CreateService(handler);
        var result = await svc.ExtractTagsAsync("Title", "Content");

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task ExtractTags_ReturnsEmptyListOnInvalidJson()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            var response = WrapInApiResponse(new { content = "not valid json at all", finishReason = "stop" });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });

        var svc = CreateService(handler);
        var result = await svc.ExtractTagsAsync("Title", "Content");

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task ExtractTags_LimitsReturnedTagsToMaxTags()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            var response = WrapInApiResponse(new
            {
                content = "[\"a\", \"b\", \"c\", \"d\", \"e\", \"f\", \"g\"]",
                finishReason = "stop"
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });

        var svc = CreateService(handler);
        var result = await svc.ExtractTagsAsync("Title", "Content", maxTags: 3);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task ExtractTags_SendsMaxTokens200()
    {
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler((request, ct) =>
        {
            capturedBody = request.Content!.ReadAsStringAsync(ct).Result;
            var response = WrapInApiResponse(new { content = "[]", finishReason = "stop" });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });

        var svc = CreateService(handler);
        await svc.ExtractTagsAsync("Title", "Content");

        Assert.NotNull(capturedBody);
        Assert.Contains("\"maxTokens\":200", capturedBody);
    }

    // === Auth Header Tests ===

    [Fact]
    public async Task AllMethods_IncludeApiKeyHeader()
    {
        string? capturedApiKey = null;
        var handler = new MockHttpMessageHandler((request, _) =>
        {
            if (request.Headers.TryGetValues("X-Api-Key", out var values))
                capturedApiKey = values.First();
            var response = WrapInApiResponse(new { content = "result", finishReason = "stop" });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });

        var svc = CreateService(handler);
        await svc.GenerateTitleAsync("content");

        Assert.Equal("test-api-key-123", capturedApiKey);
    }

    // === Static Helper Tests ===

    [Fact]
    public void TruncateContent_ShortContent_ReturnsSame()
    {
        var content = "Short text";
        var result = PlatformTextEnrichmentService.TruncateContent(content);
        Assert.Equal(content, result);
    }

    [Fact]
    public void TruncateContent_LongContent_Truncates()
    {
        var content = new string('x', 15_000);
        var result = PlatformTextEnrichmentService.TruncateContent(content);
        Assert.Equal(PlatformTextEnrichmentService.MaxContentChars, result.Length);
    }

    [Fact]
    public void TruncateContent_ExactlyMaxLength_ReturnsSame()
    {
        var content = new string('a', PlatformTextEnrichmentService.MaxContentChars);
        var result = PlatformTextEnrichmentService.TruncateContent(content);
        Assert.Equal(content, result);
    }

    [Fact]
    public void TruncateContent_EmptyString_ReturnsEmpty()
    {
        var result = PlatformTextEnrichmentService.TruncateContent("");
        Assert.Equal("", result);
    }

    [Fact]
    public void ParseTagsJson_ValidJsonArray_ReturnsTags()
    {
        var json = "[\"machine-learning\", \"python\", \"data-analysis\"]";
        var result = PlatformTextEnrichmentService.ParseTagsJson(json, 5);
        Assert.Equal(3, result.Count);
        Assert.Equal("machine-learning", result[0]);
        Assert.Equal("python", result[1]);
        Assert.Equal("data-analysis", result[2]);
    }

    [Fact]
    public void ParseTagsJson_MarkdownCodeBlock_ExtractsJson()
    {
        var response = "```json\n[\"tag1\", \"tag2\"]\n```";
        var result = PlatformTextEnrichmentService.ParseTagsJson(response, 5);
        Assert.Equal(2, result.Count);
        Assert.Equal("tag1", result[0]);
        Assert.Equal("tag2", result[1]);
    }

    [Fact]
    public void ParseTagsJson_InvalidJson_ReturnsEmptyList()
    {
        var response = "not valid json";
        var result = PlatformTextEnrichmentService.ParseTagsJson(response, 5);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseTagsJson_RespectsMaxTags()
    {
        var json = "[\"a\", \"b\", \"c\", \"d\", \"e\", \"f\"]";
        var result = PlatformTextEnrichmentService.ParseTagsJson(json, 3);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void ParseTagsJson_FiltersEmptyStrings()
    {
        var json = "[\"valid\", \"\", \"  \", \"also-valid\"]";
        var result = PlatformTextEnrichmentService.ParseTagsJson(json, 5);
        Assert.Equal(2, result.Count);
        Assert.Equal("valid", result[0]);
        Assert.Equal("also-valid", result[1]);
    }

    [Fact]
    public void ParseTagsJson_EmptyArray_ReturnsEmptyList()
    {
        var json = "[]";
        var result = PlatformTextEnrichmentService.ParseTagsJson(json, 5);
        Assert.Empty(result);
    }

    // === ApiResponse unwrapping Tests ===

    [Fact]
    public async Task GenerateTitle_ReturnsNullOnApiResponseFailure()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            var response = WrapInErrorResponse("AI service unavailable");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });

        var svc = CreateService(handler);
        var result = await svc.GenerateTitleAsync("Some content");

        Assert.Null(result);
    }

    [Fact]
    public async Task Summarize_ReturnsNullOnApiResponseFailure()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            var response = WrapInErrorResponse("Quota exceeded");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });

        var svc = CreateService(handler);
        var result = await svc.SummarizeAsync("Some content");

        Assert.Null(result);
    }

    [Fact]
    public async Task ExtractTags_ReturnsEmptyOnApiResponseFailure()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            var response = WrapInErrorResponse("Internal error");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });

        var svc = CreateService(handler);
        var result = await svc.ExtractTagsAsync("Title", "Content");

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    // === MockHttpMessageHandler ===

    internal class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request, cancellationToken));
        }
    }
}
