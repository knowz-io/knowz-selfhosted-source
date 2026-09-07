using Knowz.Core.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// Routes extraction requests to the first extractor that supports the content type.
/// Registered as IFileContentExtractor in DI, wrapping all individual extractors.
/// </summary>
public class CompositeContentExtractor : IFileContentExtractor
{
    private readonly IReadOnlyList<IFileContentExtractor> _extractors;

    public CompositeContentExtractor(IEnumerable<IFileContentExtractor> extractors)
    {
        _extractors = extractors.ToList();
    }

    public bool CanExtract(string? contentType)
    {
        return _extractors.Any(e => e.CanExtract(contentType));
    }

    public async Task<FileExtractionResult> ExtractAsync(
        FileRecord fileRecord, Stream fileStream, CancellationToken ct = default)
    {
        var extractor = _extractors.FirstOrDefault(e => e.CanExtract(fileRecord.ContentType));
        if (extractor == null)
            return new FileExtractionResult(false, ErrorMessage: "No extractor available for this content type");

        return await extractor.ExtractAsync(fileRecord, fileStream, ct);
    }
}
