using Knowz.Core.Interfaces;

namespace Knowz.Core.Entities;

public class FileRecord : ISelfHostedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long SizeBytes { get; set; }
    public string? BlobUri { get; set; }
    public string? TranscriptionText { get; set; }
    public string? ExtractedText { get; set; }
    public string? VisionDescription { get; set; }
    public string? VisionTagsJson { get; set; }
    public string? VisionObjectsJson { get; set; }
    public string? VisionExtractedText { get; set; }
    public DateTime? VisionAnalyzedAt { get; set; }
    public string? AttachmentAIProvider { get; set; }

    // Document extraction fields (NodeID: SelfHostedDocumentExtraction)
    public string? LayoutDataJson { get; set; }
    public DateTime? TextExtractedAt { get; set; }
    public int TextExtractionStatus { get; set; } // 0=NotStarted, 1=Processing, 2=Completed, 3=Failed
    public string? TextExtractionError { get; set; }

    public bool BlobMigrationPending { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
    public string? PlatformData { get; set; }
    public virtual ICollection<FileAttachment> Attachments { get; set; } = new List<FileAttachment>();
}
