using Knowz.Core.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.Extensions.Logging;
using DocumentFormat.OpenXml.Packaging;
using D = DocumentFormat.OpenXml.Drawing;

namespace Knowz.SelfHosted.Infrastructure.Services;

public class PowerPointContentExtractor : IFileContentExtractor
{
    internal const int MaxExtractionChars = 10_000_000; // 10M chars

    private static readonly HashSet<string> SupportedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "application/vnd.ms-powerpoint"
    };

    private const string PptxMimeType =
        "application/vnd.openxmlformats-officedocument.presentationml.presentation";
    private const string PptMimeType = "application/vnd.ms-powerpoint";

    private readonly ILogger<PowerPointContentExtractor> _logger;

    public PowerPointContentExtractor(ILogger<PowerPointContentExtractor> logger)
    {
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

        // Legacy PPT format cannot be read by OpenXml — requires Document Intelligence fallback
        if (string.Equals(fileRecord.ContentType, PptMimeType, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Legacy PPT format for {FileRecordId} ({FileName}) requires Document Intelligence fallback",
                fileRecord.Id, fileRecord.FileName);
            return new FileExtractionResult(false,
                ErrorMessage: "Legacy PPT format is not supported by native extraction; Document Intelligence fallback required");
        }

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
                using var document = PresentationDocument.Open(readStream, false);
                var presentationPart = document.PresentationPart;
                var slideIdList = presentationPart?.Presentation?.SlideIdList;
                if (slideIdList == null)
                    return new FileExtractionResult(false,
                        ErrorMessage: "PowerPoint file has no slides");

                var slideIds = slideIdList.Elements<DocumentFormat.OpenXml.Presentation.SlideId>().ToList();
                var textParts = new List<string>();
                var totalChars = 0;
                var truncated = false;
                var slideNumber = 0;

                foreach (var slideId in slideIds)
                {
                    ct.ThrowIfCancellationRequested();

                    if (truncated) break;

                    slideNumber++;
                    var relationshipId = slideId.RelationshipId?.Value;
                    if (string.IsNullOrEmpty(relationshipId))
                        continue;

                    if (presentationPart!.GetPartById(relationshipId) is not SlidePart slidePart)
                        continue;

                    // Add slide marker (account for \n separator between parts)
                    var marker = $"--- Slide {slideNumber} ---";
                    var markerCost = marker.Length + (textParts.Count > 0 ? 1 : 0);
                    if (totalChars + markerCost > MaxExtractionChars)
                    {
                        truncated = true;
                        break;
                    }
                    textParts.Add(marker);
                    totalChars += markerCost;

                    // Extract text from shapes
                    var shapeTexts = ExtractTextFromSlide(slidePart);
                    foreach (var shapeText in shapeTexts)
                    {
                        if (truncated) break;

                        var shapeCost = shapeText.Length + 1; // +1 for \n separator
                        if (totalChars + shapeCost > MaxExtractionChars)
                        {
                            var remaining = MaxExtractionChars - totalChars - 1;
                            if (remaining > 0)
                                textParts.Add(shapeText[..remaining]);

                            _logger.LogWarning(
                                "PowerPoint {FileRecordId} ({FileName}) exceeded 10M char extraction limit at slide {Slide}, content truncated",
                                fileRecord.Id, fileRecord.FileName, slideNumber);
                            truncated = true;
                            break;
                        }

                        textParts.Add(shapeText);
                        totalChars += shapeCost;
                    }

                    // Extract speaker notes
                    var notes = ExtractSpeakerNotes(slidePart);
                    if (!string.IsNullOrEmpty(notes) && !truncated)
                    {
                        var notesHeader = "[Speaker Notes]";
                        var notesBlock = $"{notesHeader}\n{notes}";
                        var notesCost = notesBlock.Length + 1; // +1 for \n separator

                        if (totalChars + notesCost > MaxExtractionChars)
                        {
                            var remaining = MaxExtractionChars - totalChars - 1;
                            if (remaining > 0)
                                textParts.Add(notesBlock[..remaining]);
                            truncated = true;
                        }
                        else
                        {
                            textParts.Add(notesBlock);
                            totalChars += notesCost;
                        }
                    }
                }

                if (textParts.Count == 0)
                    return new FileExtractionResult(false,
                        ErrorMessage: "PowerPoint file contains no extractable text");

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
            _logger.LogWarning(ex, "PowerPoint extraction failed for FileRecord {Id}", fileRecord.Id);
            return new FileExtractionResult(false, ErrorMessage: ex.Message);
        }
    }

    private static List<string> ExtractTextFromSlide(SlidePart slidePart)
    {
        var texts = new List<string>();
        var slide = slidePart.Slide;
        if (slide?.CommonSlideData?.ShapeTree == null)
            return texts;

        foreach (var shape in slide.CommonSlideData.ShapeTree
            .Descendants<DocumentFormat.OpenXml.Presentation.Shape>())
        {
            var textBody = shape.TextBody;
            if (textBody == null)
                continue;

            foreach (var paragraph in textBody.Elements<D.Paragraph>())
            {
                var paraText = string.Concat(
                    paragraph.Elements<D.Run>().Select(r => r.Text?.Text ?? ""));
                if (!string.IsNullOrWhiteSpace(paraText))
                    texts.Add(paraText.Trim());
            }
        }

        return texts;
    }

    private static string? ExtractSpeakerNotes(SlidePart slidePart)
    {
        var notesSlidePart = slidePart.NotesSlidePart;
        if (notesSlidePart?.NotesSlide?.CommonSlideData?.ShapeTree == null)
            return null;

        var texts = new List<string>();
        foreach (var shape in notesSlidePart.NotesSlide.CommonSlideData.ShapeTree
            .Descendants<DocumentFormat.OpenXml.Presentation.Shape>())
        {
            var textBody = shape.TextBody;
            if (textBody == null)
                continue;

            foreach (var paragraph in textBody.Elements<D.Paragraph>())
            {
                var paraText = string.Concat(
                    paragraph.Elements<D.Run>().Select(r => r.Text?.Text ?? ""));
                if (!string.IsNullOrWhiteSpace(paraText))
                    texts.Add(paraText.Trim());
            }
        }

        return texts.Count > 0 ? string.Join("\n", texts) : null;
    }
}
