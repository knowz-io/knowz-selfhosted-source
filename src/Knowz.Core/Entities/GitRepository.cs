using Knowz.Core.Interfaces;

namespace Knowz.Core.Entities;

public class GitRepository : ISelfHostedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>
    /// The vault this git repository syncs into. One repo per vault.
    /// </summary>
    public Guid VaultId { get; set; }

    /// <summary>
    /// HTTPS clone URL for the repository.
    /// </summary>
    public string RepositoryUrl { get; set; } = string.Empty;

    /// <summary>
    /// Branch to sync from. Defaults to "main".
    /// </summary>
    public string Branch { get; set; } = "main";

    /// <summary>
    /// The commit SHA of the last successful sync.
    /// </summary>
    public string? LastSyncCommitSha { get; set; }

    /// <summary>
    /// When the last successful sync completed.
    /// </summary>
    public DateTime? LastSyncAt { get; set; }

    /// <summary>
    /// Current sync status: NotSynced, Syncing, Synced, Failed.
    /// </summary>
    public string Status { get; set; } = "NotSynced";

    /// <summary>
    /// JSON array of glob patterns for file inclusion (e.g. ["**/*.md","**/*.txt"]).
    /// Null means use defaults.
    /// </summary>
    public string? FilePatterns { get; set; }

    /// <summary>
    /// Error message from the last failed sync attempt.
    /// </summary>
    public string? ErrorMessage { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? PlatformData { get; set; }

    // Commit-history ingestion (NODE-4: SelfHostedCommitHistoryParity)
    // Mirrors Knowz.Domain.Entities.GitRepository fields for the selfhosted edition.
    // WorkGroupID: kc-feat-git-commit-knowledge-sync-20260410

    /// <summary>
    /// Opt-in feature flag for commit-history ingestion (default OFF).
    /// When flipped false→true on an existing repo, triggers a one-time bounded
    /// backfill on next sync. Normal incremental syncs walk new commits only.
    /// </summary>
    public bool TrackCommitHistory { get; set; } = false;

    /// <summary>
    /// Maximum commits to walk on first-enable backfill.
    /// Null ⇒ default 500. Hard ceiling 2000 enforced at service layer.
    /// </summary>
    public int? CommitHistoryDepth { get; set; }

    /// <summary>
    /// Checkpoint SHA of the last successfully ingested commit for commit-history.
    /// Advances independently of <see cref="LastSyncCommitSha"/> (which tracks file sync).
    /// Only advances after all commit children + relationships are persisted.
    /// </summary>
    public string? LastCommitHistorySyncSha { get; set; }
}
