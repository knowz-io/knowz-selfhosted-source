using Knowz.Core.Enums;
using Knowz.Core.Interfaces;

namespace Knowz.Core.Entities;

public class Knowledge : ISelfHostedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? BriefSummary { get; set; }
    public string? SummaryRefinementGuidance { get; set; }
    public KnowledgeType Type { get; set; } = KnowledgeType.Note;
    public string? Source { get; set; }
    public string? FilePath { get; set; }

    // Search integration
    public bool IsIndexed { get; set; }
    public DateTime? IndexedAt { get; set; }

    // Creator tracking
    public Guid? CreatedByUserId { get; set; }

    // Timestamps
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// UTC timestamp of the git commit that produced this knowledge row, when applicable.
    /// Set by <see cref="Knowz.SelfHosted.Application.Services.GitCommitHistory.GitCommitHistoryService"/>
    /// during commit ingestion. NULL for non-commit knowledge items and for commit rows ingested
    /// before NODE-2 landed (backfill via CommitBackfillEndpoint NODE-3).
    /// WorkGroupID: kc-feat-commit-history-polish-20260411-051000
    /// NodeID: NODE-2 CommittedAtColumn
    /// </summary>
    public DateTime? CommittedAt { get; set; }

    public bool IsDeleted { get; set; }
    public string? PlatformData { get; set; }

    // Navigation
    public Guid? TopicId { get; set; }
    public virtual Topic? Topic { get; set; }
    public virtual ICollection<Tag> Tags { get; set; } = new List<Tag>();
    public virtual ICollection<KnowledgeVault> KnowledgeVaults { get; set; } = new List<KnowledgeVault>();
    public virtual ICollection<KnowledgePerson> KnowledgePersons { get; set; } = new List<KnowledgePerson>();
    public virtual ICollection<KnowledgeLocation> KnowledgeLocations { get; set; } = new List<KnowledgeLocation>();
    public virtual ICollection<KnowledgeEvent> KnowledgeEvents { get; set; } = new List<KnowledgeEvent>();
    public virtual ICollection<KnowledgeComment> Comments { get; set; } = new List<KnowledgeComment>();
    public virtual ICollection<KnowledgeRelationship> OutgoingRelationships { get; set; } = new List<KnowledgeRelationship>();
    public virtual ICollection<KnowledgeRelationship> IncomingRelationships { get; set; } = new List<KnowledgeRelationship>();
}
