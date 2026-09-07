namespace Knowz.SelfHosted.Infrastructure.Data.Entities;

/// <summary>
/// Append-only audit row for platform sync operations.
/// Captures every connect/disconnect/browse/pull/push event for forensic review.
/// During a run the row is InProgress; on completion the same row is updated once
/// with outcome + counts, then treated as immutable.
/// Sensitive content (API keys, knowledge bodies, platform response bodies) is NEVER stored.
/// </summary>
public class PlatformSyncRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    /// <summary>
    /// FK to <see cref="VaultSyncLink"/> when this run targets a specific link.
    /// Null for connection-level ops (Connect/Disconnect/TestConnection) and top-level browse events.
    /// </summary>
    public Guid? VaultSyncLinkId { get; set; }

    /// <summary>
    /// The authenticated user who initiated the operation. Falls back to <see cref="Guid.Empty"/> for
    /// system / scheduled runs (not yet in MVP scope).
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Denormalized email for forensic querying — persisted at start time so deletion of the user
    /// does not erase the audit trail.
    /// </summary>
    public string? UserEmail { get; set; }

    public PlatformSyncOperation Operation { get; set; }

    public PlatformSyncDirection Direction { get; set; }

    /// <summary>
    /// For single-item operations (PullItem/PushItem) the local or remote knowledge id.
    /// </summary>
    public Guid? KnowledgeId { get; set; }

    /// <summary>
    /// Number of items accepted by the run. 0 for connection-level ops.
    /// </summary>
    public int ItemCount { get; set; }

    /// <summary>
    /// Approximate bytes transferred (sum of payload lengths seen by the orchestrator).
    /// </summary>
    public long BytesTransferred { get; set; }

    public PlatformSyncRunStatus Status { get; set; } = PlatformSyncRunStatus.InProgress;

    /// <summary>
    /// Sanitized error message — never a raw exception message, never a response body.
    /// Max 500 chars, API-key and basic-auth patterns redacted before insert.
    /// </summary>
    public string? ErrorMessage { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// The specific platform sync action being audited.
/// </summary>
public enum PlatformSyncOperation
{
    Connect = 0,
    Disconnect = 1,
    TestConnection = 2,
    BrowseVaults = 3,
    BrowseKnowledge = 4,
    PullItem = 5,
    PushItem = 6,
    PullVault = 7,
    PushVault = 8,
}

/// <summary>
/// Direction of data movement. <see cref="None"/> for connection-level / browse events.
/// </summary>
public enum PlatformSyncDirection
{
    None = 0,
    Pull = 1,
    Push = 2,
}

/// <summary>
/// Terminal (and in-flight) state of a platform sync run.
/// </summary>
public enum PlatformSyncRunStatus
{
    InProgress = 0,
    Succeeded = 1,
    Failed = 2,
    Partial = 3,
}
