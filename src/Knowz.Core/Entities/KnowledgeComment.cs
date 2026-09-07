using Knowz.Core.Interfaces;

namespace Knowz.Core.Entities;

public class KnowledgeComment : ISelfHostedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid KnowledgeId { get; set; }
    public virtual Knowledge Knowledge { get; set; } = null!;
    public Guid? ParentCommentId { get; set; }
    public virtual KnowledgeComment? ParentComment { get; set; }
    public virtual ICollection<KnowledgeComment> Replies { get; set; } = new List<KnowledgeComment>();
    public string AuthorName { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsAnswer { get; set; }
    public string? Sentiment { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
    public string? PlatformData { get; set; }
}
