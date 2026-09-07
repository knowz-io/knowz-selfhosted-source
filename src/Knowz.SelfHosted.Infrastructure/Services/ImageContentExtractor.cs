using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services;

public class ImageContentExtractor : IFileContentExtractor
{
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp", "image/tiff"
    };

    private readonly IAttachmentAIProvider _attachmentAIProvider;
    private readonly ILogger<ImageContentExtractor> _logger;

    public ImageContentExtractor(
        IAttachmentAIProvider attachmentAIProvider,
        ILogger<ImageContentExtractor> logger)
    {
        _attachmentAIProvider = attachmentAIProvider;
        _logger = logger;
    }

    public bool CanExtract(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
            return false;

        return SupportedTypes.Contains(contentType);
    }

    public async Task<FileExtractionResult> ExtractAsync(
        FileRecord fileRecord, Stream fileStream, CancellationToken ct = default)
    {
        if (!CanExtract(fileRecord.ContentType))
            return new FileExtractionResult(false, ErrorMessage: "Unsupported content type");

        try
        {
            using var ms = new MemoryStream();
            await fileStream.CopyToAsync(ms, ct);
            var imageBytes = ms.ToArray();

            if (imageBytes.Length == 0)
                return new FileExtractionResult(false, ErrorMessage: "Image file is empty");

            var mimeType = fileRecord.ContentType ?? "image/png";
            var result = await _attachmentAIProvider.AnalyzeImageAsync(imageBytes, mimeType, ct);

            if (result.NotAvailable)
            {
                _logger.LogWarning(
                    "Attachment AI provider is not available — image extraction for {FileRecordId} ({FileName}) skipped. {Reason}",
                    fileRecord.Id, fileRecord.FileName, result.ErrorMessage);
                return new FileExtractionResult(false, ErrorMessage: result.ErrorMessage);
            }

            if (!result.Success)
                return new FileExtractionResult(false, ErrorMessage: result.ErrorMessage);

            // Map structured results to FileRecord
            fileRecord.VisionDescription = result.Caption;
            fileRecord.VisionTagsJson = result.Tags != null ? JsonSerializer.Serialize(result.Tags) : null;
            fileRecord.VisionObjectsJson = result.Objects != null ? JsonSerializer.Serialize(result.Objects) : null;
            fileRecord.VisionExtractedText = result.ExtractedText;
            fileRecord.VisionAnalyzedAt = DateTime.UtcNow;
            fileRecord.AttachmentAIProvider = _attachmentAIProvider is AzureAttachmentAIProvider azureProvider
                ? (azureProvider.HasVisionCapability ? "AzureAIVision" : "AzureOpenAI")
                : _attachmentAIProvider.ProviderName;

            // Build combined extracted text for enrichment pipeline compatibility
            var textParts = new List<string>();

            if (!string.IsNullOrWhiteSpace(result.Caption))
                textParts.Add(result.Caption);

            if (!string.IsNullOrWhiteSpace(result.ExtractedText))
                textParts.Add(result.ExtractedText);

            if (result.Tags is { Count: > 0 })
                textParts.Add($"Tags: {string.Join(", ", result.Tags)}");

            if (result.Objects is { Count: > 0 })
                textParts.Add($"Objects: {string.Join(", ", result.Objects)}");

            var extractedText = string.Join("\n\n", textParts);

            if (string.IsNullOrWhiteSpace(extractedText))
                return new FileExtractionResult(false,
                    ErrorMessage: "Vision analysis returned no text");

            return new FileExtractionResult(true,
                ExtractedText: extractedText,
                VisionDescription: result.Caption,
                VisionTagsJson: fileRecord.VisionTagsJson,
                VisionObjectsJson: fileRecord.VisionObjectsJson,
                VisionExtractedText: result.ExtractedText);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Image extraction failed for FileRecord {Id}", fileRecord.Id);
            return new FileExtractionResult(false, ErrorMessage: ex.Message);
        }
    }
}
