namespace Knowz.SelfHosted.Application.Services;

using System.Collections.Concurrent;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
using Microsoft.Extensions.Logging;

/// <summary>
/// In-memory sliding-window rate limiter for platform sync operations.
///
/// Enforces V-SEC-09:
/// - 10 sync ops per tenant per rolling hour
/// - 1 concurrent sync per tenant
/// - 100 items max per run
///
/// TODO: migrate to PlatformSyncRun table after Node 4 lands. For MVP the selfhosted API is
/// single-replica so in-memory is sufficient. See knowzcode/specs/PlatformSyncItemOps.md.
/// </summary>
public class PlatformSyncRateLimiter : IPlatformSyncRateLimiter
{
    public const int MaxItemsPerRun = 100;
    public const int MaxRunsPerHour = 10;
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private static readonly TimeSpan ConcurrentRunStale = TimeSpan.FromMinutes(30);

    private readonly ILogger<PlatformSyncRateLimiter> _logger;
    private readonly ConcurrentDictionary<Guid, TenantRateState> _stateByTenant = new();
    private readonly ConcurrentDictionary<Guid, InFlight> _inFlight = new();

    public PlatformSyncRateLimiter(ILogger<PlatformSyncRateLimiter> logger)
    {
        _logger = logger;
    }

    public Task<RateLimitDecision> CheckAsync(Guid tenantId, int itemCount, CancellationToken ct = default)
    {
        if (itemCount > MaxItemsPerRun)
        {
            return Task.FromResult(new RateLimitDecision(
                Allowed: false,
                Reason: RateLimitReason.ItemLimitExceeded,
                RetryAfter: null));
        }

        var state = _stateByTenant.GetOrAdd(tenantId, _ => new TenantRateState());
        var now = DateTime.UtcNow;

        lock (state.SyncRoot)
        {
            state.PruneOlderThan(now - Window);

            var hasActiveRun = false;
            TimeSpan? retryAfterForConcurrent = null;
            foreach (var entry in _inFlight.Values)
            {
                if (entry.TenantId != tenantId) continue;
                if (now - entry.StartedAt > ConcurrentRunStale) continue;
                hasActiveRun = true;
                var runningFor = now - entry.StartedAt;
                var remaining = ConcurrentRunStale - runningFor;
                if (retryAfterForConcurrent == null || remaining < retryAfterForConcurrent)
                    retryAfterForConcurrent = remaining;
            }

            if (hasActiveRun)
            {
                return Task.FromResult(new RateLimitDecision(
                    Allowed: false,
                    Reason: RateLimitReason.ConcurrentRunInProgress,
                    RetryAfter: retryAfterForConcurrent ?? TimeSpan.FromSeconds(30)));
            }

            if (state.RecentRuns.Count >= MaxRunsPerHour)
            {
                var oldest = state.RecentRuns.Peek();
                var retryAfter = (oldest + Window) - now;
                if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.FromSeconds(1);
                return Task.FromResult(new RateLimitDecision(
                    Allowed: false,
                    Reason: RateLimitReason.HourlyQuotaExceeded,
                    RetryAfter: retryAfter));
            }
        }

        return Task.FromResult(new RateLimitDecision(Allowed: true, Reason: null, RetryAfter: null));
    }

    public Task<Guid> RecordOperationAsync(Guid tenantId, string operation, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var state = _stateByTenant.GetOrAdd(tenantId, _ => new TenantRateState());

        lock (state.SyncRoot)
        {
            state.PruneOlderThan(now - Window);
            state.RecentRuns.Enqueue(now);
        }

        var opId = Guid.NewGuid();
        _inFlight[opId] = new InFlight(tenantId, operation, now);
        _logger.LogDebug(
            "Rate limiter recorded operation {OperationId} ({Operation}) for tenant {TenantId}",
            opId, operation, tenantId);
        return Task.FromResult(opId);
    }

    public Task CompleteOperationAsync(Guid operationId, CancellationToken ct = default)
    {
        _inFlight.TryRemove(operationId, out _);
        return Task.CompletedTask;
    }

    private sealed class TenantRateState
    {
        public object SyncRoot { get; } = new();
        public Queue<DateTime> RecentRuns { get; } = new();

        public void PruneOlderThan(DateTime cutoff)
        {
            while (RecentRuns.Count > 0 && RecentRuns.Peek() < cutoff)
                RecentRuns.Dequeue();
        }
    }

    private sealed record InFlight(Guid TenantId, string Operation, DateTime StartedAt);
}
