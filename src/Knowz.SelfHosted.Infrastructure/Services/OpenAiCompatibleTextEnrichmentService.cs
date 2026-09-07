using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// Text enrichment backed by the same OpenAI-compatible chat model used for
/// local question answering.
/// </summary>
public sealed class OpenAiCompatibleTextEnrichmentService : TextEnrichmentService
{
    public OpenAiCompatibleTextEnrichmentService(
        OpenAIClient client,
        IConfiguration configuration,
        PromptResolutionService promptResolution,
        ILogger<TextEnrichmentService> logger)
        : base(
            client.GetChatClient(Required(configuration, "OpenAiCompatible:ChatModel")),
            Required(configuration, "OpenAiCompatible:ChatModel"),
            promptResolution,
            logger)
    {
    }

    private static string Required(IConfiguration configuration, string key) =>
        configuration[key] is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"{key} is required");
}
