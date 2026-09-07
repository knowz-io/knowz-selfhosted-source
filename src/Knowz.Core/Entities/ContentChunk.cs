using Knowz.Core.Interfaces;

namespace Knowz.Core.Entities;

public class ContentChunk : ISelfHostedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid KnowledgeId { get; set; }
    public int Position { get; set; }
    public string Content { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public string? EmbeddingVectorJson { get; set; }
    public string? ContextSummary { get; set; }
    public bool IsContextualEmbedding { get; set; }
    public DateTime? EmbeddedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
    public string? PlatformData { get; set; }

    // Navigation
    public virtual Knowledge Knowledge { get; set; } = null!;
}
