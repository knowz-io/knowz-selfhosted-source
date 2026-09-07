using Knowz.Core.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;

namespace Knowz.SelfHosted.Infrastructure.Services;

public class PdfContentExtractor : IFileContentExtractor
{
    internal const int MaxExtractionChars = 10_000_000; // 10M chars
    private readonly ILogger<PdfContentExtractor> _logger;

    public PdfContentExtractor(ILogger<PdfContentExtractor> logger)
    {
        _logger = logger;
    }

    public bool CanExtract(string? contentType)
    {
        return string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<FileExtractionResult> ExtractAsync(
        FileRecord fileRecord, Stream fileStream, CancellationToken ct = default)
    {
        if (!CanExtract(fileRecord.ContentType))
            return new FileExtractionResult(false, ErrorMessage: "Unsupported content type");

        try
        {
            // PdfPig requires a seekable stream; buffer if needed
            Stream readStream = fileStream;
            MemoryStream? memoryStream = null;
            if (!fileStream.CanSeek)
            {
                memoryStream = new MemoryStream();
                await fileStream.CopyToAsync(memoryStream, ct);
                memoryStream.Position = 0;
                readStream = memoryStream;
            }

            try
            {
                using var document = PdfDocument.Open(readStream);
                var textParts = new List<string>();
                var totalChars = 0;

                foreach (var page in document.GetPages())
                {
                    ct.ThrowIfCancellationRequested();
                    var pageText = page.Text?.Trim();
                    if (string.IsNullOrEmpty(pageText))
                        continue;

                    if (totalChars + pageText.Length > MaxExtractionChars)
                    {
                        var remaining = MaxExtractionChars - totalChars;
                        if (remaining > 0)
                            textParts.Add(pageText[..remaining]);

                        _logger.LogWarning(
                            "PDF {FileRecordId} ({FileName}) exceeded 10M char extraction limit at page {Page}, content truncated",
                            fileRecord.Id, fileRecord.FileName, page.Number);
                        break;
                    }

                    textParts.Add(pageText);
                    totalChars += pageText.Length;
                }

                if (textParts.Count == 0)
                    return new FileExtractionResult(false,
                        ErrorMessage: "PDF contains no extractable text (may be image-only/scanned)");

                var text = string.Join("\n\n", textParts);
                NativeDocumentExtractionMetadata.ApplySuccess(fileRecord);
                return new FileExtractionResult(true, ExtractedText: text);
            }
            finally
            {
                if (memoryStream != null)
                    await memoryStream.DisposeAsync();
            }
        }
        catch (OperationCanceledException)
        {
            throw; // propagate cancellation
        }
        catch (Exception ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                                || ex.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("PDF {FileRecordId} is password-protected: {Error}", fileRecord.Id, ex.Message);
            return new FileExtractionResult(false,
                ErrorMessage: "PDF is password-protected and cannot be extracted");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PDF extraction failed for FileRecord {Id}", fileRecord.Id);
            return new FileExtractionResult(false, ErrorMessage: ex.Message);
        }
    }
}
