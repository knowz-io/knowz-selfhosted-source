namespace Knowz.Core.Enums;

/// <summary>
/// Vault categorization type shared between platform and self-hosted deployments.
/// Ordinals are identical across both codebases; no data migration required.
/// </summary>
public enum VaultType
{
    /// <summary>General-purpose knowledge vault.</summary>
    GeneralKnowledge = 0,

    /// <summary>Business-related knowledge vault.</summary>
    Business = 1,

    /// <summary>Product documentation vault.</summary>
    Product = 2,

    /// <summary>Source code and repository vault.</summary>
    CodeBase = 3,

    /// <summary>Daily diary / journaling vault.</summary>
    DailyDiary = 4,

    /// <summary>Q&amp;A engagement vault.</summary>
    QuestionAnswer = 5,

    /// <summary>Vault bound to a specific person.</summary>
    PersonBound = 6,

    /// <summary>Vault bound to a specific location.</summary>
    LocationBound = 7
}
