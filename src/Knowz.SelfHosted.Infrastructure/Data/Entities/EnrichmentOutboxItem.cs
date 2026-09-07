namespace Knowz.SelfHosted.Infrastructure.Data.Entities;

public class EnrichmentOutboxItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid KnowledgeId { get; set; }
    public EnrichmentStatus Status { get; set; } = EnrichmentStatus.Pending;
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; } = 3;
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public DateTime? NextRetryAt { get; set; }

    /// <summary>
    /// SH_ENTERPRISE_RUNTIME_RESILIENCE §2.4: counter incremented at the start of
    /// each <c>ProcessWorkItemAsync</c> attempt. Distinct from <see cref="RetryCount"/>
    /// which only bumps on failure — this counts every *start*, so operators can
    /// surface "stuck" items whose attempts climbs without ever completing.
    /// </summary>
    public int AiProcessingAttempts { get; set; }

    /// <summary>
    /// Timestamp of the most recent <c>Processing</c> transition. Replaces the
    /// CreatedAt-based stuck-detection heuristic (Fix 6 TODO). Null when never
    /// started or after a <c>forceReset</c>.
    /// </summary>
    public DateTime? StartedProcessingAt { get; set; }
}

public enum EnrichmentStatus
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3
}
