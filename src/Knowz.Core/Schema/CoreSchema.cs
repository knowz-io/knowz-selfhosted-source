namespace Knowz.Core.Schema;

/// <summary>
/// Canonical schema version for all Knowz.Core entities.
/// Used to gate import compatibility for portable export packages.
/// </summary>
public static class CoreSchema
{
    /// <summary>
    /// Current schema version. Increment when entity shapes change.
    /// </summary>
    public const int Version = 2;

    /// <summary>
    /// Minimum schema version this build can read.
    /// Advance when breaking changes make older formats unreadable.
    /// </summary>
    public const int MinReadableVersion = 1;

    /// <summary>
    /// Returns true if this build can read data exported at the given schema version.
    /// </summary>
    public static bool CanRead(int exportedVersion)
        => exportedVersion >= MinReadableVersion && exportedVersion <= Version;

    /// <summary>
    /// Returns a human-readable compatibility summary for diagnostics and error messages.
    /// Example: "Current: 3, Readable: 2-3"
    /// </summary>
    public static string GetCompatibilityInfo()
        => MinReadableVersion == Version
            ? $"Schema v{Version}"
            : $"Schema v{Version} (reads v{MinReadableVersion}-v{Version})";
}
