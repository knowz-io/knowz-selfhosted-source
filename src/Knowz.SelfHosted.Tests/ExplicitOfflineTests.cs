using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Extensions;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Knowz.SelfHosted.Tests;

public class ExplicitOfflineTests
{
    private static IConfiguration Config(string? disabled) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
        ["KNOWZ_AI_DISABLED"] = disabled,
        ["KnowzPlatform:Enabled"] = "true", ["KnowzPlatform:BaseUrl"] = "https://platform.example", ["KnowzPlatform:ApiKey"] = "retained-key",
        ["OpenAiCompatible:Endpoint"] = "https://native.example", ["OpenAiCompatible:ChatModel"] = "chat", ["OpenAiCompatible:EmbeddingModel"] = "embed",
        ["AzureOpenAI:Endpoint"] = "https://azure.example", ["AzureOpenAI:DeploymentName"] = "chat",
        ["AzureAISearch:Endpoint"] = "https://search.example", ["AzureAISearch:IndexName"] = "knowledge",
        ["AzureAIVision:Endpoint"] = "https://vision.example"
    }).Build();

    [Fact]
    public void ExplicitOffline_WinsOverRetainedProviderConfiguration_ForAllAiRegistrations()
    {
        var config = Config("true");
        var services = new ServiceCollection();
        services.AddSelfHostedOpenAI(config).AddSelfHostedSearch(config).AddAttachmentAI(config);
        Assert.Contains(services, d => d.ServiceType == typeof(IOpenAIService) && d.ImplementationType == typeof(NoOpOpenAIService));
        Assert.Contains(services, d => d.ServiceType == typeof(ISearchService) && d.ImplementationType == typeof(DatabaseSearchService));
        Assert.Contains(services, d => d.ServiceType == typeof(IAttachmentAIProvider) && d.ImplementationType == typeof(NoOpAttachmentAIProvider));
        Assert.Equal("offline", new RunningConfiguration(config).ActiveProvider);
        Assert.Equal("https://native.example", config["OpenAiCompatible:Endpoint"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void OmittedOrClearedOfflineGate_AllowsRetainedProviderToActivate(string? disabled)
    {
        var config = Config(disabled);
        var services = new ServiceCollection();
        services.AddSelfHostedOpenAI(config);
        Assert.Contains(services, d => d.ServiceType == typeof(IOpenAIService) && d.ImplementationType == typeof(PlatformAIService));
        Assert.Equal("KnowzPlatform", new RunningConfiguration(config).ActiveProvider);
    }

    [Theory]
    [InlineData("OpenAiCompatible")]
    [InlineData("AzureOpenAI")]
    [InlineData("KnowzPlatform")]
    [InlineData("AzureAIVision")]
    public async Task ExplicitOffline_ProbesReportDisabled_WithoutNetworkCalls(string category)
    {
        using var client = new HttpClient(new RejectNetwork());
        var result = await ConfigurationProbe.RunAsync(category, Config("true"), client);
        Assert.False(result.IsHealthy);
        Assert.Equal("disabled", result.ProbeStatus);
        Assert.Contains("host", result.Status);
    }

    private sealed class RejectNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new Xunit.Sdk.XunitException("Explicit offline must not contact a provider.");
    }
}
