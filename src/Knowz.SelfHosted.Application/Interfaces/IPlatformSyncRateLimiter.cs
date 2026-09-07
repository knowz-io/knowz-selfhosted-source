namespace Knowz.SelfHosted.Application.Interfaces;

using Knowz.SelfHosted.Application.DTOs;

/// <summary>
/// Sliding-window rate limiter for platform sync operations.
/// Enforces: 10 runs/tenant/hour, 1 concurrent run/tenant, 100 items per run (V-SEC-09).
///
/// Backs onto the PlatformSyncRun table (owned by Node 4 PlatformSyncHistory) when
/// IPlatformAuditLog is present. Falls back to an in-memory counter otherwise.
/// </summary>
public interface IPlatformSyncRateLimiter
{
    /// <summary>
    /// Check whether a new sync operation may proceed for the given tenant.
    /// </summary>
    Task<RateLimitDecision> CheckAsync(Guid tenantId, int itemCount, CancellationToken ct = default);

    /// <summary>
    /// Record that an operation started so future CheckAsync calls can see it.
    /// Returns an opaque operation id that must be passed to <see cref="CompleteOperationAsync"/>.
    /// </summary>
    Task<Guid> RecordOperationAsync(Guid tenantId, string operation, CancellationToken ct = default);

    /// <summary>
    /// Mark a previously-recorded operation as complete so it no longer counts toward concurrency.
    /// </summary>
    Task CompleteOperationAsync(Guid operationId, CancellationToken ct = default);
}
