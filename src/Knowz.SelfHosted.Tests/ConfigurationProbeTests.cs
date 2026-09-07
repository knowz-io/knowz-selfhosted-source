using System.Net;
using Azure.Core;
using Knowz.SelfHosted.Application.Services;
using Microsoft.Extensions.Configuration;

namespace Knowz.SelfHosted.Tests;

public class ConfigurationProbeTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
    private sealed class ManagedCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken ct) => new("managed-test-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken ct) => ValueTask.FromResult(GetToken(requestContext, ct));
    }
    private sealed class CancellableHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return new(HttpStatusCode.OK); }
    }
    private static IConfiguration Native() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
        ["OpenAiCompatible:Endpoint"] = "http://127.0.0.1:11434/v1", ["OpenAiCompatible:ChatModel"] = "chat",
        ["OpenAiCompatible:EmbeddingModel"] = "embed" }).Build();
    [Fact]
    public async Task Should_NotClaimHealthy_WhenEndpointAcceptsUrlButRejectsCredential()
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.Unauthorized) { Content = new StringContent("private error credential") }));
        var result = await ConfigurationProbe.RunAsync("OpenAiCompatible", Native(), client);
        Assert.False(result.IsHealthy); Assert.Equal("unauthorized", result.ProbeStatus);
        Assert.DoesNotContain("private error", result.Status);
    }
    [Fact]
    public async Task Should_ProbeBothModels_WhenNativeProviderIsKeyless()
    {
        var paths = new List<string>();
        using var client = new HttpClient(new Handler(req => {
            paths.Add(req.RequestUri!.AbsolutePath);
            return new(HttpStatusCode.OK) { Content = new StringContent(req.RequestUri.AbsolutePath.EndsWith("embeddings")
                ? "{\"data\":[{\"embedding\":\"zczMPc3MTD4=\",\"index\":0}],\"model\":\"embed\",\"object\":\"list\",\"usage\":{\"prompt_tokens\":1,\"total_tokens\":1}}"
                : "{\"id\":\"probe\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"chat\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}") };
        }));
        var result = await ConfigurationProbe.RunAsync("OpenAiCompatible", Native(), client);
        Assert.True(result.IsHealthy, result.Status + " paths=" + string.Join(",", paths)); Assert.Equal("connected", result.ProbeStatus);
        Assert.Contains("/v1/chat/completions", paths); Assert.Contains("/v1/embeddings", paths);
    }
    [Fact]
    public async Task Should_NotTreatMissingModelAsConnected()
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.NotFound)));
        var result = await ConfigurationProbe.RunAsync("OpenAiCompatible", Native(), client);
        Assert.False(result.IsHealthy); Assert.Equal("unavailable", result.ProbeStatus);
    }
    [Fact]
    public async Task Should_ReportConfigurationOnly_ForAttachmentProvider()
    {
        using var client = new HttpClient(new Handler(_ => throw new Exception("must not probe a customer file")));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["AzureAIVision:Endpoint"] = "https://vision.example" }).Build();
        var result = await ConfigurationProbe.RunAsync("AzureAIVision", config, client);
        Assert.False(result.IsHealthy); Assert.Equal("configuration-valid", result.ProbeStatus);
        Assert.Equal("configuration", result.ProbeKind);
    }

    [Fact]
    public async Task Should_UseManagedIdentity_WhenAzureKeyIsAbsent()
    {
        var tokens = new List<string?>();
        using var client = new HttpClient(new Handler(req => {
            tokens.Add(req.Headers.Authorization?.Parameter);
            return new(HttpStatusCode.OK) { Content = new StringContent(req.RequestUri!.AbsolutePath.Contains("embeddings")
                ? "{\"data\":[{\"embedding\":\"zczMPc3MTD4=\",\"index\":0}],\"model\":\"embed\",\"object\":\"list\",\"usage\":{\"prompt_tokens\":1,\"total_tokens\":1}}"
                : "{\"id\":\"probe\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"chat\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}") };
        }));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["AzureOpenAI:Endpoint"] = "https://azure.example", ["AzureOpenAI:DeploymentName"] = "chat",
            ["AzureOpenAI:EmbeddingDeploymentName"] = "embed" }).Build();
        var result = await ConfigurationProbe.RunAsync("AzureOpenAI", config, client, new ManagedCredential());
        Assert.True(result.IsHealthy);
        Assert.Equal(2, tokens.Count);
        Assert.All(tokens, token => Assert.Equal("managed-test-token", token));
    }
    [Fact]
    public async Task Should_StopProbe_WhenCallerCancels()
    {
        using var client = new HttpClient(new CancellableHandler());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var result = await ConfigurationProbe.RunAsync("OpenAiCompatible", Native(), client, cancellationToken: cancellation.Token);
        Assert.False(result.IsHealthy);
        Assert.Equal("unavailable", result.ProbeStatus);
    }
}
