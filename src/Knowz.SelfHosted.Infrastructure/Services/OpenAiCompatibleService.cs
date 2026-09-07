using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// Native OpenAI-compatible transport for LM Studio, Ollama, vLLM, OpenAI, and
/// other servers implementing the standard chat-completions and embeddings API.
/// All answer/edit behavior remains in <see cref="AzureOpenAIService"/>; only
/// client construction and configuration names differ.
/// </summary>
public sealed class OpenAiCompatibleService : AzureOpenAIService
{
    internal const string PlaceholderApiKey = "knowz-local-keyless";

    public OpenAiCompatibleService(
        OpenAIClient client,
        IConfiguration configuration,
        ILogger<AzureOpenAIService> logger)
        : base(
            client.GetChatClient(Required(configuration, "OpenAiCompatible:ChatModel")),
            client.GetEmbeddingClient(Required(configuration, "OpenAiCompatible:EmbeddingModel")),
            Required(configuration, "OpenAiCompatible:ChatModel"),
            Required(configuration, "OpenAiCompatible:EmbeddingModel"),
            configuration,
            logger)
    {
    }

    internal static OpenAIClient CreateClient(
        IConfiguration configuration,
        PipelineTransport? transport = null)
    {
        var endpoint = Required(configuration, "OpenAiCompatible:Endpoint");
        var apiKey = configuration["OpenAiCompatible:ApiKey"];
        var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
        if (transport is not null)
            options.Transport = transport;
        return new OpenAIClient(
            new ApiKeyCredential(string.IsNullOrWhiteSpace(apiKey) ? PlaceholderApiKey : apiKey),
            options);
    }

    private static string Required(IConfiguration configuration, string key) =>
        configuration[key] is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"{key} is required");
}
