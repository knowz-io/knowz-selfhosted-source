using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services;

public class DocumentIntelligenceContentExtractor : IFileContentExtractor
{
    // Only document types — image/* routes to ImageContentExtractor for vision analysis.
    // Azure Document Intelligence also supports image/jpeg, image/png, image/tiff, image/bmp
    // but for self-hosted parity we want images to go through the vision pipeline (caption,
    // tags, objects, OCR) rather than document extraction (text-only).
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation"
    };

    private readonly IAttachmentAIProvider _provider;
    private readonly ILogger<DocumentIntelligenceContentExtractor> _logger;

    public DocumentIntelligenceContentExtractor(
        IAttachmentAIProvider provider,
        ILogger<DocumentIntelligenceContentExtractor> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    public bool CanExtract(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
            return false;

        // When provider is NoOp, or the direct Azure provider lacks document capability,
        // return false so CompositeContentExtractor falls through to native extractors.
        if (_provider is NoOpAttachmentAIProvider)
            return false;

        if (_provider is AzureAttachmentAIProvider azureProvider &&
            !azureProvider.HasDocumentIntelligenceCapability)
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
            // Buffer stream into byte array for the provider
            using var ms = new MemoryStream();
            await fileStream.CopyToAsync(ms, ct);
            var documentBytes = ms.ToArray();

            if (documentBytes.Length == 0)
                return new FileExtractionResult(false, ErrorMessage: "Document file is empty");

            // Set status to Processing
            fileRecord.TextExtractionStatus = (int)TextExtractionStatus.Processing;

            var mimeType = fileRecord.ContentType ?? "application/pdf";
            var result = await _provider.ExtractDocumentAsync(documentBytes, mimeType, ct);

            if (result.NotAvailable)
            {
                _logger.LogWarning(
                    "Attachment AI provider is not available — document extraction for {FileRecordId} ({FileName}) skipped. {Reason}",
                    fileRecord.Id, fileRecord.FileName, result.ErrorMessage);
                fileRecord.TextExtractionStatus = (int)TextExtractionStatus.Failed;
                fileRecord.TextExtractionError = result.ErrorMessage;
                return new FileExtractionResult(false, ErrorMessage: result.ErrorMessage);
            }

            if (!result.Success)
            {
                fileRecord.TextExtractionStatus = (int)TextExtractionStatus.Failed;
                fileRecord.TextExtractionError = result.ErrorMessage;
                return new FileExtractionResult(false, ErrorMessage: result.ErrorMessage);
            }

            // Success — populate FileRecord fields
            fileRecord.ExtractedText = result.ExtractedText;
            fileRecord.LayoutDataJson = result.LayoutDataJson;
            fileRecord.TextExtractedAt = DateTime.UtcNow;
            fileRecord.TextExtractionStatus = (int)TextExtractionStatus.Completed;
            fileRecord.TextExtractionError = null;
            fileRecord.AttachmentAIProvider = _provider is AzureAttachmentAIProvider
                ? "AzureDocumentIntelligence"
                : _provider.ProviderName;

            return new FileExtractionResult(true, ExtractedText: result.ExtractedText);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Document extraction failed for FileRecord {Id}", fileRecord.Id);
            fileRecord.TextExtractionStatus = (int)TextExtractionStatus.Failed;
            fileRecord.TextExtractionError = ex.Message;
            return new FileExtractionResult(false, ErrorMessage: ex.Message);
        }
    }
}
