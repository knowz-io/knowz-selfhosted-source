namespace Knowz.SelfHosted.Infrastructure.Interfaces;

public interface IAttachmentAIProvider
{
    Task<VisionAnalysisResult> AnalyzeImageAsync(
        byte[] imageBytes, string contentType, CancellationToken ct = default);

    Task<DocumentExtractionResult> ExtractDocumentAsync(
        byte[] documentBytes, string contentType, CancellationToken ct = default);

    string ProviderName { get; }
}

public record VisionAnalysisResult(
    bool Success,
    string? Caption = null,
    string? ExtractedText = null,
    List<string>? Tags = null,
    List<string>? Objects = null,
    string? ErrorMessage = null,
    bool NotAvailable = false);

public record DocumentExtractionResult(
    bool Success,
    string? ExtractedText = null,
    string? LayoutDataJson = null,
    string? ErrorMessage = null,
    bool NotAvailable = false);
