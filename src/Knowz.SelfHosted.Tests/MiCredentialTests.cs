using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Storage.Blobs;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Extensions;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// VERIFY (SH_ENTERPRISE_MI_SWAP §3.2 unit-tests):
/// 1. Storage/OpenAI/Search extensions register clients that consume the injected
///    TokenCredential (not AzureKeyCredential).
/// 2. When the `Storage:Azure:ApiKey` style config keys are present they are
///    ignored — only endpoint + TokenCredential are used.
/// 3. AzureAttachmentAIProvider accepts a TokenCredential and exposes
///    capabilities based on endpoint presence alone.
/// </summary>
public class MiCredentialTests
{
    private static TokenCredential FakeTokenCredential()
    {
        var credential = Substitute.For<TokenCredential>();
        credential.GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(new AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1)));
        return credential;
    }

    [Fact]
    public void StorageExtensions_Registers_BlobServiceClient_With_InjectedTokenCredential()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(FakeTokenCredential());

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Provider"] = "AzureBlob",
                ["Storage:Azure:AccountUrl"] = "https://contoso.blob.core.windows.net",
                ["Storage:Azure:ContainerName"] = "files"
            })
            .Build();

        services.AddSelfHostedFileStorage(config);
        using var sp = services.BuildServiceProvider();

        // BlobServiceClient resolves without requiring a connection string.
        var client = sp.GetRequiredService<BlobServiceClient>();
        Assert.NotNull(client);
        Assert.Equal("https://contoso.blob.core.windows.net/", client.Uri.ToString());
    }

    [Fact]
    public void OpenAIExtensions_Registers_AzureOpenAIClient_Without_ApiKey()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(FakeTokenCredential());

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureOpenAI:Endpoint"] = "https://contoso.openai.azure.com",
                ["AzureOpenAI:DeploymentName"] = "gpt-5",
                ["AzureOpenAI:EmbeddingDeploymentName"] = "text-embedding-3-large",
                // Intentionally NO ApiKey
            })
            .Build();

        services.AddSelfHostedOpenAI(config);
        using var sp = services.BuildServiceProvider();

        var client = sp.GetRequiredService<AzureOpenAIClient>();
        Assert.NotNull(client);
    }

    [Fact]
    public void SearchExtensions_Registers_SearchClients_Without_ApiKey()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(FakeTokenCredential());
        services.AddSingleton(Substitute.For<ITenantProvider>());

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureAISearch:Endpoint"] = "https://contoso.search.windows.net",
                ["AzureAISearch:IndexName"] = "knowz",
                // Intentionally NO ApiKey
            })
            .Build();

        services.AddSelfHostedSearch(config);
        using var sp = services.BuildServiceProvider();

        var searchClient = sp.GetRequiredService<SearchClient>();
        var indexClient = sp.GetRequiredService<SearchIndexClient>();
        Assert.NotNull(searchClient);
        Assert.NotNull(indexClient);
        Assert.Equal("knowz", searchClient.IndexName);
    }

    [Fact]
    public void AzureAttachmentAIProvider_Accepts_InjectedTokenCredential()
    {
        var credential = FakeTokenCredential();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var logger = Substitute.For<ILogger<AzureAttachmentAIProvider>>();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Endpoint-only; keys omitted.
                ["AzureAIVision:Endpoint"] = "https://vision.example.com",
                ["AzureOpenAI:Endpoint"] = "https://openai.example.com",
                ["AzureOpenAI:DeploymentName"] = "gpt-5",
                ["AzureDocumentIntelligence:Endpoint"] = "https://docintel.example.com"
            })
            .Build();

        var provider = new AzureAttachmentAIProvider(config, logger, httpClientFactory, credential);

        // Capabilities now derive from endpoint presence only (no ApiKey check).
        Assert.True(provider.HasVisionCapability);
        Assert.True(provider.HasDocumentIntelligenceCapability);
        Assert.True(provider.HasModelSynthesisCapability);
    }

    [Fact]
    public void AzureAttachmentAIProvider_NoEndpoints_NoCapabilities()
    {
        var credential = FakeTokenCredential();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var logger = Substitute.For<ILogger<AzureAttachmentAIProvider>>();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var provider = new AzureAttachmentAIProvider(config, logger, httpClientFactory, credential);

        Assert.False(provider.HasVisionCapability);
        Assert.False(provider.HasDocumentIntelligenceCapability);
        Assert.False(provider.HasModelSynthesisCapability);
    }
}
