namespace Knowz.Core.Enums;

/// <summary>
/// Type of item submitted to the inbox for triage.
/// </summary>
public enum InboxItemType
{
    /// <summary>Free-form text note.</summary>
    Note = 0,

    /// <summary>Web link or URL.</summary>
    Link = 1,

    /// <summary>File attachment.</summary>
    File = 2
}
