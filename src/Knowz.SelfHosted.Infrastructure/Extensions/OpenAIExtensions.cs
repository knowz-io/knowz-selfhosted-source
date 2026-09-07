using Azure;
using Azure.AI.OpenAI;
using Azure.Core;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Knowz.SelfHosted.Infrastructure.Services.GitCommitHistory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;

namespace Knowz.SelfHosted.Infrastructure.Extensions;

public static class OpenAIExtensions
{
    /// <summary>
    /// Registers AI services with three-tier priority:
    /// 1. KnowzPlatform:Enabled → PlatformAIService + PlatformTextEnrichmentService
    /// 2. AzureOpenAI configured → AzureOpenAIService + TextEnrichmentService
    /// 3. Neither → NoOpOpenAIService + NoOpTextEnrichmentService
    /// </summary>
    public static IServiceCollection AddSelfHostedOpenAI(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (AiRuntimePolicy.IsDisabled(configuration)) return AddNoAi(services);

        // Tier 1: Knowz Platform AI Proxy
        var platformEnabled = string.Equals(
            configuration["KnowzPlatform:Enabled"], "true", StringComparison.OrdinalIgnoreCase);

        if (platformEnabled)
        {
            var baseUrl = configuration["KnowzPlatform:BaseUrl"];
            var platformApiKey = configuration["KnowzPlatform:ApiKey"];

            if (!string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(platformApiKey))
            {
                // Register named HttpClient for platform API calls
                services.AddHttpClient("KnowzPlatformClient", client =>
                {
                    client.BaseAddress = new Uri(baseUrl);
                    client.DefaultRequestHeaders.Add("X-Api-Key", platformApiKey);
                    client.Timeout = TimeSpan.FromSeconds(120);
                });

                services.AddScoped<IOpenAIService, PlatformAIService>();
                services.AddScoped<IContentAmendmentService, PlatformAIService>();
                services.AddScoped<IStreamingOpenAIService, PlatformAIService>();
                services.AddScoped<ITextEnrichmentService, PlatformTextEnrichmentService>();
                // NODE-4: commit-history elaboration uses the platform completion endpoint
                services.AddScoped<ICommitElaborationLlmClient, PlatformCommitElaborationLlmClient>();
                return services;
            }
        }

        // Tier 2: native OpenAI-compatible endpoint (LM Studio/Ollama/vLLM/OpenAI).
        var compatibleEndpoint = configuration["OpenAiCompatible:Endpoint"];
        var compatibleChat = configuration["OpenAiCompatible:ChatModel"];
        var compatibleEmbedding = configuration["OpenAiCompatible:EmbeddingModel"];
        if (!string.IsNullOrWhiteSpace(compatibleEndpoint) &&
            !string.IsNullOrWhiteSpace(compatibleChat) &&
            !string.IsNullOrWhiteSpace(compatibleEmbedding))
        {
            services.AddSingleton<OpenAIClient>(_ => OpenAiCompatibleService.CreateClient(configuration));
            services.AddScoped<IOpenAIService, OpenAiCompatibleService>();
            services.AddScoped<IContentAmendmentService, OpenAiCompatibleService>();
            services.AddScoped<IStreamingOpenAIService, OpenAiCompatibleService>();
            services.AddScoped<ITextEnrichmentService, OpenAiCompatibleTextEnrichmentService>();
            services.AddScoped<ICommitElaborationLlmClient, NoOpCommitElaborationLlmClient>();
            return services;
        }

        // Tier 3: Azure OpenAI. MI-first per SH_ENTERPRISE_MI_SWAP §2.3; however,
        // `AzureOpenAI:ApiKey` takes precedence when present so external-mode
        // deploys (pointing at a shared/third-party OpenAI resource where the
        // UAMI has no data-plane role) can still authenticate.
        var endpoint = configuration["AzureOpenAI:Endpoint"];
        var apiKey = configuration["AzureOpenAI:ApiKey"];

        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            services.AddSingleton(sp =>
            {
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    return new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
                }
                var credential = sp.GetRequiredService<TokenCredential>();
                return new AzureOpenAIClient(new Uri(endpoint), credential);
            });

            services.AddScoped<IOpenAIService, AzureOpenAIService>();
            services.AddScoped<IContentAmendmentService, AzureOpenAIService>();
            services.AddScoped<IStreamingOpenAIService, AzureOpenAIService>();
            services.AddScoped<ITextEnrichmentService, TextEnrichmentService>();
            // NODE-4: Azure OpenAI tier also uses the NoOp commit LLM client for now.
            // Commit-history elaboration is a platform-only feature today; Azure OpenAI
            // direct callers get metadata-only stubs. Debt item: add an Azure-backed impl.
            services.AddScoped<ICommitElaborationLlmClient, NoOpCommitElaborationLlmClient>();
            return services;
        }

        return AddNoAi(services);
    }

    private static IServiceCollection AddNoAi(IServiceCollection services)
    {
        services.AddScoped<IOpenAIService, NoOpOpenAIService>();
        services.AddScoped<IContentAmendmentService, NoOpOpenAIService>();
        services.AddScoped<IStreamingOpenAIService, NoOpOpenAIService>();
        services.AddScoped<ITextEnrichmentService, NoOpTextEnrichmentService>();
        services.AddScoped<ICommitElaborationLlmClient, NoOpCommitElaborationLlmClient>();
        return services;
    }

    /// <summary>
    /// SelfHostedAbstractionSeams: registers <see cref="IEmbeddingService"/> as a thin
    /// adapter over the currently-active <see cref="IOpenAIService"/> tier. Call this AFTER
    /// <see cref="AddSelfHostedOpenAI"/> so the adapter picks up whichever tier (Platform /
    /// Azure / NoOp) was selected. Future ONNX / Ollama providers replace this registration
    /// (see <c>Planned_SelfHostedLocalEmbedding</c>).
    /// </summary>
    public static IServiceCollection AddSelfHostedEmbeddingService(this IServiceCollection services)
    {
        services.AddScoped<IEmbeddingService, EmbeddingServiceAdapter>();
        return services;
    }
}
