using Azure;
using Azure.Core;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Knowz.SelfHosted.Infrastructure.Extensions;

public static class SearchExtensions
{
    /// <summary>
    /// Registers search services with four-tier priority:
    /// 1. KnowzPlatform:Enabled → PlatformSearchService (proxies to Knowz Platform API)
    /// 2. AzureAISearch configured → AzureSearchService (vector + keyword)
    /// 3. AzureOpenAI configured → LocalVectorSearchService (local vector + keyword)
    /// 4. Fallback → DatabaseSearchService (SQL LIKE keyword search)
    /// </summary>
    public static IServiceCollection AddSelfHostedSearch(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (AiRuntimePolicy.IsDisabled(configuration))
        {
            services.AddScoped<ISearchService, DatabaseSearchService>();
            return services;
        }

        // Tier 1: Knowz Platform mode → proxy search to the platform API
        var platformEnabled = string.Equals(
            configuration["KnowzPlatform:Enabled"], "true", StringComparison.OrdinalIgnoreCase);

        if (platformEnabled)
        {
            services.AddScoped<ISearchService, PlatformSearchService>();
            return services;
        }

        // Tier 2: Azure AI Search. MI-first per SH_ENTERPRISE_MI_SWAP §2.4; however,
        // `AzureAISearch:ApiKey` takes precedence when present so external-mode
        // deploys can still authenticate against a search service outside the
        // UAMI's RBAC scope.
        var endpoint = configuration["AzureAISearch:Endpoint"];
        var indexName = configuration["AzureAISearch:IndexName"];
        var searchApiKey = configuration["AzureAISearch:ApiKey"];

        if (!string.IsNullOrWhiteSpace(endpoint) &&
            !string.IsNullOrWhiteSpace(indexName))
        {
            services.AddSingleton(sp =>
            {
                if (!string.IsNullOrWhiteSpace(searchApiKey))
                {
                    return new SearchClient(new Uri(endpoint), indexName, new AzureKeyCredential(searchApiKey));
                }
                var credential = sp.GetRequiredService<TokenCredential>();
                return new SearchClient(new Uri(endpoint), indexName, credential);
            });

            services.AddSingleton(sp =>
            {
                if (!string.IsNullOrWhiteSpace(searchApiKey))
                {
                    return new SearchIndexClient(new Uri(endpoint), new AzureKeyCredential(searchApiKey));
                }
                var credential = sp.GetRequiredService<TokenCredential>();
                return new SearchIndexClient(new Uri(endpoint), credential);
            });

            services.AddScoped<ISearchService, AzureSearchService>();
            return services;
        }

        // Tier 3: Local vector search (if OpenAI/embedding config present)
        var openAiEndpoint = configuration["AzureOpenAI:Endpoint"];
        var openAiDeployment = configuration["AzureOpenAI:DeploymentName"];
        var compatibleEndpoint = configuration["OpenAiCompatible:Endpoint"];
        var compatibleEmbedding = configuration["OpenAiCompatible:EmbeddingModel"];

        if (!string.IsNullOrWhiteSpace(openAiEndpoint) || !string.IsNullOrWhiteSpace(openAiDeployment) ||
            (!string.IsNullOrWhiteSpace(compatibleEndpoint) && !string.IsNullOrWhiteSpace(compatibleEmbedding)))
        {
            services.AddScoped<ISearchService, LocalVectorSearchService>();
            return services;
        }

        // Tier 4: Database fallback — SQL LIKE keyword search (no vector/semantic)
        services.AddScoped<ISearchService, DatabaseSearchService>();
        return services;
    }

    /// <summary>
    /// SelfHostedAbstractionSeams: registers <see cref="IVectorStore"/> as a thin adapter
    /// over the currently-active <see cref="ISearchService"/> tier. Call this AFTER
    /// <see cref="AddSelfHostedSearch"/>. Future pgvector provider (see
    /// <c>Planned_SelfHostedPgvectorAdapter</c>) replaces this registration with a direct
    /// <see cref="IVectorStore"/> implementation.
    /// </summary>
    public static IServiceCollection AddSelfHostedVectorStore(this IServiceCollection services)
    {
        services.AddScoped<IVectorStore, VectorStoreAdapter>();
        return services;
    }
}
