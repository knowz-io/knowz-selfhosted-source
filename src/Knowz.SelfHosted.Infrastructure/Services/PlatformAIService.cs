using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Knowz.Core.Interfaces;
using Knowz.Core.Models;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// Proxies AI operations to the Knowz Platform AI Services API.
/// Drop-in replacement for AzureOpenAIService via DI switch in OpenAIExtensions.
/// </summary>
public class PlatformAIService : IOpenAIService, IStreamingOpenAIService, IContentAmendmentService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _defaultSystemPrompt;
    private readonly ILogger<PlatformAIService> _logger;

    private static readonly JsonSerializerOptions s_serializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions s_deserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal const int DefaultTokenBudget = 4000;
    internal const int ResearchTokenBudget = 8000;
    internal const int ApproxCharsPerToken = 4;

    public PlatformAIService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<PlatformAIService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _baseUrl = configuration["KnowzPlatform:BaseUrl"]
            ?? throw new InvalidOperationException("KnowzPlatform:BaseUrl is required");
        _apiKey = configuration["KnowzPlatform:ApiKey"]
            ?? throw new InvalidOperationException("KnowzPlatform:ApiKey is required");
        _defaultSystemPrompt = configuration["SelfHosted:SystemPrompt"]
            ?? DefaultSystemPrompt;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<float[]?> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = CreateRequest(HttpMethod.Post, "/api/v1/ai-services/embeddings",
                new PlatformEmbeddingsRequest(new[] { text }));

            using var client = _httpClientFactory.CreateClient("KnowzPlatformClient");
            var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = UnwrapResponse<PlatformEmbeddingsResponse>(body, "GenerateEmbedding");

            return result.Embeddings[0];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to generate embedding via platform API, vector search will be unavailable for this query");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<AnswerResponse> AnswerQuestionAsync(
        string question,
        List<SearchResultItem> searchResults,
        string? vaultSystemPrompt = null,
        bool researchMode = false,
        string userTimezone = "UTC",
        CancellationToken cancellationToken = default)
    {
        var tokenBudget = researchMode ? ResearchTokenBudget : DefaultTokenBudget;
        // FEAT_SelfHostedTemporalAwareness
        var contextText = BuildContext(searchResults, tokenBudget, userTimezone, _logger);

        if (string.IsNullOrWhiteSpace(contextText))
        {
            return new AnswerResponse
            {
                Answer = "I don't have enough information in the knowledge base to answer this question.",
                SourceKnowledgeIds = new List<Guid>(),
                Confidence = 0
            };
        }

        try
        {
            // FEAT_SelfHostedTemporalAwareness: build dynamic system prompt.
            // Note: the platform AI service is a proxy — the main platform
            // has its own temporal awareness for its own chat pipelines. We
            // still build a temporal system prompt here because the completion
            // endpoint used is a plain LLM completion, not the platform's
            // full chat pipeline.
            var customOverride = !string.IsNullOrWhiteSpace(vaultSystemPrompt)
                ? vaultSystemPrompt
                : _defaultSystemPrompt;
            var systemPrompt = TemporalPromptBuilder.BuildSystemPrompt(
                customOverride, userTimezone, DateTime.UtcNow, _logger);
            var completionRequest = new PlatformCompletionRequest(
                Messages: new[]
                {
                    new PlatformChatMessage("user",
                        $"Context from knowledge base:\n\n{contextText}\n\n---\n\nQuestion: {question}")
                },
                MaxTokens: researchMode ? 4000 : 2000,
                SystemPrompt: systemPrompt);

            _logger.LogInformation(
                "Answering question via platform: '{Question}' with {SourceCount} sources, research={Research}",
                question.Length > 50 ? question[..50] + "..." : question,
                searchResults.Count, researchMode);

            var request = CreateRequest(HttpMethod.Post, "/api/v1/ai-services/completion", completionRequest);

            using var client = _httpClientFactory.CreateClient("KnowzPlatformClient");
            var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = UnwrapResponse<PlatformCompletionResponse>(body, "AnswerQuestion");

            var sourceIds = searchResults
                .Where(r => r.KnowledgeId != Guid.Empty)
                .Select(r => r.KnowledgeId)
                .Distinct()
                .ToList();

            return new AnswerResponse
            {
                Answer = result.Content,
                SourceKnowledgeIds = sourceIds,
                Confidence = searchResults.Count > 0
                    ? NormalizeConfidence(searchResults)
                    : 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to answer question via platform API");
            return new AnswerResponse
            {
                Answer = string.Empty,
                SourceKnowledgeIds = new List<Guid>(),
                Confidence = 0
            };
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> AnswerQuestionStreamingAsync(
        string question,
        List<SearchResultItem> searchResults,
        string? vaultSystemPrompt = null,
        bool researchMode = false,
        string userTimezone = "UTC",
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tokenBudget = researchMode ? ResearchTokenBudget : DefaultTokenBudget;
        // FEAT_SelfHostedTemporalAwareness
        var contextText = BuildContext(searchResults, tokenBudget, userTimezone, _logger);

        if (string.IsNullOrWhiteSpace(contextText))
        {
            yield return "I don't have enough information in the knowledge base to answer this question.";
            yield break;
        }

        var customOverride = !string.IsNullOrWhiteSpace(vaultSystemPrompt)
            ? vaultSystemPrompt
            : _defaultSystemPrompt;
        var systemPrompt = TemporalPromptBuilder.BuildSystemPrompt(
            customOverride, userTimezone, DateTime.UtcNow, _logger);
        var completionRequest = new PlatformCompletionRequest(
            Messages: new[]
            {
                new PlatformChatMessage("user",
                    $"Context from knowledge base:\n\n{contextText}\n\n---\n\nQuestion: {question}")
            },
            MaxTokens: researchMode ? 4000 : 2000,
            SystemPrompt: systemPrompt);

        _logger.LogInformation(
            "Streaming answer via platform: '{Question}' with {SourceCount} sources, research={Research}",
            question.Length > 50 ? question[..50] + "..." : question,
            searchResults.Count, researchMode);

        // Collect chunks or error from the streaming call.
        // We use a list to buffer because yield cannot appear in try/catch blocks.
        var chunks = new List<string>();
        string? errorMessage = null;

        HttpResponseMessage? streamResponse = null;
        try
        {
            var streamRequest = CreateRequest(HttpMethod.Post, "/api/v1/ai-services/completion/stream", completionRequest);

            using var client = _httpClientFactory.CreateClient("KnowzPlatformClient");
            streamResponse = await client.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            // 501 fallback: platform doesn't support streaming yet
            if (streamResponse.StatusCode == System.Net.HttpStatusCode.NotImplemented)
            {
                streamResponse.Dispose();
                streamResponse = null;

                var fallbackRequest = CreateRequest(HttpMethod.Post, "/api/v1/ai-services/completion", completionRequest);
                var fallbackResponse = await client.SendAsync(fallbackRequest, cancellationToken);
                fallbackResponse.EnsureSuccessStatusCode();

                var fallbackBody = await fallbackResponse.Content.ReadAsStringAsync(cancellationToken);
                var fallbackResult = UnwrapResponse<PlatformCompletionResponse>(fallbackBody, "AnswerQuestionStreaming-Fallback");
                chunks.Add(fallbackResult.Content);
            }
            else
            {
                streamResponse.EnsureSuccessStatusCode();

                using var stream = await streamResponse.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                while (!reader.EndOfStream)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line == null)
                        break;

                    if (!line.StartsWith("data: "))
                        continue;

                    var json = line["data: ".Length..];
                    if (string.IsNullOrWhiteSpace(json))
                        continue;

                    PlatformStreamChunk? chunk;
                    try
                    {
                        chunk = JsonSerializer.Deserialize<PlatformStreamChunk>(json, s_deserializeOptions);
                    }
                    catch (JsonException)
                    {
                        continue; // Skip malformed chunks
                    }

                    if (chunk == null)
                        continue;

                    if (chunk.Done)
                        break;

                    if (!string.IsNullOrEmpty(chunk.Content))
                        chunks.Add(chunk.Content);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected - yield whatever we have so far
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stream answer via platform API");
            errorMessage = "An error occurred while streaming the response. Please try again.";
        }
        finally
        {
            streamResponse?.Dispose();
        }

        // Yield collected chunks outside of try/catch
        foreach (var chunk in chunks)
        {
            yield return chunk;
        }

        if (errorMessage != null)
        {
            yield return errorMessage;
        }
    }

    /// <inheritdoc />
    public async Task<string> ApplyContentUpdateAsync(
        string existingContent,
        string instruction,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var completionRequest = new PlatformCompletionRequest(
                Messages: new[]
                {
                    new PlatformChatMessage("user",
                        $"Document:\n\n{existingContent}\n\n---\n\nInstruction: {instruction}")
                },
                MaxTokens: 4000,
                SystemPrompt: "You are a document editor. Apply the user's instruction to modify the document. Return ONLY the updated document content -- no explanations, no markdown fences.");

            _logger.LogInformation(
                "Amending content via platform: instructionLength={InstructionLength}, contentLength={ContentLength}",
                instruction.Length, existingContent.Length);

            var request = CreateRequest(HttpMethod.Post, "/api/v1/ai-services/completion", completionRequest);

            using var client = _httpClientFactory.CreateClient("KnowzPlatformClient");
            var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = UnwrapResponse<PlatformCompletionResponse>(body, "ApplyContentUpdate");

            return result.Content;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "Failed to apply content update via platform API.", ex);
        }
    }

    // --- Internal helpers (visible for testing via InternalsVisibleTo) ---

    internal static string BuildContext(
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

    internal static string FormatSourceBlock(
        SearchResultItem result,
        string userTimezone = "UTC",
        ILogger? logger = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### {result.Title} [{result.KnowledgeId}]");

        // FEAT_SelfHostedTemporalAwareness: emit Created/Updated before body.
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

        if (!string.IsNullOrWhiteSpace(result.Summary))
        {
            sb.AppendLine($"Summary: {result.Summary}");
        }

        var content = result.Content;
        if (content.Length > 2000 && !string.IsNullOrWhiteSpace(result.Summary))
        {
            content = result.Summary + "\n\n" + content[..1500] + "...";
        }
        sb.AppendLine(content);

        if (!string.IsNullOrWhiteSpace(result.VaultName))
        {
            sb.AppendLine($"Source: {result.VaultName}");
        }

        return sb.ToString();
    }

    private T UnwrapResponse<T>(string responseBody, string operationName)
    {
        var envelope = JsonSerializer.Deserialize<PlatformApiResponse<T>>(responseBody, s_deserializeOptions);
        if (envelope == null)
            throw new InvalidOperationException($"{operationName}: Failed to deserialize platform response");

        if (!envelope.Success)
        {
            var errors = envelope.Errors != null ? string.Join(", ", envelope.Errors) : "Unknown error";
            throw new InvalidOperationException($"{operationName}: Platform API returned failure: {errors}");
        }

        if (envelope.Data == null)
            throw new InvalidOperationException($"{operationName}: Platform API returned null data");

        return envelope.Data;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, object? body = null)
    {
        var uri = new Uri(new Uri(_baseUrl), path);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-Api-Key", _apiKey);

        if (body != null)
        {
            var json = JsonSerializer.Serialize(body, s_serializeOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    internal const string DefaultSystemPrompt = """
        You are a helpful knowledge assistant. Answer questions based on the provided context from the knowledge base.

        Guidelines:
        - Answer based on the provided context. If the context doesn't contain enough information, say so.
        - Reference specific sources by their title when citing information.
        - Be concise but thorough. Provide specific details from the sources.
        - If multiple sources provide different perspectives, present them.
        - Format your response using markdown for readability.
        """;

    /// <summary>
    /// Normalizes search scores to a 0-1 confidence range.
    /// Azure AI Search RRF hybrid scores are typically 0.01-0.05; local cosine similarity scores are 0-1.
    /// </summary>
    private static double NormalizeConfidence(List<SearchResultItem> searchResults)
    {
        if (searchResults.Count == 0) return 0;
        var maxScore = searchResults.Max(r => r.Score);
        return maxScore < 0.1 ? Math.Min(1.0, maxScore * 10) : Math.Min(1.0, maxScore);
    }

    // --- Internal DTOs (local to service, not shared) ---

    internal record PlatformEmbeddingsRequest(string[] Input);

    internal record PlatformCompletionRequest(
        PlatformChatMessage[] Messages,
        int? MaxTokens = null,
        string? SystemPrompt = null);

    internal record PlatformChatMessage(string Role, string Content);

    internal record PlatformApiResponse<T>(bool Success, T? Data, List<string>? Errors);

    internal record PlatformEmbeddingsResponse(float[][] Embeddings, string Model);

    internal record PlatformCompletionResponse(string Content, string FinishReason);

    internal record PlatformStreamChunk(string? Content, bool Done, string? FinishReason);
}
