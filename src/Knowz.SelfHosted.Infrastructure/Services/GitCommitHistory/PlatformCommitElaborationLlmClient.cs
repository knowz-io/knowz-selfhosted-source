using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services.GitCommitHistory;

/// <summary>
/// LLM client that proxies commit-elaboration completions to the Knowz Platform
/// <c>/api/v1/ai-services/completion</c> endpoint. Used when <c>KnowzPlatform:Enabled</c>
/// is true.
///
/// The shape of the call mirrors <c>PlatformTextEnrichmentService</c> so that the platform
/// API sees a familiar request envelope. The system prompt and user prompt are delivered as
/// two chat messages — the system prompt becomes the <c>SystemPrompt</c> field (not a
/// system-role message) so the platform's default provider persona does not leak in.
///
/// On any failure (network, deserialization, API error), <see cref="ElaborateAsync"/> returns
/// null. The caller (<c>GitCommitHistoryService</c>) then leaves the child as a stub.
///
/// WorkGroupID: kc-feat-git-commit-knowledge-sync-20260410
/// NodeID: SelfHostedCommitHistoryParity (NODE-4)
/// </summary>
public sealed class PlatformCommitElaborationLlmClient : ICommitElaborationLlmClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiKey;
    private readonly ILogger<PlatformCommitElaborationLlmClient> _logger;

    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public PlatformCommitElaborationLlmClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<PlatformCommitElaborationLlmClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _apiKey = configuration["KnowzPlatform:ApiKey"]
            ?? throw new InvalidOperationException("KnowzPlatform:ApiKey is required");
        _logger = logger;
    }

    public bool IsAvailable => true;

    public async Task<string?> ElaborateAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = new CompletionRequest(
                Messages: new[] { new ChatMessage("user", userPrompt) },
                MaxTokens: 400,
                SystemPrompt: systemPrompt);

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ai-services/completion")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(request, SerializeOptions),
                    Encoding.UTF8,
                    "application/json")
            };
            httpRequest.Headers.Add("X-Api-Key", _apiKey);

            var client = _httpClientFactory.CreateClient("KnowzPlatformClient");
            var response = await client.SendAsync(httpRequest, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Platform commit elaboration completion failed with status {Status}",
                    response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var envelope = JsonSerializer.Deserialize<ApiEnvelope<CompletionResponse>>(body, DeserializeOptions);
            if (envelope == null || !envelope.Success || envelope.Data == null)
            {
                _logger.LogWarning(
                    "Platform commit elaboration returned invalid envelope: success={Success}",
                    envelope?.Success);
                return null;
            }

            return envelope.Data.Content;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Platform commit elaboration call failed — commit child will remain as stub");
            return null;
        }
    }

    // Local DTOs — deliberately NOT shared with PlatformAIService so that the shape of
    // commit elaboration payloads can evolve independently.

    private record CompletionRequest(
        ChatMessage[] Messages,
        int? MaxTokens = null,
        string? SystemPrompt = null);

    private record ChatMessage(string Role, string Content);

    private record ApiEnvelope<T>(bool Success, T? Data, List<string>? Errors);

    private record CompletionResponse(string Content, string FinishReason);
}
