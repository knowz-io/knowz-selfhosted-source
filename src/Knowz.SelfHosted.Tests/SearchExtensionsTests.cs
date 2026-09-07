using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Extensions;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Knowz.SelfHosted.Tests;

public class SearchExtensionsTests
{
    [Fact]
    public void AddSelfHostedSearch_RegistersPlatformSearchService_WhenPlatformEnabled()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["KnowzPlatform:Enabled"] = "true"
        });

        var services = new ServiceCollection();
        services.AddSelfHostedSearch(config);

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ISearchService));
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(PlatformSearchService), descriptor.ImplementationType);
    }

    [Fact]
    public void AddSelfHostedSearch_RegistersAzureSearchService_WhenAzureSearchConfigured()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["AzureAISearch:Endpoint"] = "https://search.azure.com",
            ["AzureAISearch:ApiKey"] = "test-key",
            ["AzureAISearch:IndexName"] = "test-index"
        });

        var services = new ServiceCollection();
        services.AddSelfHostedSearch(config);

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ISearchService));
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(AzureSearchService), descriptor.ImplementationType);
    }

    [Fact]
    public void AddSelfHostedSearch_RegistersLocalVectorSearchService_WhenOpenAIConfigured()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["AzureOpenAI:Endpoint"] = "https://openai.azure.com",
            ["AzureOpenAI:DeploymentName"] = "text-embedding-ada-002"
        });

        var services = new ServiceCollection();
        services.AddSelfHostedSearch(config);

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ISearchService));
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(LocalVectorSearchService), descriptor.ImplementationType);
    }

    [Fact]
    public void AddSelfHostedSearch_RegistersDatabaseSearchService_WhenNoAIConfigured()
    {
        var config = BuildConfig(new Dictionary<string, string?>());

        var services = new ServiceCollection();
        services.AddSelfHostedSearch(config);

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ISearchService));
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(DatabaseSearchService), descriptor.ImplementationType);
    }

    [Fact]
    public void AddSelfHostedSearch_PlatformTakesPriority_OverAzureSearch()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["KnowzPlatform:Enabled"] = "true",
            ["AzureAISearch:Endpoint"] = "https://search.azure.com",
            ["AzureAISearch:ApiKey"] = "test-key",
            ["AzureAISearch:IndexName"] = "test-index"
        });

        var services = new ServiceCollection();
        services.AddSelfHostedSearch(config);

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ISearchService));
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(PlatformSearchService), descriptor.ImplementationType);
    }

    [Fact]
    public void AddSelfHostedSearch_AzureSearchTakesPriority_OverLocalVector()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["AzureAISearch:Endpoint"] = "https://search.azure.com",
            ["AzureAISearch:ApiKey"] = "test-key",
            ["AzureAISearch:IndexName"] = "test-index",
            ["AzureOpenAI:Endpoint"] = "https://openai.azure.com",
            ["AzureOpenAI:DeploymentName"] = "text-embedding-ada-002"
        });

        var services = new ServiceCollection();
        services.AddSelfHostedSearch(config);

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ISearchService));
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(AzureSearchService), descriptor.ImplementationType);
    }

    [Fact]
    public void AddSelfHostedSearch_LocalVectorTakesPriority_OverDatabase()
    {
        // Only OpenAI configured (no Azure Search) => tier 3 LocalVectorSearchService
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["AzureOpenAI:Endpoint"] = "https://openai.azure.com"
        });

        var services = new ServiceCollection();
        services.AddSelfHostedSearch(config);

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ISearchService));
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(LocalVectorSearchService), descriptor.ImplementationType);
    }

    [Fact]
    public void AddSelfHostedSearch_DatabaseFallback_WhenOnlyPartialOpenAIConfig()
    {
        // Neither endpoint nor deployment name — should fall to database
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["AzureOpenAI:ApiKey"] = "some-key" // key alone is not enough
        });

        var services = new ServiceCollection();
        services.AddSelfHostedSearch(config);

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ISearchService));
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(DatabaseSearchService), descriptor.ImplementationType);
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
