using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;

namespace Knowz.SelfHosted.API.Endpoints;

public static class FileEndpoints
{
    public static void MapFileEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/files").WithTags("Files");

        // POST /api/files/upload -- multipart file upload
        // Ceiling: 500 MB. Matches Kestrel/Form limits in Program.cs and the
        // 500M client_max_body_size on the web container's nginx config.
        const long MaxUploadBytes = 500L * 1024 * 1024;

        group.MapPost("/upload", async (
            FileStorageService svc,
            IFormFile file,
            CancellationToken ct) =>
        {
            if (file == null || file.Length == 0)
                return Results.BadRequest(new { error = "No file uploaded" });

            if (file.Length > MaxUploadBytes)
                return Results.BadRequest(new { error = "File size exceeds 500MB limit" });

            using var stream = file.OpenReadStream();
            var result = await svc.UploadAsync(
                stream,
                file.FileName,
                file.ContentType ?? "application/octet-stream",
                ct);

            return Results.Created($"/api/v1/files/{result.FileRecordId}", result);
        })
        .Produces<FileUploadResult>(201)
        .Produces(400)
        .DisableAntiforgery();

        // GET /api/files -- paginated list with search/filter
        group.MapGet("/", async (
            FileStorageService svc,
            int page = 1,
            int pageSize = 20,
            string? search = null,
            string? contentTypeFilter = null,
            CancellationToken ct = default) =>
        {
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 20;
            if (pageSize > 100) pageSize = 100;

            var result = await svc.ListAsync(page, pageSize, search, contentTypeFilter, ct);
            return Results.Ok(result);
        })
        .Produces<FileListResponse>();

        // GET /api/files/{id} -- get file metadata (no binary content)
        group.MapGet("/{id:guid}", async (
            FileStorageService svc, Guid id, CancellationToken ct) =>
        {
            var result = await svc.GetMetadataAsync(id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        })
        .Produces<FileMetadataDto>()
        .Produces(404);

        // GET /api/files/{id}/download -- stream file content
        group.MapGet("/{id:guid}/download", async (
            FileStorageService svc, Guid id, CancellationToken ct) =>
        {
            var result = await svc.DownloadAsync(id, ct);
            if (result is null)
                return Results.NotFound();

            var (stream, contentType, fileName) = result.Value;
            return Results.File(stream, contentType, fileName);
        })
        .Produces(200)
        .Produces(404);

        // GET /api/files/{id}/download-url -- get time-limited download URL
        group.MapGet("/{id:guid}/download-url", async (
            FileStorageService svc, Guid id, int? expiryMinutes = null, CancellationToken ct = default) =>
        {
            try
            {
                var expiry = expiryMinutes.HasValue
                    ? TimeSpan.FromMinutes(expiryMinutes.Value)
                    : (TimeSpan?)null;

                var url = await svc.GenerateDownloadUrlAsync(id, expiry, ct);
                return Results.Ok(new { url });
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound();
            }
        })
        .Produces<object>(200)
        .Produces(404);

        // DELETE /api/files/{id} -- soft-delete file
        group.MapDelete("/{id:guid}", async (
            FileStorageService svc, Guid id, CancellationToken ct) =>
        {
            var result = await svc.DeleteAsync(id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        })
        .Produces<DeleteResult>()
        .Produces(404);

        // Knowledge attachment endpoints
        var knowledgeGroup = app.MapGroup("/api/v1/knowledge/{knowledgeId:guid}/attachments")
            .WithTags("Knowledge", "Files");

        // POST /api/knowledge/{knowledgeId}/attachments -- attach file to knowledge
        knowledgeGroup.MapPost("/", async (
            FileStorageService svc,
            Guid knowledgeId,
            AttachFileRequest req,
            CancellationToken ct) =>
        {
            if (req.FileRecordId == Guid.Empty)
                return Results.BadRequest(new { error = "fileRecordId is required" });

            try
            {
                var result = await svc.AttachToKnowledgeAsync(req.FileRecordId, knowledgeId, ct);
                return Results.Created($"/api/v1/knowledge/{knowledgeId}/attachments/{result.Id}", result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .Produces<FileAttachmentDto>(201)
        .Produces(400);

        // GET /api/knowledge/{knowledgeId}/attachments -- list attached files
        knowledgeGroup.MapGet("/", async (
            FileStorageService svc, Guid knowledgeId, CancellationToken ct) =>
        {
            var result = await svc.GetAttachmentsForKnowledgeAsync(knowledgeId, ct);
            return Results.Ok(result);
        })
        .Produces<List<FileMetadataDto>>();

        // DELETE /api/knowledge/{knowledgeId}/attachments/{fileRecordId} -- detach file.
        // Optional ?deleteFiles=true|false honors the configured FileCleanup:Mode (R2).
        // Absent under the default PromptUser mode => preserve (fail-safe). Self-hosted deletes
        // blobs IMMEDIATELY when the effective flag is true and no other entity references the file.
        knowledgeGroup.MapDelete("/{fileRecordId:guid}", async (
            FileStorageService svc, Guid knowledgeId, Guid fileRecordId,
            bool? deleteFiles, CancellationToken ct) =>
        {
            var result = await svc.DetachFromKnowledgeAsync(fileRecordId, knowledgeId, deleteFiles, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        })
        .Produces<DetachResult>(200)
        .Produces(404);

        // File-cleanup policy (config-backed, no DB — R8). Lets the web client know whether to
        // prompt on detach (PromptUser) or proceed silently (AutoCleanup / PreserveAlways).
        app.MapGet("/api/v1/settings/file-cleanup", (IFileCleanupPolicyResolver resolver) =>
            Results.Ok(new FileCleanupSettingsDto(resolver.ResolveMode().ToString())))
            .WithTags("Settings")
            .Produces<FileCleanupSettingsDto>(200);

        // Comment attachment endpoints (parallel structure)
        var commentGroup = app.MapGroup("/api/v1/comments/{commentId:guid}/attachments")
            .WithTags("Comments", "Files");

        // POST /api/comments/{commentId}/attachments -- attach file to comment
        commentGroup.MapPost("/", async (
            FileStorageService svc,
            Guid commentId,
            AttachFileRequest req,
            CancellationToken ct) =>
        {
            if (req.FileRecordId == Guid.Empty)
                return Results.BadRequest(new { error = "fileRecordId is required" });

            try
            {
                var result = await svc.AttachToCommentAsync(req.FileRecordId, commentId, ct);
                return Results.Created($"/api/v1/comments/{commentId}/attachments/{result.Id}", result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .Produces<FileAttachmentDto>(201)
        .Produces(400);

        // GET /api/comments/{commentId}/attachments -- list attached files
        commentGroup.MapGet("/", async (
            FileStorageService svc, Guid commentId, CancellationToken ct) =>
        {
            var result = await svc.GetAttachmentsForCommentAsync(commentId, ct);
            return Results.Ok(result);
        })
        .Produces<List<FileMetadataDto>>();

        // DELETE /api/comments/{commentId}/attachments/{fileRecordId} -- detach file
        commentGroup.MapDelete("/{fileRecordId:guid}", async (
            FileStorageService svc, Guid commentId, Guid fileRecordId, CancellationToken ct) =>
        {
            var success = await svc.DetachFromCommentAsync(fileRecordId, commentId, ct);
            return success
                ? Results.Ok(new { fileRecordId, commentId, detached = true })
                : Results.NotFound();
        })
        .Produces<object>(200)
        .Produces(404);
    }
}

/// <summary>
/// Request to attach existing FileRecord to Knowledge/Comment.
/// </summary>
public record AttachFileRequest(Guid FileRecordId);

/// <summary>
/// Config-backed file-cleanup policy exposed to the web client so it knows whether to prompt on
/// detach. <c>Mode</c> is one of AutoCleanup | PreserveAlways | PromptUser.
/// </summary>
public record FileCleanupSettingsDto(string Mode);
