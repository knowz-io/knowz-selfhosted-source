namespace Knowz.SelfHosted.Infrastructure.Interfaces;

public interface IFileContentExtractor
{
    Task<FileExtractionResult> ExtractAsync(
        Knowz.Core.Entities.FileRecord fileRecord,
        Stream fileStream,
        CancellationToken ct = default);

    bool CanExtract(string? contentType);
}

public record FileExtractionResult(
    bool Success,
    string? ExtractedText = null,
    string? ErrorMessage = null,
    string? VisionDescription = null,
    string? VisionTagsJson = null,
    string? VisionObjectsJson = null,
    string? VisionExtractedText = null);
