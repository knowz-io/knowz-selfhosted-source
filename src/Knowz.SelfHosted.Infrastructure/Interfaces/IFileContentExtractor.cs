namespace Knowz.SelfHosted.Infrastructure.Interfaces;

public interface IFileContentExtractor
{
    Task<FileExtractionResult> ExtractAsync(
        Knowz.Core.Entities.FileRecord fileRecord,
        Stream fileStream,
        CancellationToken ct = default);

    bool CanExtract(string? contentType);

    /// <summary>
    /// True when this extractor may return <c>Declined: true</c> to hand the file to the NEXT
    /// matching extractor in <see cref="Services.CompositeContentExtractor"/> instead of ending the
    /// chain. Default false — every pre-existing extractor keeps the legacy "first match wins"
    /// semantics and the composite takes a byte-identical single-call path for them.
    /// NodeID: SH_AnydocContentExtractor (R5).
    /// </summary>
    bool MayDecline => false;
}

public record FileExtractionResult(
    bool Success,
    string? ExtractedText = null,
    string? ErrorMessage = null,
    string? VisionDescription = null,
    string? VisionTagsJson = null,
    string? VisionObjectsJson = null,
    string? VisionExtractedText = null,
    // Trailing + defaulted so no existing construction site changes. Only extractors whose
    // MayDecline is true ever set this; the composite then falls through to the next candidate.
    // NodeID: SH_AnydocContentExtractor (R5).
    bool Declined = false);
