using System.Linq.Expressions;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Application.Services;

/// <summary>
/// Service for file storage operations coordinating IFileStorageProvider + DB.
/// Pattern: Repository for CRUD, DbContext for complex queries (LIKE, includes).
/// </summary>
public class FileStorageService
{
    private readonly IFileStorageProvider _storageProvider;
    private readonly ISelfHostedRepository<FileRecord> _fileRepo;
    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly ILogger<FileStorageService> _logger;
    private readonly IFileContentExtractor? _contentExtractor;
    private readonly IEnrichmentOutboxWriter? _enrichmentWriter;
    private readonly IFileCleanupPolicyResolver? _cleanupPolicyResolver;

    public FileStorageService(
        IFileStorageProvider storageProvider,
        ISelfHostedRepository<FileRecord> fileRepo,
        SelfHostedDbContext db,
        ITenantProvider tenantProvider,
        ILogger<FileStorageService> logger,
        IFileContentExtractor? contentExtractor = null,
        IEnrichmentOutboxWriter? enrichmentWriter = null,
        IFileCleanupPolicyResolver? cleanupPolicyResolver = null)
    {
        _storageProvider = storageProvider;
        _fileRepo = fileRepo;
        _db = db;
        _tenantProvider = tenantProvider;
        _logger = logger;
        _contentExtractor = contentExtractor;
        _enrichmentWriter = enrichmentWriter;
        _cleanupPolicyResolver = cleanupPolicyResolver;
    }

    public async Task<FileUploadResult> UploadAsync(
        Stream stream, string fileName, string contentType, CancellationToken ct = default)
    {
        if (stream == null)
            throw new ArgumentException("Stream cannot be null");

        if (!stream.CanRead)
            throw new ArgumentException("Stream must be readable");

        if (stream.CanSeek && stream.Length == 0)
            throw new ArgumentException("Stream cannot be empty");

        // Non-seekable streams (e.g. HTTP upload streams) need to be buffered
        // so we can seek back for both blob upload and content extraction.
        // IMPORTANT: Do NOT buffer into MemoryStream — for 500 MB uploads this
        // blows memory (500 MB × N concurrent uploads). Use a temp file on
        // disk with FileOptions.DeleteOnClose so it auto-cleans when disposed.
        Stream workingStream = stream;
        FileStream? tempFileStream = null;
        string? tempFilePath = null;
        if (!stream.CanSeek)
        {
            tempFilePath = Path.Combine(Path.GetTempPath(), $"knowz-upload-{Guid.NewGuid():N}.tmp");
            tempFileStream = new FileStream(
                tempFilePath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            await stream.CopyToAsync(tempFileStream, ct).ConfigureAwait(false);
            tempFileStream.Position = 0;
            workingStream = tempFileStream;
        }

        try
        {
            var fileRecordId = Guid.NewGuid();
            var tenantId = _tenantProvider.TenantId;

            var blobUri = await _storageProvider.UploadAsync(tenantId, fileRecordId, workingStream, contentType, ct)
                .ConfigureAwait(false);

            var sizeBytes = workingStream.CanSeek ? workingStream.Length : 0;

            var fileRecord = new FileRecord
            {
                Id = fileRecordId,
                TenantId = tenantId,
                FileName = fileName,
                ContentType = contentType,
                SizeBytes = sizeBytes,
                BlobUri = blobUri,
                BlobMigrationPending = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            // Proactive text extraction (best-effort, with timeout to avoid blocking uploads).
            // Skip for files > 50 MB — extraction is prohibitively expensive, and the
            // enrichment pipeline handles large files asynchronously in the background.
            const long ProactiveExtractionMaxBytes = 50L * 1024 * 1024;
            var extractionEligible = _contentExtractor != null
                && _contentExtractor.CanExtract(contentType)
                && workingStream.CanSeek
                && sizeBytes > 0
                && sizeBytes <= ProactiveExtractionMaxBytes;

            if (extractionEligible)
            {
                try
                {
                    workingStream.Position = 0;
                    // 30-second timeout — image vision analysis can take 15-25s.
                    // Text/PDF/Office extraction is fast (<1s) so this only affects images.
                    using var extractionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    extractionCts.CancelAfter(TimeSpan.FromSeconds(30));
                    var extraction = await _contentExtractor!.ExtractAsync(fileRecord, workingStream, extractionCts.Token);
                    if (extraction.Success)
                    {
                        fileRecord.ExtractedText = extraction.ExtractedText;
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Proactive extraction timed out for {FileRecordId} ({ContentType}) — will be handled by enrichment",
                        fileRecordId, contentType);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Proactive text extraction failed for {FileRecordId}", fileRecordId);
                }
            }
            else if (_contentExtractor != null && _contentExtractor.CanExtract(contentType) && sizeBytes > ProactiveExtractionMaxBytes)
            {
                _logger.LogInformation("Skipping proactive extraction for {FileRecordId}: {SizeBytes} bytes exceeds {Limit} byte threshold — will be handled by enrichment",
                    fileRecordId, sizeBytes, ProactiveExtractionMaxBytes);
            }

            await _fileRepo.AddAsync(fileRecord, ct).ConfigureAwait(false);
            await _fileRepo.SaveChangesAsync(ct).ConfigureAwait(false);

            _logger.LogInformation("File uploaded: {FileRecordId}, {FileName}, {SizeBytes} bytes",
                fileRecordId, fileName, sizeBytes);

            return new FileUploadResult(fileRecordId, fileName, contentType, sizeBytes, blobUri, true);
        }
        finally
        {
            // FileOptions.DeleteOnClose handles temp file cleanup on dispose.
            if (tempFileStream != null)
                await tempFileStream.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<(Stream stream, string contentType, string fileName)?> DownloadAsync(
        Guid fileRecordId, CancellationToken ct = default)
    {
        var fileRecord = await _fileRepo.GetByIdAsync(fileRecordId, ct).ConfigureAwait(false);
        if (fileRecord == null)
            return null;

        try
        {
            var result = await _storageProvider.DownloadAsync(fileRecord.TenantId, fileRecordId, ct)
                .ConfigureAwait(false);
            return (result.stream, fileRecord.ContentType ?? result.contentType, fileRecord.FileName);
        }
        catch (FileNotFoundException)
        {
            _logger.LogWarning("Storage file not found for FileRecord {FileRecordId}", fileRecordId);
            return null;
        }
    }

    public async Task<DeleteResult?> DeleteAsync(Guid fileRecordId, CancellationToken ct = default)
    {
        var fileRecord = await _fileRepo.GetByIdAsync(fileRecordId, ct).ConfigureAwait(false);
        if (fileRecord == null)
            return null;

        await _fileRepo.SoftDeleteAsync(fileRecord, ct).ConfigureAwait(false);
        await _fileRepo.SaveChangesAsync(ct).ConfigureAwait(false);

        try
        {
            await _storageProvider.DeleteAsync(fileRecord.TenantId, fileRecordId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete storage file for FileRecord {FileRecordId}", fileRecordId);
        }

        return new DeleteResult(fileRecordId, true);
    }

    private static readonly Expression<Func<FileRecord, FileMetadataDto>> FileRecordProjection =
        f => new FileMetadataDto(
            f.Id, f.FileName, f.ContentType, f.SizeBytes, f.BlobUri,
            f.TranscriptionText, f.ExtractedText, f.VisionDescription,
            f.BlobMigrationPending, f.CreatedAt, f.UpdatedAt,
            f.Attachments.Where(a => a.KnowledgeId != null).Select(a => a.KnowledgeId).FirstOrDefault(),
            f.Attachments.Where(a => a.KnowledgeId != null && a.Knowledge != null)
                .Select(a => a.Knowledge!.Title).FirstOrDefault(),
            f.Attachments.Where(a => a.KnowledgeId != null && a.Knowledge != null)
                .SelectMany(a => a.Knowledge!.KnowledgeVaults)
                .Select(kv => (Guid?)kv.VaultId).FirstOrDefault(),
            f.Attachments.Where(a => a.KnowledgeId != null && a.Knowledge != null)
                .SelectMany(a => a.Knowledge!.KnowledgeVaults)
                .Select(kv => kv.Vault.Name).FirstOrDefault(),
            f.VisionTagsJson, f.VisionObjectsJson, f.VisionExtractedText,
            f.VisionAnalyzedAt, f.LayoutDataJson, f.TextExtractionStatus,
            f.TextExtractedAt, f.AttachmentAIProvider
        );

    public async Task<FileMetadataDto?> GetMetadataAsync(Guid fileRecordId, CancellationToken ct = default)
    {
        return await _db.FileRecords
            .Where(f => f.Id == fileRecordId)
            .Select(FileRecordProjection)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<FileListResponse> ListAsync(
        int page = 1, int pageSize = 20, string? search = null, string? contentTypeFilter = null,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var query = _db.FileRecords.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.ToLowerInvariant();
            query = query.Where(f => f.FileName.ToLower().Contains(searchLower));
        }

        if (!string.IsNullOrWhiteSpace(contentTypeFilter))
        {
            query = query.Where(f => f.ContentType == contentTypeFilter);
        }

        query = query.OrderByDescending(f => f.CreatedAt);

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(FileRecordProjection)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var totalPages = pageSize > 0 ? (int)Math.Ceiling((double)total / pageSize) : 0;
        return new FileListResponse(items, page, pageSize, total, totalPages);
    }

    public async Task<string> GenerateDownloadUrlAsync(
        Guid fileRecordId, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        var fileRecord = await _fileRepo.GetByIdAsync(fileRecordId, ct).ConfigureAwait(false);
        if (fileRecord == null)
            throw new FileNotFoundException($"FileRecord {fileRecordId} not found");

        return await _storageProvider.GenerateDownloadUrlAsync(
            fileRecord.TenantId, fileRecordId, fileRecord.FileName, expiry, ct)
            .ConfigureAwait(false);
    }

    // --- Attachment Operations ---

    public async Task<FileAttachmentDto> AttachToKnowledgeAsync(
        Guid fileRecordId, Guid knowledgeId, CancellationToken ct = default)
    {
        var fileRecord = await _fileRepo.GetByIdAsync(fileRecordId, ct).ConfigureAwait(false);
        if (fileRecord == null)
            throw new InvalidOperationException($"FileRecord {fileRecordId} does not exist");

        if (!await _db.KnowledgeItems.AnyAsync(k => k.Id == knowledgeId, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"Knowledge {knowledgeId} does not exist");

        var existing = await _db.FileAttachments
            .FirstOrDefaultAsync(fa => fa.FileRecordId == fileRecordId && fa.KnowledgeId == knowledgeId, ct)
            .ConfigureAwait(false);

        if (existing != null)
            return new FileAttachmentDto(existing.Id, existing.FileRecordId, existing.KnowledgeId, existing.CommentId, existing.CreatedAt);

        var attachment = new FileAttachment
        {
            Id = Guid.NewGuid(),
            FileRecordId = fileRecordId,
            KnowledgeId = knowledgeId,
            CommentId = null,
            TenantId = _tenantProvider.TenantId,
            CreatedAt = DateTime.UtcNow
        };

        _db.FileAttachments.Add(attachment);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Extract content if not already done (idempotent)
        if (string.IsNullOrEmpty(fileRecord.ExtractedText) && _contentExtractor != null
            && _contentExtractor.CanExtract(fileRecord.ContentType))
        {
            try
            {
                var downloadResult = await _storageProvider.DownloadAsync(fileRecord.TenantId, fileRecordId, ct);
                using var stream = downloadResult.stream;
                var extraction = await _contentExtractor.ExtractAsync(fileRecord, stream, ct);
                if (extraction.Success)
                {
                    fileRecord.ExtractedText = extraction.ExtractedText;
                    fileRecord.UpdatedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Content extraction failed for FileRecord {Id}", fileRecordId);
            }
        }

        // Trigger re-enrichment of parent knowledge (AFTER save)
        if (_enrichmentWriter != null)
        {
            try
            {
                await _enrichmentWriter.EnqueueAsync(knowledgeId, _tenantProvider.TenantId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enqueue re-enrichment after attachment for Knowledge {Id}", knowledgeId);
            }
        }

        return new FileAttachmentDto(attachment.Id, attachment.FileRecordId, attachment.KnowledgeId, attachment.CommentId, attachment.CreatedAt);
    }

    public async Task<FileAttachmentDto> AttachToCommentAsync(
        Guid fileRecordId, Guid commentId, CancellationToken ct = default)
    {
        var fileRecord = await _fileRepo.GetByIdAsync(fileRecordId, ct).ConfigureAwait(false);
        if (fileRecord == null)
            throw new InvalidOperationException($"FileRecord {fileRecordId} does not exist");

        if (!await _db.Comments.AnyAsync(c => c.Id == commentId, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"Comment {commentId} does not exist");

        var existing = await _db.FileAttachments
            .FirstOrDefaultAsync(fa => fa.FileRecordId == fileRecordId && fa.CommentId == commentId, ct)
            .ConfigureAwait(false);

        if (existing != null)
            return new FileAttachmentDto(existing.Id, existing.FileRecordId, existing.KnowledgeId, existing.CommentId, existing.CreatedAt);

        var attachment = new FileAttachment
        {
            Id = Guid.NewGuid(),
            FileRecordId = fileRecordId,
            KnowledgeId = null,
            CommentId = commentId,
            TenantId = _tenantProvider.TenantId,
            CreatedAt = DateTime.UtcNow
        };

        _db.FileAttachments.Add(attachment);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Extract content if not already done
        if (string.IsNullOrEmpty(fileRecord.ExtractedText) && _contentExtractor != null
            && _contentExtractor.CanExtract(fileRecord.ContentType))
        {
            try
            {
                var downloadResult = await _storageProvider.DownloadAsync(fileRecord.TenantId, fileRecordId, ct);
                using var stream = downloadResult.stream;
                var extraction = await _contentExtractor.ExtractAsync(fileRecord, stream, ct);
                if (extraction.Success)
                {
                    fileRecord.ExtractedText = extraction.ExtractedText;
                    fileRecord.UpdatedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Content extraction failed for FileRecord {Id}", fileRecordId);
            }
        }

        // Resolve parent Knowledge for re-enrichment
        var comment = await _db.Comments.FindAsync(new object[] { commentId }, ct);
        if (comment != null && _enrichmentWriter != null)
        {
            try
            {
                await _enrichmentWriter.EnqueueAsync(comment.KnowledgeId, _tenantProvider.TenantId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enqueue re-enrichment after comment attachment for Knowledge {Id}", comment.KnowledgeId);
            }
        }

        return new FileAttachmentDto(attachment.Id, attachment.FileRecordId, attachment.KnowledgeId, attachment.CommentId, attachment.CreatedAt);
    }

    /// <summary>
    /// Detach a file from a knowledge item. Honors the configured <see cref="FileCleanupMode"/>
    /// and an optional per-request <paramref name="deleteFiles"/> override (fail-safe resolution).
    /// WorkGroupID: kc-feat-file-delete-orphan-policy-20260616-140729 — FEAT_SelfHostedFileCleanupPolicy (N6).
    /// </summary>
    /// <param name="deleteFiles">
    /// null (default) = not supplied. Effective destructive flag is resolved fail-safe:
    /// AutoCleanup → true; PreserveAlways → false; PromptUser + true → true; PromptUser + false/absent → false.
    /// When the effective flag is true AND no other entity references the file, the FileRecord is
    /// soft-deleted and the blob is IMMEDIATELY deleted (self-hosted has NO grace period).
    /// Files referenced elsewhere are always preserved (last-link guard).
    /// </param>
    /// <returns>A <see cref="DetachResult"/>, or null if the attachment did not exist.</returns>
    public async Task<DetachResult?> DetachFromKnowledgeAsync(
        Guid fileRecordId, Guid knowledgeId, bool? deleteFiles, CancellationToken ct = default)
    {
        var attachment = await _db.FileAttachments
            .FirstOrDefaultAsync(fa => fa.FileRecordId == fileRecordId && fa.KnowledgeId == knowledgeId, ct)
            .ConfigureAwait(false);

        if (attachment == null)
            return null;

        // Resolve effective destructive flag (R2 table). Null resolver defaults to PromptUser (fail-safe).
        var mode = _cleanupPolicyResolver?.ResolveMode() ?? FileCleanupMode.PromptUser;
        var effectiveDeleteFiles = ResolveEffectiveDeleteFiles(mode, deleteFiles);

        var result = new DetachResult { Detached = true };

        // Cleanup decision MUST run while the junction row still exists so the last-link guard
        // counts it as one of the rows being removed (mirrors platform "Attachment"-type lookup).
        var fileRecord = await _db.FileRecords
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == fileRecordId, ct)
            .ConfigureAwait(false);
        var fileName = fileRecord?.FileName ?? fileRecordId.ToString();

        if (!effectiveDeleteFiles)
        {
            // Preserve path. This is also the fix for the pre-existing self-hosted orphan bug:
            // the previous code removed the junction row and left FileRecord/blob dangling forever.
            // Now the FileRecord is explicitly preserved and queryable.
            result = result with
            {
                FilesPreserved = result.FilesPreserved + 1,
                PreservedFileNames = new List<string>(result.PreservedFileNames) { fileName }
            };
        }
        else
        {
            // Last-link cross-reference guard (R3 — mirrors CommentService.DeleteCommentAsync):
            // count references from ALL FileAttachment rows for this FileRecord, EXCLUDING the row
            // being detached in THIS operation. Only delete when nothing else references it.
            var otherReferenceCount = await _db.FileAttachments
                .Where(fa => fa.FileRecordId == fileRecordId && fa.Id != attachment.Id)
                .CountAsync(ct)
                .ConfigureAwait(false);

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
            }
            else if (fileRecord != null && !fileRecord.IsDeleted)
            {
                // No other references: attempt the (synchronous, immediate) blob delete FIRST.
                // Only persist FileRecord.IsDeleted if the blob delete succeeds — if it throws,
                // we bail without saving so the FileRecord stays referenceable (R5).
                try
                {
                    await _storageProvider.DeleteAsync(_tenantProvider.TenantId, fileRecordId, ct)
                        .ConfigureAwait(false);
                    fileRecord.IsDeleted = true;
                    fileRecord.UpdatedAt = DateTime.UtcNow;
                    result = result with
                    {
                        FilesDeleted = result.FilesDeleted + 1,
                        DeletedFileNames = new List<string>(result.DeletedFileNames) { fileName }
                    };
                    _logger.LogInformation(
                        "Deleted FileRecord {FileRecordId} ({FileName}) and blob on detach from Knowledge {KnowledgeId}",
                        fileRecordId, fileName, knowledgeId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to delete blob for FileRecord {FileRecordId} ({FileName}); FileRecord will remain active",
                        fileRecordId, fileName);
                    throw;
                }
            }
        }

        // Remove the junction row (existing behavior preserved) and persist.
        _db.FileAttachments.Remove(attachment);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Trigger re-enrichment after detach (R6 — preserved, runs after the cleanup decision)
        if (_enrichmentWriter != null)
        {
            try
            {
                await _enrichmentWriter.EnqueueAsync(knowledgeId, _tenantProvider.TenantId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enqueue re-enrichment after detach for Knowledge {Id}", knowledgeId);
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves the effective destructive flag from the configured mode + the optional per-request
    /// override, fail-safe. See R2 of FEAT_SelfHostedFileCleanupPolicy.
    /// </summary>
    internal static bool ResolveEffectiveDeleteFiles(FileCleanupMode mode, bool? deleteFiles) => mode switch
    {
        FileCleanupMode.AutoCleanup => true,
        FileCleanupMode.PreserveAlways => false,
        // PromptUser: honor explicit param; absent (null) => preserve (FAIL-SAFE).
        FileCleanupMode.PromptUser => deleteFiles ?? false,
        _ => false
    };

    public async Task<bool> DetachFromCommentAsync(
        Guid fileRecordId, Guid commentId, CancellationToken ct = default)
    {
        var attachment = await _db.FileAttachments
            .FirstOrDefaultAsync(fa => fa.FileRecordId == fileRecordId && fa.CommentId == commentId, ct)
            .ConfigureAwait(false);

        if (attachment == null)
            return false;

        _db.FileAttachments.Remove(attachment);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Resolve parent Knowledge for re-enrichment
        var comment = await _db.Comments.FindAsync(new object[] { commentId }, ct);
        if (comment != null && _enrichmentWriter != null)
        {
            try
            {
                await _enrichmentWriter.EnqueueAsync(comment.KnowledgeId, _tenantProvider.TenantId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enqueue re-enrichment after comment detach for Knowledge {Id}", comment.KnowledgeId);
            }
        }

        return true;
    }

    public async Task<List<FileMetadataDto>> GetAttachmentsForKnowledgeAsync(
        Guid knowledgeId, CancellationToken ct = default)
    {
        var fileRecords = await _db.FileAttachments
            .Where(fa => fa.KnowledgeId == knowledgeId)
            .Include(fa => fa.FileRecord)
            .Select(fa => fa.FileRecord)
            .Where(f => !f.IsDeleted)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return fileRecords.Select(MapToDto).ToList();
    }

    public async Task<List<FileMetadataDto>> GetAttachmentsForCommentAsync(
        Guid commentId, CancellationToken ct = default)
    {
        var fileRecords = await _db.FileAttachments
            .Where(fa => fa.CommentId == commentId)
            .Include(fa => fa.FileRecord)
            .Select(fa => fa.FileRecord)
            .Where(f => !f.IsDeleted)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return fileRecords.Select(MapToDto).ToList();
    }

    private static FileMetadataDto MapToDto(FileRecord f) => new(
        f.Id, f.FileName, f.ContentType, f.SizeBytes, f.BlobUri,
        f.TranscriptionText, f.ExtractedText, f.VisionDescription,
        f.BlobMigrationPending, f.CreatedAt, f.UpdatedAt,
        VisionTagsJson: f.VisionTagsJson, VisionObjectsJson: f.VisionObjectsJson,
        VisionExtractedText: f.VisionExtractedText, VisionAnalyzedAt: f.VisionAnalyzedAt,
        LayoutDataJson: f.LayoutDataJson, TextExtractionStatus: f.TextExtractionStatus,
        TextExtractedAt: f.TextExtractedAt, AttachmentAIProvider: f.AttachmentAIProvider);
}
