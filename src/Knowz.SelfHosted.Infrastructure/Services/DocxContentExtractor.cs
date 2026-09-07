using Knowz.Core.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.Extensions.Logging;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Knowz.SelfHosted.Infrastructure.Services;

public class DocxContentExtractor : IFileContentExtractor
{
    internal const int MaxExtractionChars = 10_000_000; // 10M chars
    private readonly ILogger<DocxContentExtractor> _logger;

    public DocxContentExtractor(ILogger<DocxContentExtractor> logger)
    {
        _logger = logger;
    }

    public bool CanExtract(string? contentType)
    {
        return string.Equals(contentType,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            StringComparison.OrdinalIgnoreCase);
    }

    public async Task<FileExtractionResult> ExtractAsync(
        FileRecord fileRecord, Stream fileStream, CancellationToken ct = default)
    {
        if (!CanExtract(fileRecord.ContentType))
            return new FileExtractionResult(false, ErrorMessage: "Unsupported content type");

        try
        {
            // OpenXML requires a seekable stream; buffer if needed
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
                using var document = WordprocessingDocument.Open(readStream, false);
                var body = document.MainDocumentPart?.Document?.Body;
                if (body == null)
                    return new FileExtractionResult(false, ErrorMessage: "DOCX has no document body");

                var paragraphs = body.Descendants<Paragraph>();
                var textParts = new List<string>();
                var totalChars = 0;

                foreach (var para in paragraphs)
                {
                    ct.ThrowIfCancellationRequested();
                    var paraText = para.InnerText?.Trim();
                    if (string.IsNullOrEmpty(paraText))
                        continue;

                    if (totalChars + paraText.Length > MaxExtractionChars)
                    {
                        var remaining = MaxExtractionChars - totalChars;
                        if (remaining > 0)
                            textParts.Add(paraText[..remaining]);

                        _logger.LogWarning(
                            "DOCX {FileRecordId} ({FileName}) exceeded 10M char extraction limit, content truncated",
                            fileRecord.Id, fileRecord.FileName);
                        break;
                    }

                    textParts.Add(paraText);
                    totalChars += paraText.Length;
                }

                if (textParts.Count == 0)
                    return new FileExtractionResult(false, ErrorMessage: "DOCX contains no extractable text");

                var text = string.Join("\n", textParts);
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
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DOCX extraction failed for FileRecord {Id}", fileRecord.Id);
            return new FileExtractionResult(false, ErrorMessage: ex.Message);
        }
    }
}
