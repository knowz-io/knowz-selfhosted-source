namespace Knowz.SelfHosted.Application.DTOs;

/// <summary>
/// Result of file upload operation.
/// </summary>
public record FileUploadResult(
    Guid FileRecordId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string BlobUri,
    bool Success);

/// <summary>
/// FileRecord metadata (no binary content).
/// </summary>
public record FileMetadataDto(
    Guid Id,
    string FileName,
    string? ContentType,
    long SizeBytes,
    string? BlobUri,
    string? TranscriptionText,
    string? ExtractedText,
    string? VisionDescription,
    bool BlobMigrationPending,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid? KnowledgeId = null,
    string? KnowledgeTitle = null,
    Guid? VaultId = null,
    string? VaultName = null,
    // Structured vision fields (NodeID: SelfHostedAttachmentExperience)
    string? VisionTagsJson = null,
    string? VisionObjectsJson = null,
    string? VisionExtractedText = null,
    DateTime? VisionAnalyzedAt = null,
    string? LayoutDataJson = null,
    int TextExtractionStatus = 0,
    DateTime? TextExtractedAt = null,
    string? AttachmentAIProvider = null);

/// <summary>
/// FileAttachment junction record (links FileRecord to Knowledge/Comment).
/// </summary>
public record FileAttachmentDto(
    Guid Id,
    Guid FileRecordId,
    Guid? KnowledgeId,
    Guid? CommentId,
    DateTime CreatedAt);

/// <summary>
/// Result of detaching a file from a knowledge item.
/// Reports whether the link was removed and how many underlying files were preserved
/// (kept in storage) vs deleted. Self-hosted deletes blobs IMMEDIATELY (no grace period).
/// WorkGroupID: kc-feat-file-delete-orphan-policy-20260616-140729 — FEAT_SelfHostedFileCleanupPolicy (N6).
/// </summary>
public record DetachResult
{
    public bool Detached { get; init; }
    public int FilesDeleted { get; init; }
    public int FilesPreserved { get; init; }
    public List<string> DeletedFileNames { get; init; } = new();
    public List<string> PreservedFileNames { get; init; } = new();
}

/// <summary>
/// Paginated list of FileRecords.
/// </summary>
public record FileListResponse(
    List<FileMetadataDto> Items,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages);
