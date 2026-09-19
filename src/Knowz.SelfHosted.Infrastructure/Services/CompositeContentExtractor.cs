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

    /// <summary>Ordered extractor chain — visible for the DI-ordering wiring test.</summary>
    internal IReadOnlyList<IFileContentExtractor> Extractors => _extractors;

    public bool CanExtract(string? contentType)
    {
        return _extractors.Any(e => e.CanExtract(contentType));
    }

    public async Task<FileExtractionResult> ExtractAsync(
        FileRecord fileRecord, Stream fileStream, CancellationToken ct = default)
    {
        var candidates = _extractors.Where(e => e.CanExtract(fileRecord.ContentType)).ToList();
        if (candidates.Count == 0)
            return new FileExtractionResult(false, ErrorMessage: "No extractor available for this content type");

        // Byte-identical legacy path: a single candidate, or no candidate BEFORE the last one that
        // may decline, means the first match wins and the ORIGINAL stream is handed over untouched.
        // Cloud extraction opts into decline only for Anydoc:Mode=FallbackOnly; other legacy
        // configurations retain first-match semantics. NodeID: SH_AnydocContentExtractor (R5).
        if (candidates.Count == 1 || !candidates.Take(candidates.Count - 1).Any(e => e.MayDecline))
            return await candidates[0].ExtractAsync(fileRecord, fileStream, ct);

        // Fall-through path: buffer ONCE so each attempt can read the complete content.
        using var buffer = new MemoryStream();
        await fileStream.CopyToAsync(buffer, ct);

        FileExtractionResult result = new(false, ErrorMessage: "No extractor available for this content type");
        for (var i = 0; i < candidates.Count; i++)
        {
            buffer.Position = 0;
            result = await candidates[i].ExtractAsync(fileRecord, buffer, ct);

            // Continue only while this candidate explicitly declined and another one remains.
            if (!result.Declined || i == candidates.Count - 1)
                return result;
        }

        return result;
    }
}
