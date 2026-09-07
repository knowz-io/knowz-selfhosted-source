namespace Knowz.SelfHosted.Application.DTOs;

/// <summary>
/// Direction for vault sync operations.
/// </summary>
public enum SyncDirection
{
    Full = 0,
    PullOnly = 1,
    PushOnly = 2
}

/// <summary>
/// Result from a vault sync operation.
/// </summary>
public class VaultSyncResult
{
    public bool Success { get; set; }
    public SyncDirection Direction { get; set; }
    public int PullAccepted { get; set; }
    public int PullSkipped { get; set; }
    public int PushAccepted { get; set; }
    public int PushSkipped { get; set; }
    public int TombstonesApplied { get; set; }
    public List<string> Details { get; set; } = new();
    public string? Error { get; set; }
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// True when the run stopped because it hit the 100-item-per-run cap (V-SEC-09).
    /// Users should re-run to continue from the cursor.
    /// </summary>
    public bool Partial { get; set; }
}

/// <summary>
/// Status of a vault sync link.
/// </summary>
public class VaultSyncStatusDto
{
    public Guid LinkId { get; set; }
    public Guid LocalVaultId { get; set; }
    public string LocalVaultName { get; set; } = string.Empty;
    public Guid RemoteVaultId { get; set; }
    public string PlatformApiUrl { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? LastSyncError { get; set; }
    public DateTime? LastSyncCompletedAt { get; set; }
    public DateTime? LastPullCursor { get; set; }
    public DateTime? LastPushCursor { get; set; }
    public bool SyncEnabled { get; set; }
}

/// <summary>
/// Result from importing a sync delta (last-write-wins).
/// </summary>
public class SyncDeltaImportResult
{
    public bool Success { get; set; }
    public int Accepted { get; set; }
    public int Skipped { get; set; }
    public int TombstonesApplied { get; set; }
    public List<string> Details { get; set; } = new();
    public string? Error { get; set; }

    /// <summary>
    /// True when this pull/push stopped because the shared 100-item run cap (V-SEC-09)
    /// was reached. The caller uses this to surface a Partial status and preserve cursors.
    /// </summary>
    public bool HitItemCap { get; set; }
}

/// <summary>
/// Generic API response wrapper matching platform's ApiResponse{T} shape.
/// Used for deserializing platform sync API responses.
/// </summary>
public class PlatformApiResponse<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Platform sync import response (mirrors Knowz.Shared.DTOs.VaultSync.VaultSyncImportResponse).
/// </summary>
public class PlatformImportResponse
{
    public bool Success { get; set; }
    public int Accepted { get; set; }
    public int Skipped { get; set; }
    public List<string> Details { get; set; } = new();
    public DateTime ServerTimestamp { get; set; }
}

/// <summary>
/// Platform schema version response.
/// </summary>
public class PlatformSchemaResponse
{
    public int Version { get; set; }
    public int MinReadableVersion { get; set; }
    public string Compatibility { get; set; } = string.Empty;

    /// <summary>
    /// Remote platform tenant id — captured from the authenticated schema response
    /// and written to <c>PlatformConnection.RemoteTenantId</c> / <c>VaultSyncLink.RemoteTenantId</c>.
    /// </summary>
    public Guid TenantId { get; set; }
}

/// <summary>
/// Request to establish a sync link.
/// </summary>
public class EstablishSyncLinkRequest
{
    public Guid LocalVaultId { get; set; }
    public Guid RemoteVaultId { get; set; }
    public string PlatformApiUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>
/// Request to trigger a sync operation.
/// </summary>
public class TriggerSyncRequest
{
    public SyncDirection Direction { get; set; } = SyncDirection.Full;
    public bool DryRun { get; set; }
}

// ----------------------------------------------------------------------------
// Single-item sync (NodeID: PlatformSyncItemOps) — V-SEC-09, V-SEC-11, V-SEC-12
// ----------------------------------------------------------------------------

/// <summary>
/// Direction for a single-item sync operation.
/// </summary>
public enum SyncItemDirection
{
    Pull = 0,
    Push = 1,
}

/// <summary>
/// Outcome of a single-item sync operation.
/// </summary>
public enum SyncItemOutcome
{
    Created,
    Updated,
    Skipped,
    Unchanged,
    Failed,
    RateLimited,
    PermissionDenied,
    NotFound,
}

/// <summary>
/// Body for POST /api/v1/sync/links/{linkId}/pull-item or push-item.
/// The OverwriteLocal flag is REQUIRED in the body (not a query string) per V-SEC-11.
/// </summary>
public class SyncItemRequest
{
    public Guid KnowledgeId { get; set; }
    public bool OverwriteLocal { get; set; }
}

/// <summary>
/// Result returned by IVaultSyncOrchestrator.SyncItemAsync.
/// </summary>
public class SyncItemResult
{
    public bool Success { get; set; }
    public SyncItemOutcome Outcome { get; set; }
    public Guid? LocalKnowledgeId { get; set; }
    public string? Message { get; set; }
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Sliding-window rate limit decision returned by IPlatformSyncRateLimiter.
/// </summary>
public record RateLimitDecision(bool Allowed, RateLimitReason? Reason, TimeSpan? RetryAfter);

public enum RateLimitReason
{
    HourlyQuotaExceeded,
    ItemLimitExceeded,
    ConcurrentRunInProgress,
}

/// <summary>
/// Thrown when a sync operation is rejected by the rate limiter.
/// The HTTP layer maps this to 429 with a Retry-After header.
/// </summary>
public class RateLimitExceededException : Exception
{
    public RateLimitReason Reason { get; }
    public TimeSpan? RetryAfter { get; }

    public RateLimitExceededException(RateLimitReason reason, TimeSpan? retryAfter, string message)
        : base(message)
    {
        Reason = reason;
        RetryAfter = retryAfter;
    }
}
