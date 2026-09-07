using Knowz.SelfHosted.Infrastructure.Interfaces;

namespace Knowz.SelfHosted.Infrastructure.Services;

public class NoOpAttachmentAIProvider : IAttachmentAIProvider
{
    public string ProviderName => "NoOp";

    public Task<VisionAnalysisResult> AnalyzeImageAsync(
        byte[] imageBytes, string contentType, CancellationToken ct = default)
    {
        return Task.FromResult(new VisionAnalysisResult(
            Success: false,
            ErrorMessage: "Attachment AI is not configured. Set KnowzPlatform or Azure AI Vision/Document Intelligence settings to enable image analysis.",
            NotAvailable: true));
    }

    public Task<DocumentExtractionResult> ExtractDocumentAsync(
        byte[] documentBytes, string contentType, CancellationToken ct = default)
    {
        return Task.FromResult(new DocumentExtractionResult(
            Success: false,
            ErrorMessage: "Attachment AI is not configured. Set KnowzPlatform or Azure Document Intelligence settings to enable document extraction.",
            NotAvailable: true));
    }
}
