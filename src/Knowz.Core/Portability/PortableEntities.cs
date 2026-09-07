namespace Knowz.Core.Portability;

using System.Text.Json;
using System.Text.Json.Serialization;
using Knowz.Core.Enums;

/// <summary>
/// Portable vault representation. Matches Knowz.Core.Entities.Vault.
/// </summary>
public class PortableVault
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public VaultType? VaultType { get; set; }
    public bool IsDefault { get; set; }
    public Guid? ParentVaultId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Person IDs linked to this vault (vault subject persons).
    /// Added in schema v2.
    /// </summary>
    public List<Guid> PersonIds { get; set; } = new();

    /// <summary>
    /// Captures platform-specific fields for round-trip preservation.
    /// Self-hosted code MUST NOT read or modify this data.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Portable knowledge item. Matches Knowz.Core.Entities.Knowledge
/// plus relationship ID lists for junction tables.
/// </summary>
public class PortableKnowledge
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? Summary { get; set; }
    /// <summary>2–3 sentence retrieval context. Dual-edition Core field (v2.1 additive).</summary>
    public string? BriefSummary { get; set; }
    public KnowledgeType Type { get; set; } = KnowledgeType.Note;
    public string? Source { get; set; }
    public string? FilePath { get; set; }
    public bool IsIndexed { get; set; }
    public DateTime? IndexedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Organization
    public Guid? TopicId { get; set; }

    // Relationships (flattened from junction tables)
    public List<Guid> VaultIds { get; set; } = new();
    public Guid? PrimaryVaultId { get; set; }
    public List<Guid> TagIds { get; set; } = new();

    /// <summary>
    /// Flat person ID list (v1 legacy). Still read on import for backward compatibility.
    /// Prefer PersonLinks when available (v2+).
    /// </summary>
    public List<Guid> PersonIds { get; set; } = new();
    public List<Guid> LocationIds { get; set; } = new();
    public List<Guid> EventIds { get; set; } = new();

    /// <summary>
    /// Rich person-knowledge links with context (v2+).
    /// When present, takes precedence over PersonIds for junction creation.
    /// </summary>
    public List<PortableEntityLink>? PersonLinks { get; set; }

    /// <summary>
    /// Rich location-knowledge links with context (v2+).
    /// When present, takes precedence over LocationIds.
    /// </summary>
    public List<PortableEntityLink>? LocationLinks { get; set; }

    /// <summary>
    /// Rich event-knowledge links with context (v2+).
    /// When present, takes precedence over EventIds.
    /// </summary>
    public List<PortableEntityLink>? EventLinks { get; set; }

    /// <summary>
    /// Commit-history-scoped KnowledgeRelationship payload entries (NODE-5).
    /// Populated ONLY for Knowledge items of Type = CommitHistory or Commit
    /// when the producer's commit-relationship sync feature flag is enabled.
    /// Null on older exporters (backwards compatible via nullability).
    /// Consumers resolve source/target across instances by (Source string) and
    /// (VaultId, FilePath) — NOT by Guid, which does not match across instances.
    /// See knowzcode/specs/SVC_PlatformSyncRelationshipPayload.md
    /// </summary>
    public List<PortableKnowledgeRelationship>? Relationships { get; set; }

    /// <summary>
    /// Captures platform-specific fields (e.g., ContentHash, Metadata, PrimaryDate,
    /// TemporalAnchorAt, SensitivityLevel, AI processing fields, perspective attribution,
    /// enrichment tracking, etc.) for round-trip preservation.
    /// Self-hosted code MUST NOT read or modify this data.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Commit-history-scoped relationship payload entry (NODE-5, SVC_PlatformSyncRelationshipPayload).
/// Represents a <c>KnowledgeRelationship</c> originating from a <c>Knowledge</c> of
/// <c>Type = CommitHistory</c> or <c>Type = Commit</c>.
///
/// <para>
/// Cross-instance identity rule: Knowledge Guids do NOT match across a platform↔selfhosted
/// boundary. Both source and target must be re-resolved on the receiver via stable strings:
/// </para>
/// <list type="bullet">
///   <item><description><c>SourceCommitSha</c> — resolves the commit-child source via
///     <c>Knowledge.Source = "{repoUrl}:{branch}:commit:{sha}"</c>.</description></item>
///   <item><description><c>SourceFilePath</c> — reserved for future source-by-FilePath
///     resolution (currently unused; always null for commit-scoped payloads).</description></item>
///   <item><description><c>TargetFilePath</c> — resolves the target Knowledge via
///     <c>(VaultId, FilePath)</c> lookup. For <c>References</c> edges pointing at
///     touched-file Knowledge rows.</description></item>
/// </list>
///
/// <para>
/// Unresolvable targets are dropped by the consumer and the missing path is appended
/// to the commit child's <c>Metadata.unlinkedFiles</c> JSON array (matching the
/// local-ingestion orphan behavior).
/// </para>
/// </summary>
public class PortableKnowledgeRelationship
{
    /// <summary>
    /// Commit SHA identifying the commit-child source Knowledge.
    /// Set when the source is a <c>Knowledge { Type = Commit }</c>.
    /// Receiver resolves via <c>Knowledge.Source = "{repoUrl}:{branch}:commit:{sha}"</c>.
    /// </summary>
    public string? SourceCommitSha { get; set; }

    /// <summary>
    /// Reserved for future source-by-FilePath resolution. Currently unused.
    /// </summary>
    public string? SourceFilePath { get; set; }

    /// <summary>
    /// Target file path (e.g., "src/Foo.cs"). Resolved on the receiver by
    /// <c>(VaultId, FilePath)</c> lookup. If the file does not exist on the receiver,
    /// the relationship is dropped and the path is appended to the commit child's
    /// <c>Metadata.unlinkedFiles</c> JSON array.
    /// Non-null for <c>References</c> edges. Null for <c>PartOf</c> parent edges (the
    /// CommitHistory parent is resolved via its own Source string).
    /// </summary>
    public string? TargetFilePath { get; set; }

    /// <summary>
    /// Relationship type — only <see cref="KnowledgeRelationshipType.PartOf"/> and
    /// <see cref="KnowledgeRelationshipType.References"/> are carried by this payload.
    /// Other relationship types are out of scope for NODE-5.
    /// </summary>
    public KnowledgeRelationshipType RelationshipType { get; set; }

    /// <summary>
    /// Optional passthrough metadata from the source <c>KnowledgeRelationship.Metadata</c>
    /// field (serialized as a dictionary for future-proofing).
    /// </summary>
    public Dictionary<string, JsonElement>? Metadata { get; set; }
}

/// <summary>
/// Rich entity link with relationship metadata (v2+).
/// Used for PersonLinks, LocationLinks, EventLinks on PortableKnowledge.
/// </summary>
public class PortableEntityLink
{
    public Guid EntityId { get; set; }
    public string? RelationshipContext { get; set; }
    public string? Role { get; set; }
    public int Mentions { get; set; }
    public double? ConfidenceScore { get; set; }
}

/// <summary>
/// Portable knowledge comment (v2+). Supports threaded hierarchy.
/// </summary>
public class PortableKnowledgeComment
{
    public Guid Id { get; set; }
    public Guid KnowledgeId { get; set; }
    public Guid? ParentCommentId { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsAnswer { get; set; }
    public string? Sentiment { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Portable file record (v2+). Metadata only — blob files are not migrated.
/// </summary>
public class PortableFileRecord
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long SizeBytes { get; set; }
    public string? BlobUri { get; set; }
    public string? TranscriptionText { get; set; }
    public string? ExtractedText { get; set; }
    public string? VisionDescription { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// File attachment links: knowledge ID and/or comment ID this file is attached to.
    /// </summary>
    public List<PortableFileAttachmentLink> Attachments { get; set; } = new();

    /// <summary>
    /// Binary file content encoded as Base64 string (OPTIONAL).
    /// Populated during export when IncludeBinaryContent=true AND file size &lt;= MaxBinaryFileSizeMB.
    /// Null for large files or when binary export is disabled.
    /// Backward compatible: old exports without this field still work.
    /// </summary>
    public string? BinaryContentBase64 { get; set; }

    /// <summary>
    /// Relative file path within a ZIP archive (Full mode exports).
    /// Example: "files/{fileRecordId}.pdf"
    /// Mutually exclusive with BinaryContentBase64. When BinaryFilePath is set,
    /// the binary content is stored as a separate file in the ZIP archive.
    /// </summary>
    public string? BinaryFilePath { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Link between a file and a knowledge item or comment.
/// </summary>
public class PortableFileAttachmentLink
{
    public Guid? KnowledgeId { get; set; }
    public Guid? CommentId { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
}

/// <summary>
/// Portable topic. Matches Knowz.Core.Entities.Topic.
/// </summary>
public class PortableTopic
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Portable tag. Matches Knowz.Core.Entities.Tag.
/// </summary>
public class PortableTag
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Portable person. Matches Knowz.Core.Entities.Person.
/// </summary>
public class PortablePerson
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Captures platform-specific fields (e.g., Email, Phone, Biography, Skills,
    /// Interests, LinkedInUrl, BirthDate, AliasesJson, FriendlyName, etc.)
    /// for round-trip preservation.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Portable location. Matches Knowz.Core.Entities.Location.
/// </summary>
public class PortableLocation
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Captures platform-specific fields (e.g., Address, City, State, Country,
    /// Latitude, Longitude, AltNames) for round-trip preservation.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Portable event. Matches Knowz.Core.Entities.Event.
/// </summary>
public class PortableEvent
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Captures platform-specific fields (e.g., Description, StartDate, EndDate,
    /// LocationId) for round-trip preservation.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Portable inbox item. Matches Knowz.Core.Entities.InboxItem.
/// </summary>
public class PortableInboxItem
{
    public Guid Id { get; set; }
    public string Body { get; set; } = string.Empty;
    public InboxItemType Type { get; set; } = InboxItemType.Note;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<PortableFileAttachmentLink> Attachments { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
