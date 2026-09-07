using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Application.Services;

public class CommentService
{
    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly ILogger<CommentService> _logger;
    private readonly IFileStorageProvider _storage;
    private readonly IEnrichmentOutboxWriter? _enrichmentWriter;

    public CommentService(
        SelfHostedDbContext db,
        ITenantProvider tenantProvider,
        ILogger<CommentService> logger,
        IFileStorageProvider storage,
        IEnrichmentOutboxWriter? enrichmentWriter = null)
    {
        _db = db;
        _tenantProvider = tenantProvider;
        _logger = logger;
        _storage = storage;
        _enrichmentWriter = enrichmentWriter;
    }

    public async Task<CommentResponse?> AddCommentAsync(
        Guid knowledgeId, string body, string authorName,
        Guid? parentCommentId, string? sentiment, CancellationToken ct)
    {
        var knowledge = await _db.KnowledgeItems
            .FirstOrDefaultAsync(k => k.Id == knowledgeId, ct);
        if (knowledge == null)
            return null;

        if (parentCommentId.HasValue)
        {
            var parent = await _db.Comments
                .FirstOrDefaultAsync(c => c.Id == parentCommentId.Value && c.KnowledgeId == knowledgeId, ct);
            if (parent == null)
                return null;
        }

        var tenantId = _tenantProvider.TenantId;
        var comment = new KnowledgeComment
        {
            TenantId = tenantId,
            KnowledgeId = knowledgeId,
            ParentCommentId = parentCommentId,
            AuthorName = authorName,
            Body = body,
            Sentiment = sentiment
        };

        _db.Comments.Add(comment);
        await _db.SaveChangesAsync(ct);

        await TriggerEnrichmentAsync(knowledgeId, tenantId, ct);

        return MapToResponse(comment, new List<CommentResponse>(), 0);
    }

    public async Task<List<CommentResponse>> ListCommentsAsync(
        Guid knowledgeId, CancellationToken ct)
    {
        var comments = await _db.Comments
            .Where(c => c.KnowledgeId == knowledgeId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        var commentIds = comments.Select(c => c.Id).ToList();
        var attachmentCounts = await _db.FileAttachments
            .Where(fa => fa.CommentId != null && commentIds.Contains(fa.CommentId.Value))
            .GroupBy(fa => fa.CommentId!.Value)
            .Select(g => new { CommentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CommentId, x => x.Count, ct);

        var topLevel = comments.Where(c => c.ParentCommentId == null).ToList();
        var byParent = comments
            .Where(c => c.ParentCommentId != null)
            .GroupBy(c => c.ParentCommentId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.CreatedAt).ToList());

        return topLevel.Select(c =>
        {
            var replies = byParent.TryGetValue(c.Id, out var children)
                ? children.Select(r => MapToResponse(r, null, attachmentCounts.GetValueOrDefault(r.Id))).ToList()
                : new List<CommentResponse>();
            return MapToResponse(c, replies, attachmentCounts.GetValueOrDefault(c.Id));
        }).ToList();
    }

    public async Task<CommentResponse?> GetCommentAsync(Guid commentId, CancellationToken ct)
    {
        var comment = await _db.Comments
            .FirstOrDefaultAsync(c => c.Id == commentId, ct);
        if (comment == null)
            return null;

        var attachmentCount = await _db.FileAttachments
            .CountAsync(fa => fa.CommentId == commentId, ct);

        return MapToResponse(comment, null, attachmentCount);
    }

    public async Task<CommentResponse?> UpdateCommentAsync(
        Guid commentId, string? body, string? sentiment, CancellationToken ct)
    {
        var comment = await _db.Comments
            .FirstOrDefaultAsync(c => c.Id == commentId, ct);
        if (comment == null)
            return null;

        bool bodyChanged = false;
        if (body != null)
        {
            comment.Body = body;
            bodyChanged = true;
        }
        if (sentiment != null)
        {
            comment.Sentiment = sentiment;
        }

        comment.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        if (bodyChanged)
        {
            await TriggerEnrichmentAsync(comment.KnowledgeId, comment.TenantId, ct);
        }

        var attachmentCount = await _db.FileAttachments
            .CountAsync(fa => fa.CommentId == commentId, ct);

        return MapToResponse(comment, null, attachmentCount);
    }

    /// <summary>
    /// Delete a comment and cascade to replies. Controls whether attached files are preserved
    /// or permanently deleted via the <paramref name="deleteFiles"/> flag.
    /// WorkGroupID: kc-fix-attach-delete-transcript-20260411-080000 — FEAT_CommentDeleteAttachmentChoice.
    /// </summary>
    /// <param name="deleteFiles">
    /// When false (default, safer): junction rows are hard-deleted but the FileRecord and blob are preserved.
    /// When true: each FileRecord is cross-referenced across ALL FileAttachment rows. If no other entity
    /// references it, the FileRecord is soft-deleted and the blob is IMMEDIATELY deleted via
    /// <see cref="IFileStorageProvider.DeleteAsync"/>. Files referenced elsewhere are preserved automatically.
    /// Self-hosted has NO grace period — blob deletion is synchronous and irreversible.
    /// </param>
    /// <returns>A <see cref="CommentDeleteResult"/> with preserved/deleted file counts, or null if not found.</returns>
    public async Task<CommentDeleteResult?> DeleteCommentAsync(Guid commentId, bool deleteFiles, CancellationToken ct)
    {
        var comment = await _db.Comments
            .FirstOrDefaultAsync(c => c.Id == commentId, ct);
        if (comment == null)
            return null;

        // Soft-delete the comment
        comment.IsDeleted = true;
        comment.UpdatedAt = DateTime.UtcNow;

        // Cascade soft-delete to child replies
        var childReplies = await _db.Comments
            .Where(c => c.ParentCommentId == commentId)
            .ToListAsync(ct);
        foreach (var reply in childReplies)
        {
            reply.IsDeleted = true;
            reply.UpdatedAt = DateTime.UtcNow;
        }

        // Gather FileAttachment junction rows for this comment + all cascaded replies
        var commentIds = childReplies.Select(r => r.Id).Append(commentId).ToList();
        var attachments = await _db.FileAttachments
            .Where(fa => fa.CommentId != null && commentIds.Contains(fa.CommentId.Value))
            .ToListAsync(ct);

        var result = new CommentDeleteResult();

        if (attachments.Count > 0)
        {
            var uniqueFileRecordIds = attachments.Select(a => a.FileRecordId).Distinct().ToList();

            // Load the FileRecord rows we might touch. IgnoreQueryFilters so already-soft-deleted
            // records don't surprise us (cross-reference check still needs their metadata).
            var fileRecords = await _db.FileRecords
                .IgnoreQueryFilters()
                .Where(f => uniqueFileRecordIds.Contains(f.Id))
                .ToDictionaryAsync(f => f.Id, ct);

            // Capture the junction-row IDs we're about to remove so we can exclude them from the
            // cross-reference count below (these are "about to be deleted in this operation").
            var attachmentIdsBeingRemoved = attachments.Select(a => a.Id).ToHashSet();

            foreach (var fileRecordId in uniqueFileRecordIds)
            {
                fileRecords.TryGetValue(fileRecordId, out var fileRecord);
                var fileName = fileRecord?.FileName ?? fileRecordId.ToString();

                if (!deleteFiles)
                {
                    // Preserve path: FileRecord stays exactly as-is (junction row removal happens below).
                    // This is also the fix for the pre-existing self-hosted orphan bug — the previous
                    // code hard-deleted junction rows and left FileRecord/blob dangling forever.
                    // Now the FileRecord is explicitly preserved and queryable.
                    result = result with
                    {
                        FilesPreserved = result.FilesPreserved + 1,
                        PreservedFileNames = new List<string>(result.PreservedFileNames) { fileName }
                    };
                    continue;
                }

                // deleteFiles == true: cross-reference check (R4 — mirrors platform AttachmentCleanupService).
                // Count references from ALL FileAttachment rows for this FileRecord, EXCLUDING the rows
                // we're deleting in this operation.
                var otherReferenceCount = await _db.FileAttachments
                    .Where(fa => fa.FileRecordId == fileRecordId && !attachmentIdsBeingRemoved.Contains(fa.Id))
                    .CountAsync(ct);

                if (otherReferenceCount > 0)
                {
                    _logger.LogInformation(
                        "Preserving FileRecord {FileRecordId} ({FileName}) — still referenced by {Count} other attachment(s)",
                        fileRecordId, fileName, otherReferenceCount);
                    result = result with
                    {
                        FilesPreserved = result.FilesPreserved + 1,
                        PreservedFileNames = new List<string>(result.PreservedFileNames) { fileName }
                    };
                    continue;
                }

                if (fileRecord == null || fileRecord.IsDeleted)
                {
                    // Already gone — nothing to do
                    continue;
                }

                // No other references: mark FileRecord soft-deleted IN MEMORY first, then attempt
                // the (synchronous, immediate) blob delete. Only persist if blob delete succeeds.
                // If the blob delete throws, we bail without saving so FileRecord.IsDeleted never
                // reaches the database — the file remains queryable at the next request.
                try
                {
                    await _storage.DeleteAsync(_tenantProvider.TenantId, fileRecordId, ct);
                    fileRecord.IsDeleted = true;
                    fileRecord.UpdatedAt = DateTime.UtcNow;

                    result = result with
                    {
                        FilesDeleted = result.FilesDeleted + 1,
                        DeletedFileNames = new List<string>(result.DeletedFileNames) { fileName }
                    };
                    _logger.LogInformation(
                        "Deleted FileRecord {FileRecordId} ({FileName}) and blob for comment {CommentId}",
                        fileRecordId, fileName, commentId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to delete blob for FileRecord {FileRecordId} ({FileName}); FileRecord will remain active",
                        fileRecordId, fileName);
                    // Re-throw so the caller sees the failure. SaveChangesAsync below won't run.
                    throw;
                }
            }

            // Hard-delete junction rows regardless of deleteFiles (existing behavior preserved).
            _db.FileAttachments.RemoveRange(attachments);
        }

        await _db.SaveChangesAsync(ct);

        await TriggerEnrichmentAsync(comment.KnowledgeId, comment.TenantId, ct);

        return result;
    }

    private async Task TriggerEnrichmentAsync(Guid knowledgeId, Guid tenantId, CancellationToken ct)
    {
        try
        {
            if (_enrichmentWriter != null)
            {
                await _enrichmentWriter.EnqueueAsync(knowledgeId, tenantId, ct);
                _logger.LogDebug("Enqueued knowledge {KnowledgeId} for re-enrichment after comment change", knowledgeId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enqueue knowledge {KnowledgeId} for re-enrichment", knowledgeId);
        }
    }

    private static CommentResponse MapToResponse(
        KnowledgeComment comment, List<CommentResponse>? replies, int attachmentCount)
    {
        return new CommentResponse(
            comment.Id,
            comment.KnowledgeId,
            comment.ParentCommentId,
            comment.AuthorName,
            comment.Body,
            comment.IsAnswer,
            comment.Sentiment,
            comment.CreatedAt,
            comment.UpdatedAt,
            replies,
            attachmentCount);
    }
}
