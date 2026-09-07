using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Identity;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Knowz.SelfHosted.Infrastructure.Extensions;

public static class DocumentIntelligenceExtensions
{
    /// <summary>
    /// Registers the DocumentIntelligenceContentExtractor.
    /// The extractor delegates to IAttachmentAIProvider (registered by AddAttachmentAI).
    /// When no AI provider is configured, CanExtract returns false and
    /// CompositeContentExtractor falls through to native extractors (PdfPig, OpenXML).
    /// </summary>
    public static IServiceCollection AddDocumentIntelligence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // The extractor now uses IAttachmentAIProvider instead of DocumentIntelligenceClient directly.
        // Always register it — CanExtract returns false when provider is NoOp,
        // causing fallthrough to native extractors.
        services.AddScoped<DocumentIntelligenceContentExtractor>();

        return services;
    }
}
