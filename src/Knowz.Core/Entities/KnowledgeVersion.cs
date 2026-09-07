using Knowz.Core.Interfaces;

namespace Knowz.Core.Entities;

public class KnowledgeVersion : ISelfHostedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    public Guid KnowledgeId { get; set; }
    public int VersionNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? ContentType { get; set; }

    public Guid? CreatedByUserId { get; set; }
    public string? ChangeDescription { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? PlatformData { get; set; }
}
