namespace Knowz.Core.Portability;

using System.Text.Json;
using Knowz.Core.Schema;

/// <summary>
/// Root envelope for portable data export/import between platform and self-hosted.
/// </summary>
public class PortableExportPackage
{
    /// <summary>
    /// Schema version of the entities in this package.
    /// Set to CoreSchema.Version on export. Checked via CoreSchema.CanRead() on import.
    /// </summary>
    public int SchemaVersion { get; set; } = CoreSchema.Version;

    /// <summary>
    /// Source system identifier: "platform" or "selfhosted".
    /// </summary>
    public string SourceEdition { get; set; } = string.Empty;

    /// <summary>
    /// Tenant ID from the source system. Used for provenance, NOT for import scoping.
    /// Import always scopes to the target tenant.
    /// </summary>
    public Guid SourceTenantId { get; set; }

    /// <summary>
    /// UTC timestamp when export was generated.
    /// </summary>
    public DateTime ExportedAt { get; set; }

    /// <summary>
    /// Summary counts for quick validation before full import.
    /// </summary>
    public PortableExportMetadata Metadata { get; set; } = new();

    /// <summary>
    /// The exported data collections.
    /// </summary>
    public PortableExportData Data { get; set; } = new();

    // --- Platform export metadata (nullable for backward compatibility) ---

    /// <summary>
    /// Describes what was included in this export (scope type, vault/item IDs).
    /// Null for legacy exports, VaultSync delta packages, and self-hosted exports.
    /// </summary>
    public ExportScope? Scope { get; set; }

    /// <summary>
    /// Whether this export includes binary file content (Full) or metadata only (Light).
    /// </summary>
    public ExportMode Mode { get; set; } = ExportMode.Light;

    // --- Sync extensions (nullable for backward compatibility) ---

    /// <summary>
    /// Server timestamp cursor for incremental sync. Set by the exporting side
    /// so the importer can use it as the "since" value for the next pull.
    /// </summary>
    public DateTime? SyncCursor { get; set; }

    /// <summary>
    /// Whether this package contains only changes since the last sync (delta),
    /// as opposed to a full snapshot export.
    /// </summary>
    public bool IsIncrementalSync { get; set; }

    /// <summary>
    /// Tombstones for entities that were soft-deleted since the last sync.
    /// Present only during incremental sync operations.
    /// </summary>
    public List<SyncTombstoneDto>? Tombstones { get; set; }
}

public class PortableExportMetadata
{
    public int TotalVaults { get; set; }
    public int TotalKnowledgeItems { get; set; }
    public int TotalTopics { get; set; }
    public int TotalTags { get; set; }
    public int TotalPersons { get; set; }
    public int TotalLocations { get; set; }
    public int TotalEvents { get; set; }
    public int TotalInboxItems { get; set; }
    // v2 additions
    public int TotalComments { get; set; }
    public int TotalFileRecords { get; set; }
    public int TotalArchiveTypes { get; set; }
}

public class PortableExportData
{
    public List<PortableVault> Vaults { get; set; } = new();
    public List<PortableKnowledge> KnowledgeItems { get; set; } = new();
    public List<PortableTopic> Topics { get; set; } = new();
    public List<PortableTag> Tags { get; set; } = new();
    public List<PortablePerson> Persons { get; set; } = new();
    public List<PortableLocation> Locations { get; set; } = new();
    public List<PortableEvent> Events { get; set; } = new();
    public List<PortableInboxItem> InboxItems { get; set; } = new();

    // v2 additions
    public List<PortableKnowledgeComment> Comments { get; set; } = new();
    public List<PortableFileRecord> FileRecords { get; set; } = new();

    /// <summary>
    /// Opaque archived entities keyed by entity type name.
    /// Used for platform-specific entity types that self-hosted preserves but doesn't query.
    /// </summary>
    public Dictionary<string, List<JsonElement>> Archives { get; set; } = new();
}
