using Knowz.Core.Interfaces;

namespace Knowz.Core.Entities;

public class Tag : ISelfHostedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
    public string? PlatformData { get; set; }

    public virtual ICollection<Knowledge> KnowledgeItems { get; set; } = new List<Knowledge>();
}
