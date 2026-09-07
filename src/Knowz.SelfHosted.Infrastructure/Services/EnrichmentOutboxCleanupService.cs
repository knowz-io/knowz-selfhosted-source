using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// Cleans up completed/failed enrichment outbox items older than 30 days.
/// Runs once per day to prevent unbounded table growth.
/// </summary>
public class EnrichmentOutboxCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EnrichmentOutboxCleanupService> _logger;

    public EnrichmentOutboxCleanupService(
        IServiceScopeFactory scopeFactory,
        ILogger<EnrichmentOutboxCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait 1 hour after startup before first cleanup (let app stabilize)
        await Task.Delay(TimeSpan.FromHours(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deleted = await CleanupAsync(stoppingToken);

                if (deleted > 0)
                    _logger.LogInformation("Enrichment outbox cleanup: deleted {Count} terminal items older than 30 days", deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Enrichment outbox cleanup failed — will retry in 24 hours");
            }

            // Run daily
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    /// <summary>
    /// Deletes completed/failed outbox items with ProcessedAt older than 30 days.
    /// Returns the number of deleted items. Exposed as internal for testability.
    /// </summary>
    internal async Task<int> CleanupAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();

        var cutoff = DateTime.UtcNow.AddDays(-30);

        // EF Core InMemory provider doesn't support ExecuteDeleteAsync,
        // so use a compatible approach that works with both SQL and InMemory
        var itemsToDelete = await db.EnrichmentOutbox
            .Where(e => (e.Status == EnrichmentStatus.Completed || e.Status == EnrichmentStatus.Failed)
                     && e.ProcessedAt != null
                     && e.ProcessedAt < cutoff)
            .ToListAsync(ct);

        if (itemsToDelete.Count > 0)
        {
            db.EnrichmentOutbox.RemoveRange(itemsToDelete);
            await db.SaveChangesAsync(ct);
        }

        return itemsToDelete.Count;
    }
}
