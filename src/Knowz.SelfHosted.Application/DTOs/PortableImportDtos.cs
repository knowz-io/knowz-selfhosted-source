namespace Knowz.SelfHosted.Application.DTOs;

public enum ImportConflictStrategy
{
    /// <summary>Skip conflicting entities, keep existing.</summary>
    Skip,
    /// <summary>Replace existing entities with imported data.</summary>
    Overwrite,
    /// <summary>Fill missing fields from imported data, keep existing non-null values.</summary>
    Merge
}

public class ImportValidationResult
{
    public bool IsValid { get; set; }
    public int SchemaVersion { get; set; }
    public bool SchemaCompatible { get; set; }
    public string? SchemaError { get; set; }
    public string? SourceEdition { get; set; }

    // Counts from package metadata
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

    // Conflict detection
    public int ConflictingVaults { get; set; }
    public int ConflictingKnowledgeItems { get; set; }
    public int ConflictingPersons { get; set; }
    public int ConflictingLocations { get; set; }
    public int ConflictingEvents { get; set; }

    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

public class PortableImportResult
{
    public bool Success { get; set; }
    public ImportConflictStrategy StrategyUsed { get; set; }

    // Per-entity-type counters
    public EntityImportCounts Vaults { get; set; } = new();
    public EntityImportCounts KnowledgeItems { get; set; } = new();
    public EntityImportCounts Topics { get; set; } = new();
    public EntityImportCounts Tags { get; set; } = new();
    public EntityImportCounts Persons { get; set; } = new();
    public EntityImportCounts Locations { get; set; } = new();
    public EntityImportCounts Events { get; set; } = new();
    public EntityImportCounts InboxItems { get; set; } = new();
    // v2 additions
    public EntityImportCounts Comments { get; set; } = new();
    public EntityImportCounts FileRecords { get; set; } = new();
    public int ArchiveRecordsStored { get; set; }

    // Junction restoration
    public int JunctionsRestored { get; set; }

    public List<string> Warnings { get; set; } = new();
    public string? Error { get; set; }
    public TimeSpan Duration { get; set; }
}

public class EntityImportCounts
{
    public int Created { get; set; }
    public int Skipped { get; set; }
    public int Overwritten { get; set; }
    public int Merged { get; set; }
    public int Total => Created + Skipped + Overwritten + Merged;
}
