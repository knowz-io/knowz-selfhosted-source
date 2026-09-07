namespace Knowz.SelfHosted.Application.Interfaces;

using Knowz.SelfHosted.Application.DTOs;

/// <summary>
/// Append-only audit log for every platform sync operation (connect/disconnect/test/browse/pull/push).
/// Rows are INSERTed on start and UPDATEd exactly once on completion; after completion they are immutable.
///
/// Error messages are sanitized before storage — callers can pass raw exception text to
/// <see cref="FailAsync"/> and rely on the implementation to strip API keys and truncate.
/// </summary>
public interface IPlatformAuditLog
{
    /// <summary>
    /// Insert an in-progress row and return its id. The caller is expected to pair this with
    /// <see cref="CompleteAsync"/> or <see cref="FailAsync"/> in a finally block.
    /// </summary>
    Task<Guid> StartAsync(PlatformSyncRunStart start, CancellationToken ct = default);

    /// <summary>
    /// Mark a run as successful (or partial) and record final counts.
    /// </summary>
    Task CompleteAsync(Guid runId, PlatformSyncRunResult result, CancellationToken ct = default);

    /// <summary>
    /// Mark a run as failed. <paramref name="errorMessage"/> is sanitized before insert —
    /// API keys and URLs with basic-auth credentials are stripped, output is truncated to 500 chars.
    /// </summary>
    Task FailAsync(Guid runId, string errorMessage, CancellationToken ct = default);

    /// <summary>
    /// Return the history for the current tenant, ordered StartedAt DESC.
    /// <paramref name="page"/> is 1-based; <paramref name="pageSize"/> is capped at 500.
    /// When <paramref name="vaultSyncLinkId"/> is supplied, rows are filtered to that link.
    /// </summary>
    Task<IReadOnlyList<PlatformSyncRunDto>> GetHistoryAsync(
        Guid tenantId,
        int page,
        int pageSize,
        CancellationToken ct = default,
        Guid? vaultSyncLinkId = null);

    /// <summary>
    /// Count rows for the tenant where <c>StartedAt &gt;= UtcNow - window</c>.
    /// Used by the rate limiter in Node 3 (PlatformSyncItemOps).
    /// </summary>
    Task<int> CountRecentAsync(Guid tenantId, TimeSpan window, CancellationToken ct = default);

    /// <summary>
    /// Count rows for the tenant with <see cref="Knowz.SelfHosted.Infrastructure.Data.Entities.PlatformSyncRunStatus.InProgress"/>.
    /// Used by concurrency guards to block a second run from starting.
    /// </summary>
    Task<int> CountInProgressAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Convenience wrapper for atomic operations (connect/disconnect/test/browse) that do not
    /// need a distinct start/complete window. Internally calls <see cref="StartAsync"/> followed
    /// by <see cref="CompleteAsync"/> (or <see cref="FailAsync"/>) in a single call.
    /// <paramref name="errorMessage"/> is sanitized when <paramref name="status"/> is
    /// <see cref="Knowz.SelfHosted.Infrastructure.Data.Entities.PlatformSyncRunStatus.Failed"/>.
    /// </summary>
    Task LogAsync(
        PlatformSyncRunStart start,
        Knowz.SelfHosted.Infrastructure.Data.Entities.PlatformSyncRunStatus status,
        string? errorMessage,
        CancellationToken ct = default);
}
