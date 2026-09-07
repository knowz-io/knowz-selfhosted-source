using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// Proxies text enrichment operations (title generation, summarization, tag extraction)
/// to the Knowz Platform AI Services API. Enables selfhosted deployments to enrich
/// knowledge items without directly configuring Azure OpenAI.
/// When a tenantId is provided, resolves configurable prompts via PromptResolutionService
/// (3-tier hierarchy: Platform → Tenant → User). Falls back to hardcoded defaults otherwise.
/// </summary>
public class PlatformTextEnrichmentService : ITextEnrichmentService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiKey;
    private readonly PromptResolutionService _promptResolution;
    private readonly ILogger<PlatformTextEnrichmentService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal const int MaxContentChars = 12_000;

    internal const string TitleSystemPrompt =
        "You are a title generator. Given the content below, generate a single concise, descriptive title of 5 to 10 words. Return ONLY the title text, nothing else. Do not include quotes, prefixes, or explanations.";

    internal const string TagsSystemPrompt =
        "You are a tag extraction assistant. Extract up to {0} relevant tags or keywords from the content below. Return ONLY a JSON array of lowercase strings. Example: [\"machine-learning\", \"python\", \"data-analysis\"]";

    public PlatformTextEnrichmentService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        PromptResolutionService promptResolution,
        ILogger<PlatformTextEnrichmentService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _apiKey = configuration["KnowzPlatform:ApiKey"]
            ?? throw new InvalidOperationException("KnowzPlatform:ApiKey is required");
        _promptResolution = promptResolution;
        _logger = logger;
    }

    public async Task<string?> GenerateTitleAsync(string content, CancellationToken ct = default, Guid? tenantId = null)
    {
        try
        {
            var truncated = TruncateContent(content);

            var systemPrompt = tenantId.HasValue
                ? await _promptResolution.ResolvePromptAsync(PromptKeys.TitlePrompt, tenantId.Value, ct: ct)
                : TitleSystemPrompt;

            var request = new PlatformCompletionRequest(
                Messages: new[]
                {
                    new PlatformChatMessage("system", systemPrompt),
                    new PlatformChatMessage("user", truncated)
                },
                MaxTokens: 50);

            var httpRequest = CreateRequest("/api/v1/ai-services/completion", request);
            var client = _httpClientFactory.CreateClient("KnowzPlatformClient");
            var response = await client.SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            var completionResponse = UnwrapResponse<PlatformCompletionResponse>(body, "GenerateTitle");
            if (completionResponse == null)
                return null;

            var title = completionResponse.Content.Trim().Trim('"', '\'');
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate title via platform API");
            return null;
        }
    }

    public async Task<string?> SummarizeAsync(string content, int maxWords = 100, CancellationToken ct = default, Guid? tenantId = null)
    {
        try
        {
            var truncated = TruncateContent(content);

            // When tenant-specific prompts are configured, use the completion endpoint
            // with the resolved prompt instead of the platform's built-in summarize endpoint.
            if (tenantId.HasValue)
            {
                var resolvedPrompt = await _promptResolution.ResolvePromptAsync(
                    PromptKeys.SummarizePrompt, tenantId.Value, formatArgs: new object[] { maxWords }, ct: ct);

                var completionRequest = new PlatformCompletionRequest(
                    Messages: new[]
                    {
                        new PlatformChatMessage("system", resolvedPrompt),
                        new PlatformChatMessage("user", truncated)
                    },
                    MaxTokens: maxWords * 2);

                var completionHttpRequest = CreateRequest("/api/v1/ai-services/completion", completionRequest);
                var completionClient = _httpClientFactory.CreateClient("KnowzPlatformClient");
                var completionResponse = await completionClient.SendAsync(completionHttpRequest, ct);
                completionResponse.EnsureSuccessStatusCode();

                var completionBody = await completionResponse.Content.ReadAsStringAsync(ct);
                var completionResult = UnwrapResponse<PlatformCompletionResponse>(completionBody, "Summarize");
                if (completionResult == null)
                    return null;

                var summaryText = completionResult.Content.Trim();
                return string.IsNullOrWhiteSpace(summaryText) ? null : summaryText;
            }

            // Default path: use the platform's built-in summarize endpoint
            var request = new PlatformSummarizeRequest(
                Content: truncated,
                MaxWords: maxWords,
                Style: "concise");

            var httpRequest = CreateRequest("/api/v1/ai-services/summarize", request);
            var client = _httpClientFactory.CreateClient("KnowzPlatformClient");
            var response = await client.SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            var summarizeResponse = UnwrapResponse<PlatformSummarizeResponse>(body, "Summarize");
            if (summarizeResponse == null)
                return null;

            var summary = summarizeResponse.Summary?.Trim();
            return string.IsNullOrWhiteSpace(summary) ? null : summary;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to summarize content via platform API");
            return null;
        }
    }

    public async Task<List<string>> ExtractTagsAsync(string title, string content, int maxTags = 5, CancellationToken ct = default, Guid? tenantId = null)
    {
        try
        {
            var truncated = TruncateContent(content);

            var systemPrompt = tenantId.HasValue
                ? await _promptResolution.ResolvePromptAsync(
                    PromptKeys.TagsPrompt, tenantId.Value, formatArgs: new object[] { maxTags }, ct: ct)
                : string.Format(TagsSystemPrompt, maxTags);

            var userPrompt = string.IsNullOrWhiteSpace(title)
                ? truncated
                : $"Title: {title}\n\n{truncated}";

            var request = new PlatformCompletionRequest(
                Messages: new[]
                {
                    new PlatformChatMessage("system", systemPrompt),
                    new PlatformChatMessage("user", userPrompt)
                },
                MaxTokens: 200);

            var httpRequest = CreateRequest("/api/v1/ai-services/completion", request);
            var client = _httpClientFactory.CreateClient("KnowzPlatformClient");
            var response = await client.SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            var completionResponse = UnwrapResponse<PlatformCompletionResponse>(body, "ExtractTags");
            if (completionResponse == null)
                return new List<string>();

            var responseText = completionResponse.Content.Trim();
            return ParseTagsJson(responseText, maxTags);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract tags via platform API");
            return new List<string>();
        }
    }

    // --- Internal helpers ---

    internal static string TruncateContent(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;
        return content.Length > MaxContentChars ? content[..MaxContentChars] : content;
    }

    internal static List<string> ParseTagsJson(string responseText, int maxTags)
    {
        var jsonText = responseText;
        var codeBlockMatch = Regex.Match(responseText, @"```(?:json)?\s*([\s\S]*?)\s*```");
        if (codeBlockMatch.Success)
            jsonText = codeBlockMatch.Groups[1].Value.Trim();

        try
        {
            var tags = JsonSerializer.Deserialize<List<string>>(jsonText);
            if (tags != null)
                return tags.Where(t => !string.IsNullOrWhiteSpace(t)).Take(maxTags).ToList();
        }
        catch (JsonException)
        {
            // Response wasn't valid JSON -- return empty
        }

        return new List<string>();
    }

    private T? UnwrapResponse<T>(string responseBody, string operationName)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<PlatformApiResponse<T>>(responseBody, DeserializeOptions);
            if (envelope == null || !envelope.Success)
            {
                var errors = envelope?.Errors != null ? string.Join(", ", envelope.Errors) : "unknown";
                _logger.LogWarning("Platform API returned failure for {Operation}: {Errors}", operationName, errors);
                return default;
            }
            return envelope.Data;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize platform response for {Operation}", operationName);
            return default;
        }
    }

    private HttpRequestMessage CreateRequest(string path, object body)
    {
        var json = JsonSerializer.Serialize(body, JsonOptions);
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Api-Key", _apiKey);
        return request;
    }

    public Task<string?> GenerateBriefSummaryAsync(string content, CancellationToken ct = default, Guid? tenantId = null)
    {
        // Platform already has its own brief summary capability; no separate call needed for proxy mode
        _logger.LogDebug("Brief summary generation not implemented for platform proxy mode");
        return Task.FromResult<string?>(null);
    }

    public Task<IList<string?>> GenerateChunkContextsAsync(
        string documentTitle, string? documentSummary,
        IList<(string Content, int Position)> chunks,
        CancellationToken ct = default)
    {
        // Platform handles contextual retrieval internally
        _logger.LogDebug("Chunk context generation not implemented for platform proxy mode");
        return Task.FromResult<IList<string?>>(Array.Empty<string?>());
    }

    // --- Internal DTOs ---

    internal record PlatformCompletionRequest(PlatformChatMessage[] Messages, int? MaxTokens = null);
    internal record PlatformChatMessage(string Role, string Content);
    internal record PlatformSummarizeRequest(string Content, int? MaxWords, string? Style);
    internal record PlatformApiResponse<T>(bool Success, T? Data, List<string>? Errors);
    internal record PlatformCompletionResponse(string Content, string FinishReason);
    internal record PlatformSummarizeResponse(string Summary);
}
