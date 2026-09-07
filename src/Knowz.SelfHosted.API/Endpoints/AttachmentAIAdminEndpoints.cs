using System.Linq.Expressions;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.API.Endpoints;

public static class AttachmentAIAdminEndpoints
{
    public static void MapAttachmentAIAdminEndpoints(this WebApplication app)
    {
        // POST /api/admin/files/reprocess
        app.MapPost("/api/admin/files/reprocess", async (
            HttpContext context,
            SelfHostedDbContext db,
            IFileStorageProvider storageProvider,
            ILogger<Marker> logger,
            ReprocessRequest? req,
            IFileContentExtractor? contentExtractor = null,
            IEnrichmentOutboxWriter? enrichmentWriter = null,
            CancellationToken ct = default) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden();

            if (contentExtractor == null)
                return Results.BadRequest(new { error = "Content extraction is not configured." });

            var contentTypeFilter = req?.ContentTypeFilter;
            var onlyMissing = req?.OnlyMissing ?? false;
            var maxCount = Math.Clamp(req?.MaxCount ?? 100, 1, 1000);
            // FileIds filter (optional, overrides content-type + onlyMissing when present).
            var fileIds = req?.FileIds?.Where(id => id != Guid.Empty).Distinct().ToArray();

            var query = db.FileRecords.Where(f => !f.IsDeleted);

            if (fileIds != null && fileIds.Length > 0)
            {
                query = query.Where(f => fileIds.Contains(f.Id));
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(contentTypeFilter))
                    query = query.Where(f => f.ContentType != null && f.ContentType.StartsWith(contentTypeFilter));

                if (onlyMissing)
                    query = query.Where(BuildOnlyMissingPredicate(contentTypeFilter));
            }

            var files = await query
                .OrderBy(f => f.CreatedAt)
                .Take(maxCount)
                .ToListAsync(ct);

            int queued = 0, skipped = 0, errors = 0;
            var errorDetails = new List<object>();

            foreach (var file in files)
            {
                if (!contentExtractor.CanExtract(file.ContentType))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    var download = await storageProvider.DownloadAsync(file.TenantId, file.Id, ct);
                    using var stream = download.stream;

                    var extraction = await contentExtractor.ExtractAsync(file, stream, ct);
                    if (extraction.Success)
                    {
                        if (file.ExtractedText == null && extraction.ExtractedText != null)
                            file.ExtractedText = extraction.ExtractedText;
                        file.UpdatedAt = DateTime.UtcNow;

                        // Resolve both knowledge-direct and comment-scoped parents so
                        // re-enrichment fires for attachments added via the comment flow.
                        if (enrichmentWriter != null)
                        {
                            var knowledgeIds = await db.FileAttachments
                                .Where(fa => fa.FileRecordId == file.Id && fa.KnowledgeId != null)
                                .Select(fa => fa.KnowledgeId!.Value)
                                .ToListAsync(ct);

                            var commentScopedKnowledgeIds = await db.FileAttachments
                                .Where(fa => fa.FileRecordId == file.Id && fa.CommentId != null)
                                .Join(db.Comments, fa => fa.CommentId, c => c.Id, (fa, c) => c.KnowledgeId)
                                .ToListAsync(ct);

                            foreach (var kid in knowledgeIds.Concat(commentScopedKnowledgeIds).Distinct())
                                await enrichmentWriter.EnqueueAsync(kid, file.TenantId, ct);
                        }

                        queued++;
                    }
                    else
                    {
                        errors++;
                        errorDetails.Add(new { fileId = file.Id, fileName = file.FileName, error = extraction.ErrorMessage });
                        logger.LogWarning(
                            "Reprocess extraction returned non-success for FileRecord {FileId} ({FileName}): {Reason}",
                            file.Id, file.FileName, extraction.ErrorMessage);
                    }
                }
                catch (Exception ex)
                {
                    errors++;
                    errorDetails.Add(new { fileId = file.Id, fileName = file.FileName, error = ex.GetType().Name + ": " + ex.Message });
                    logger.LogWarning(ex,
                        "Reprocess failed for FileRecord {FileId} ({FileName})",
                        file.Id, file.FileName);
                }
            }

            await db.SaveChangesAsync(ct);

            return Results.Ok(new { queued, skipped, errors, errorDetails });
        })
        .WithTags("Administration")
        .Produces<object>()
        .Produces(403);

        // GET /api/admin/attachment-ai/status
        app.MapGet("/api/admin/attachment-ai/status", async (
            HttpContext context,
            SelfHostedDbContext db,
            IConfiguration configuration,
            IAttachmentAIProvider? aiProvider = null,
            CancellationToken ct = default) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden();

            var providerName = aiProvider?.ProviderName ?? "None";
            var visionConfigured =
                !string.IsNullOrWhiteSpace(configuration["AzureAIVision:Endpoint"]) &&
                !string.IsNullOrWhiteSpace(configuration["AzureAIVision:ApiKey"]);
            var documentConfigured =
                !string.IsNullOrWhiteSpace(configuration["AzureDocumentIntelligence:Endpoint"]) &&
                !string.IsNullOrWhiteSpace(configuration["AzureDocumentIntelligence:ApiKey"]);
            var synthesisConfigured =
                !string.IsNullOrWhiteSpace(configuration["AzureOpenAI:Endpoint"]) &&
                !string.IsNullOrWhiteSpace(configuration["AzureOpenAI:ApiKey"]) &&
                !string.IsNullOrWhiteSpace(configuration["AzureOpenAI:DeploymentName"]);
            var warnings = new List<string>();

            if (!visionConfigured)
                warnings.Add("Azure AI Vision is not configured. Structured image OCR, tags, and object detection are unavailable.");
            if (!documentConfigured)
                warnings.Add("Azure Document Intelligence is not configured. Direct Azure document extraction will fall back to native extractors where available.");
            if (!synthesisConfigured)
                warnings.Add("Azure OpenAI / Foundry synthesis is not configured. Diagram-aware semantic descriptions will be limited.");

            var total = await db.FileRecords.CountAsync(f => !f.IsDeleted, ct);
            var withVisionData = await db.FileRecords.CountAsync(f => !f.IsDeleted && f.VisionAnalyzedAt != null, ct);
            var withExtractedText = await db.FileRecords.CountAsync(f => !f.IsDeleted && f.TextExtractionStatus == (int)TextExtractionStatus.Completed, ct);
            var extractionNotStarted = await db.FileRecords.CountAsync(f => !f.IsDeleted && f.TextExtractionStatus == (int)TextExtractionStatus.NotStarted, ct);
            var extractionFailed = await db.FileRecords.CountAsync(f => !f.IsDeleted && f.TextExtractionStatus == (int)TextExtractionStatus.Failed, ct);
            var imageFiles = await db.FileRecords.CountAsync(f => !f.IsDeleted && f.ContentType != null && f.ContentType.StartsWith("image/"), ct);
            var documentFiles = await db.FileRecords.CountAsync(BuildDocumentFilePredicate(includeNotDeleted: true), ct);

            return Results.Ok(new
            {
                provider = providerName,
                capabilities = new
                {
                    visionConfigured,
                    documentIntelligenceConfigured = documentConfigured,
                    modelSynthesisConfigured = synthesisConfigured,
                    operatingMode = providerName == "NoOp" ? "Degraded" : "DirectAzure"
                },
                warnings,
                fileStats = new
                {
                    total,
                    imageFiles,
                    documentFiles,
                    withVisionData,
                    withExtractedText,
                    extractionNotStarted,
                    extractionFailed
                }
            });
        })
        .WithTags("Administration")
        .Produces<object>()
        .Produces(403);
    }

    public static Expression<Func<FileRecord, bool>> BuildOnlyMissingPredicate(string? contentTypeFilter)
    {
        var completed = (int)TextExtractionStatus.Completed;

        if (!string.IsNullOrWhiteSpace(contentTypeFilter) &&
            contentTypeFilter.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return f => f.VisionAnalyzedAt == null;
        }

        if (!string.IsNullOrWhiteSpace(contentTypeFilter))
        {
            return f => f.TextExtractedAt == null || f.TextExtractionStatus != completed;
        }

        return f =>
            (f.ContentType != null &&
             f.ContentType.StartsWith("image/") &&
             f.VisionAnalyzedAt == null) ||
            ((f.ContentType == "application/pdf" ||
              f.ContentType == "application/vnd.openxmlformats-officedocument.wordprocessingml.document" ||
              f.ContentType == "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" ||
              f.ContentType == "application/vnd.openxmlformats-officedocument.presentationml.presentation") &&
             (f.TextExtractedAt == null || f.TextExtractionStatus != completed));
    }

    private static Expression<Func<FileRecord, bool>> BuildDocumentFilePredicate(bool includeNotDeleted)
    {
        return f =>
            (!includeNotDeleted || !f.IsDeleted) &&
            (f.ContentType == "application/pdf" ||
             f.ContentType == "application/vnd.openxmlformats-officedocument.wordprocessingml.document" ||
             f.ContentType == "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" ||
             f.ContentType == "application/vnd.openxmlformats-officedocument.presentationml.presentation");
    }
}

public record ReprocessRequest(
    string? ContentTypeFilter = null,
    bool OnlyMissing = false,
    int MaxCount = 100,
    Guid[]? FileIds = null);

// Marker class for ILogger<> category (kept internal — logger category reads as
// "Knowz.SelfHosted.API.Endpoints.AttachmentAIAdminEndpoints+Marker").
internal sealed class Marker { }
