using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Knowz.SelfHosted.Infrastructure.Extensions;

public static class AttachmentAIExtensions
{
    /// <summary>
    /// Registers IAttachmentAIProvider for the direct-Azure self-hosted attachment pipeline:
    /// 1. Direct Azure when any attachment AI capability is configured
    /// 2. NoOp when attachment AI is unavailable
    ///
    /// Attachment intelligence no longer falls back to Knowz Platform proxy endpoints.
    /// </summary>
    public static IServiceCollection AddAttachmentAI(
        this IServiceCollection services, IConfiguration configuration)
    {
        if (!AiRuntimePolicy.IsDisabled(configuration) && HasAnyAttachmentAzureCapability(configuration))
        {
            services.AddScoped<IAttachmentAIProvider, AzureAttachmentAIProvider>();
            return services;
        }

        services.AddScoped<IAttachmentAIProvider, NoOpAttachmentAIProvider>();
        return services;
    }

    // MI swap SH_ENTERPRISE_MI_SWAP §2.5: capability detection now checks endpoint
    // presence only. TokenCredential is injected at resolution time; auth is validated
    // on first Azure SDK call (401 surfaces as a log line from the SDK, not here).
    private static bool HasAnyAttachmentAzureCapability(IConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration["AzureAIVision:Endpoint"]))
            return true;

        if (!string.IsNullOrWhiteSpace(configuration["AzureOpenAI:Endpoint"]) &&
            !string.IsNullOrWhiteSpace(configuration["AzureOpenAI:DeploymentName"]))
            return true;

        if (!string.IsNullOrWhiteSpace(configuration["AzureDocumentIntelligence:Endpoint"]))
            return true;

        return false;
    }
}
