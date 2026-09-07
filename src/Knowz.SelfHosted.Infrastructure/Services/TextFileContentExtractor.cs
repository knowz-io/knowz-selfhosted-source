using System.Text;
using Knowz.Core.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services;

public class TextFileContentExtractor : IFileContentExtractor
{
    internal const int MaxExtractionChars = 10_000_000; // 10M chars

    private static readonly HashSet<string> SupportedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/plain", "text/markdown", "text/csv", "text/html", "text/xml",
        "application/json", "application/xml"
    };

    private readonly ILogger<TextFileContentExtractor> _logger;

    public TextFileContentExtractor(ILogger<TextFileContentExtractor> logger)
    {
        _logger = logger;
    }

    public bool CanExtract(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
            return false;

        return SupportedTypes.Contains(contentType)
            || contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<FileExtractionResult> ExtractAsync(
        FileRecord fileRecord, Stream fileStream, CancellationToken ct = default)
    {
        if (!CanExtract(fileRecord.ContentType))
            return new FileExtractionResult(false, ErrorMessage: "Unsupported content type");

        try
        {
            using var reader = new StreamReader(
                fileStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                leaveOpen: true);

            const int BufferSize = 8192;
            var sb = new StringBuilder();
            var buffer = new char[BufferSize];
            int read;
            while (sb.Length < MaxExtractionChars
                   && (read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                int take = Math.Min(read, MaxExtractionChars - sb.Length);
                sb.Append(buffer, 0, take);
            }

            if (sb.Length == 0)
                return new FileExtractionResult(false, ErrorMessage: "File is empty");

            bool wasTruncated = sb.Length >= MaxExtractionChars;
            if (wasTruncated)
            {
                _logger.LogWarning(
                    "File {FileRecordId} ({FileName}) exceeded 10M char extraction limit, content truncated",
                    fileRecord.Id, fileRecord.FileName);
            }

            return new FileExtractionResult(true, ExtractedText: sb.ToString().Trim());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Text extraction failed for FileRecord {Id}", fileRecord.Id);
            return new FileExtractionResult(false, ErrorMessage: ex.Message);
        }
    }
}
