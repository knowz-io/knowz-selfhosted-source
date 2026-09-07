using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Knowz.Core.Interfaces;
using Knowz.Core.Models;
using Knowz.SelfHosted.Infrastructure.Extensions;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Knowz.SelfHosted.Tests;

public class OpenAiCompatibleRegistrationTests
{
    [Fact]
    public void AddSelfHostedOpenAI_RegistersCompatibleService_WhenConfigured()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenAiCompatible:Endpoint"] = "http://host.docker.internal:1234/v1",
            ["OpenAiCompatible:ApiKey"] = "local-key",
            ["OpenAiCompatible:ChatModel"] = "local-chat",
            ["OpenAiCompatible:EmbeddingModel"] = "local-embed",
            ["OpenAiCompatible:EmbeddingDimensions"] = "768",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSelfHostedOpenAI(config);

        Assert.Contains(services, d => d.ServiceType == typeof(IOpenAIService)
            && d.ImplementationType == typeof(OpenAiCompatibleService));
        Assert.Contains(services, d => d.ServiceType == typeof(IContentAmendmentService)
            && d.ImplementationType == typeof(OpenAiCompatibleService));
        Assert.Contains(services, d => d.ServiceType == typeof(IStreamingOpenAIService)
            && d.ImplementationType == typeof(OpenAiCompatibleService));
        Assert.Contains(services, d => d.ServiceType == typeof(ITextEnrichmentService)
            && d.ImplementationType?.Name == "OpenAiCompatibleTextEnrichmentService");
    }

    [Fact]
    public void AddSelfHostedSearch_RegistersLocalVectorSearch_ForCompatibleProvider()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenAiCompatible:Endpoint"] = "http://host.docker.internal:1234/v1",
            ["OpenAiCompatible:EmbeddingModel"] = "local-embed",
        }).Build();
        var services = new ServiceCollection();
        services.AddSelfHostedSearch(config);

        Assert.Contains(services, d => d.ServiceType == typeof(ISearchService)
            && d.ImplementationType == typeof(LocalVectorSearchService));
    }

    [Fact]
    public async Task CompatibleService_UsesConfiguredRoutesModelsAndBearerKey()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenAiCompatible:Endpoint"] = "http://127.0.0.1:44123/v1",
            ["OpenAiCompatible:ApiKey"] = "runtime-test-key",
            ["OpenAiCompatible:ChatModel"] = "runtime-chat",
            ["OpenAiCompatible:EmbeddingModel"] = "runtime-embed",
        }).Build();
        var handler = new CaptureHandler();
        var transport = new HttpClientPipelineTransport(new HttpClient(handler));
        var client = OpenAiCompatibleService.CreateClient(config, transport);
        var service = new OpenAiCompatibleService(
            client, config, NullLogger<AzureOpenAIService>.Instance);

        var answer = await service.AnswerQuestionAsync(
            "What was tested?",
            [new SearchResultItem
            {
                KnowledgeId = Guid.NewGuid(),
                Title = "Smoke",
                Content = "Native runtime",
                Score = 1,
            }]);
        var embedding = await service.GenerateEmbeddingAsync("Native runtime");

        Assert.Equal("runtime answer", answer.Answer);
        Assert.NotNull(embedding);
        Assert.Equal([0.1f, 0.2f, 0.3f], embedding);
        Assert.Collection(handler.Requests,
            request => AssertRequest(request, "/v1/chat/completions", "runtime-chat"),
            request => AssertRequest(request, "/v1/embeddings", "runtime-embed"));
    }

    private static void AssertRequest(CapturedRequest request, string path, string model)
    {
        Assert.Equal(path, request.Uri.AbsolutePath);
        Assert.Equal("Bearer", request.AuthScheme);
        Assert.Equal("runtime-test-key", request.AuthParameter);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(model, body.RootElement.GetProperty("model").GetString());
    }

    private sealed record CapturedRequest(
        Uri Uri, string? AuthScheme, string? AuthParameter, string Body);

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.RequestUri!,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                body));

            var embeddingBytes = new byte[3 * sizeof(float)];
            Buffer.BlockCopy(new[] { 0.1f, 0.2f, 0.3f }, 0, embeddingBytes, 0, embeddingBytes.Length);
            var encodedEmbedding = Convert.ToBase64String(embeddingBytes);
            var json = request.RequestUri!.AbsolutePath.EndsWith("/embeddings", StringComparison.Ordinal)
                ? """{"object":"list","data":[{"object":"embedding","embedding":"ENCODED","index":0}],"model":"runtime-embed","usage":{"prompt_tokens":1,"total_tokens":1}}"""
                    .Replace("ENCODED", encodedEmbedding, StringComparison.Ordinal)
                : """{"id":"chatcmpl-test","object":"chat.completion","created":1,"model":"runtime-chat","choices":[{"index":0,"message":{"role":"assistant","content":"runtime answer"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
