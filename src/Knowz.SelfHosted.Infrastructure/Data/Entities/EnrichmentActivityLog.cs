namespace Knowz.SelfHosted.Infrastructure.Data.Entities;

/// <summary>
/// One row per enrichment attempt — written by <c>EnrichmentBackgroundService</c>
/// so operators can correlate "my file never got a summary" with the exact
/// attempt that failed, its error, and timing.
///
/// SH_ENTERPRISE_RUNTIME_RESILIENCE §2.5. Queried by the admin outbox endpoint
/// and the post-deploy smoke script.
///
/// Tenant-scoped via SelfHostedDbContext query filter.
/// </summary>
public class EnrichmentActivityLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid KnowledgeId { get; set; }
    public int AttemptNumber { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public EnrichmentStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
}
