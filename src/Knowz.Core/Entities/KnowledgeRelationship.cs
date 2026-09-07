using Knowz.Core.Enums;
using Knowz.Core.Interfaces;

namespace Knowz.Core.Entities;

public class KnowledgeRelationship : ISelfHostedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    public Guid SourceKnowledgeId { get; set; }
    public Guid TargetKnowledgeId { get; set; }

    public KnowledgeRelationshipType RelationshipType { get; set; } = KnowledgeRelationshipType.RelatedTo;

    public double Confidence { get; set; } = 1.0;
    public double Weight { get; set; } = 1.0;
    public bool IsBidirectional { get; set; } = true;
    public bool IsAutoDetected { get; set; } = false;
    public string? Metadata { get; set; }
    public string? PlatformData { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }

    // Navigation
    public virtual Knowledge? SourceKnowledge { get; set; }
    public virtual Knowledge? TargetKnowledge { get; set; }
}
