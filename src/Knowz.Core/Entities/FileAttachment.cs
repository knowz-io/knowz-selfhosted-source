namespace Knowz.Core.Entities;

public class FileAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FileRecordId { get; set; }
    public virtual FileRecord FileRecord { get; set; } = null!;
    public Guid? KnowledgeId { get; set; }
    public virtual Knowledge? Knowledge { get; set; }
    public Guid? CommentId { get; set; }
    public virtual KnowledgeComment? Comment { get; set; }
    public Guid TenantId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
