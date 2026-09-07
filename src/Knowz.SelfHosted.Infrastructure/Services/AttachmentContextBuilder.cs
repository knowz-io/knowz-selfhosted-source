using System.Text;
using System.Text.Json;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Infrastructure.Services;

public enum AttachmentContextRenderMode
{
    Enrichment = 0,
    SearchScopedChat = 1
}

public static class AttachmentContextBuilder
{
    public static async Task<string> BuildKnowledgeAttachmentContextAsync(
        SelfHostedDbContext db,
        Guid knowledgeId,
        AttachmentContextRenderMode mode,
        int maxChars,
        CancellationToken ct)
    {
        var parts = new List<string>();
        var totalChars = 0;

        var knowledgeAttachments = await db.FileAttachments
            .IgnoreQueryFilters()
            .Where(fa => fa.KnowledgeId == knowledgeId)
            .Join(
                db.FileRecords.IgnoreQueryFilters().Where(fr => !fr.IsDeleted),
                fa => fa.FileRecordId,
                fr => fr.Id,
                (fa, fr) => new
                {
                    fr.FileName,
                    fr.ExtractedText,
                    fr.TranscriptionText,
                    fr.ContentType,
                    fa.CreatedAt,
                    fr.VisionDescription,
                    fr.VisionTagsJson,
                    fr.VisionObjectsJson,
                    fr.VisionExtractedText,
                    fr.LayoutDataJson,
                    fr.TextExtractionStatus
                })
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);

        foreach (var attachment in knowledgeAttachments)
        {
            if (totalChars >= maxChars)
                break;

            AddSection(parts, ref totalChars, maxChars, BuildAttachmentSection(
                attachment.FileName,
                attachment.ContentType,
                attachment.ExtractedText,
                attachment.VisionDescription,
                attachment.VisionTagsJson,
                attachment.VisionObjectsJson,
                attachment.VisionExtractedText,
                attachment.LayoutDataJson,
                attachment.TextExtractionStatus,
                isCommentAttachment: false,
                mode: mode));

            AddSection(parts, ref totalChars, maxChars,
                BuildTranscriptSection(attachment.FileName, attachment.TranscriptionText));
        }

        var comments = await db.Comments
            .IgnoreQueryFilters()
            .Where(c => c.KnowledgeId == knowledgeId && !c.IsDeleted)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new { c.Id, c.AuthorName, c.Body })
            .ToListAsync(ct);

        foreach (var comment in comments)
        {
            if (totalChars >= maxChars)
                break;

            if (!string.IsNullOrWhiteSpace(comment.Body))
            {
                AddSection(parts, ref totalChars, maxChars,
                    $"--- Comment by {comment.AuthorName} ---\n{comment.Body}");
            }

            var commentAttachments = await db.FileAttachments
                .IgnoreQueryFilters()
                .Where(fa => fa.CommentId == comment.Id)
                .Join(
                    db.FileRecords.IgnoreQueryFilters().Where(fr => !fr.IsDeleted),
                    fa => fa.FileRecordId,
                    fr => fr.Id,
                    (fa, fr) => new
                    {
                        fr.FileName,
                        fr.ExtractedText,
                        fr.TranscriptionText,
                        fr.ContentType,
                        fr.VisionDescription,
                        fr.VisionTagsJson,
                        fr.VisionObjectsJson,
                        fr.VisionExtractedText,
                        fr.LayoutDataJson,
                        fr.TextExtractionStatus
                    })
                .ToListAsync(ct);

            foreach (var attachment in commentAttachments)
            {
                if (totalChars >= maxChars)
                    break;

                AddSection(parts, ref totalChars, maxChars, BuildAttachmentSection(
                    attachment.FileName,
                    attachment.ContentType,
                    attachment.ExtractedText,
                    attachment.VisionDescription,
                    attachment.VisionTagsJson,
                    attachment.VisionObjectsJson,
                    attachment.VisionExtractedText,
                    attachment.LayoutDataJson,
                    attachment.TextExtractionStatus,
                    isCommentAttachment: true,
                    mode: mode));

                AddSection(parts, ref totalChars, maxChars,
                    BuildTranscriptSection(attachment.FileName, attachment.TranscriptionText));
            }
        }

        return parts.Count > 0 ? string.Join("\n\n", parts) : string.Empty;
    }

    internal static string? BuildAttachmentSection(
        string fileName,
        string? contentType,
        string? extractedText,
        string? visionDescription,
        string? visionTagsJson,
        string? visionObjectsJson,
        string? visionExtractedText,
        string? layoutDataJson,
        int textExtractionStatus,
        bool isCommentAttachment,
        AttachmentContextRenderMode mode)
    {
        var isImage = contentType != null &&
            contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        var hasVisionData = !string.IsNullOrWhiteSpace(visionDescription) ||
            !string.IsNullOrWhiteSpace(visionExtractedText) ||
            !string.IsNullOrWhiteSpace(visionTagsJson) ||
            !string.IsNullOrWhiteSpace(visionObjectsJson);

        var prefix = mode == AttachmentContextRenderMode.Enrichment && isCommentAttachment
            ? "Comment "
            : string.Empty;
        var imageHeader = mode == AttachmentContextRenderMode.SearchScopedChat
            ? $"[Image Analysis: {fileName}]"
            : $"--- {prefix}Image Analysis: {fileName} ---";
        var attachmentHeader = mode == AttachmentContextRenderMode.SearchScopedChat
            ? $"[Attachment: {fileName}]"
            : $"--- {prefix}Attachment: {fileName} ---";

        if (isImage && hasVisionData)
        {
            var lines = new List<string> { imageHeader };

            if (!string.IsNullOrWhiteSpace(visionDescription))
            {
                lines.Add(mode == AttachmentContextRenderMode.Enrichment
                    ? $"Caption: {visionDescription}"
                    : visionDescription);
            }

            var objects = ParseJsonArray(visionObjectsJson);
            if (objects.Count > 0)
                lines.Add($"Objects detected: {string.Join(", ", objects)}");

            var tags = ParseJsonArray(visionTagsJson);
            if (tags.Count > 0)
                lines.Add($"Tags: {string.Join(", ", tags)}");

            if (!string.IsNullOrWhiteSpace(visionExtractedText))
            {
                lines.Add("Text from image:");
                lines.Add(visionExtractedText);
            }

            return string.Join("\n", lines);
        }

        if (!string.IsNullOrWhiteSpace(extractedText) || !string.IsNullOrWhiteSpace(layoutDataJson))
        {
            var lines = new List<string> { attachmentHeader };

            if (!string.IsNullOrWhiteSpace(extractedText))
                lines.Add(extractedText);

            if (!string.IsNullOrWhiteSpace(layoutDataJson))
                lines.Add("Structured layout data available");

            return string.Join("\n", lines);
        }

        if (isImage)
        {
            var marker = $"[Image: {fileName} — analysis unavailable]";
            return mode == AttachmentContextRenderMode.SearchScopedChat
                ? marker
                : $"{attachmentHeader}\n{marker}";
        }

        if (textExtractionStatus == 3)
        {
            var marker = $"[Document: {fileName} — extraction failed]";
            return mode == AttachmentContextRenderMode.SearchScopedChat
                ? marker
                : $"{attachmentHeader}\n{marker}";
        }

        if (mode == AttachmentContextRenderMode.SearchScopedChat)
            return $"[Attachment: {fileName} — content not yet available]";

        return null;
    }

    internal static List<string> ParseJsonArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<string>();

        try
        {
            var document = JsonDocument.Parse(json);
            var results = new List<string>();

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    var value = element.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        results.Add(value);
                }
                else if (element.ValueKind == JsonValueKind.Object &&
                         element.TryGetProperty("name", out var nameProperty))
                {
                    var value = nameProperty.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        results.Add(value);
                }
            }

            return results;
        }
        catch
        {
            return new List<string>();
        }
    }

    private static string? BuildTranscriptSection(string fileName, string? transcriptionText)
    {
        if (string.IsNullOrWhiteSpace(transcriptionText))
            return null;

        var builder = new StringBuilder();
        builder.AppendLine($"[Transcription: {fileName}]");
        builder.AppendLine("[SPOKEN CONTENT BEGIN — verbatim transcript of spoken audio/video]");
        builder.AppendLine("The text between these markers is a verbatim transcript of spoken audio or video.");
        builder.AppendLine("Statements made here reflect what the speaker said, not facts about the attachment itself.");
        builder.AppendLine("Do not treat phrases like \"there is no video preview\" as metadata about the entry.");
        builder.AppendLine(transcriptionText);
        builder.AppendLine("[SPOKEN CONTENT END]");
        return builder.ToString().TrimEnd();
    }

    private static void AddSection(List<string> parts, ref int totalChars, int maxChars, string? section)
    {
        if (string.IsNullOrWhiteSpace(section) || totalChars >= maxChars)
            return;

        var availableChars = maxChars - totalChars;
        if (availableChars <= 0)
            return;

        var normalizedSection = section.Length > availableChars
            ? section[..availableChars]
            : section;

        parts.Add(normalizedSection);
        totalChars += normalizedSection.Length;
    }
}
