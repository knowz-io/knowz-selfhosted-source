namespace Knowz.Core.Enums;

/// <summary>
/// Canonical knowledge item type shared between platform and self-hosted deployments.
/// Ordinals 0-7 match the platform's existing implicit ordering (production data).
/// Ordinals 8-11 are self-hosted media types appended after the platform values.
/// </summary>
public enum KnowledgeType
{
    /// <summary>Free-form text note.</summary>
    Note = 0,

    /// <summary>Source code or code snippet.</summary>
    Code = 1,

    /// <summary>Document (PDF, Word, etc.).</summary>
    Document = 2,

    /// <summary>Question and answer pair.</summary>
    QuestionAnswer = 3,

    /// <summary>Daily journal or diary entry.</summary>
    Journal = 4,

    /// <summary>Web link or URL bookmark.</summary>
    Link = 5,

    /// <summary>Generic file attachment.</summary>
    File = 6,

    /// <summary>Prompt template or AI instruction.</summary>
    Prompt = 7,

    /// <summary>Audio/video transcript.</summary>
    Transcript = 8,

    /// <summary>Image file (photo, screenshot, diagram).</summary>
    Image = 9,

    /// <summary>Video file.</summary>
    Video = 10,

    /// <summary>Audio file (podcast, recording).</summary>
    Audio = 11,

    /// <summary>
    /// Parent roll-up "commit history" view for a GitRepository/branch (NODE-4).
    /// One row per (GitRepositoryId, Branch) pair. Content is a rolling-window
    /// of delimited commit blocks; contains no per-commit source of truth.
    /// </summary>
    CommitHistory = 12,

    /// <summary>
    /// Immutable per-commit child row (NODE-4). Holds an AI-elaborated description
    /// of a single commit. Source format: "{repoUrl}:{branch}:commit:{sha}".
    /// </summary>
    Commit = 13
}
