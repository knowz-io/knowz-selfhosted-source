using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// Background service that processes git sync requests from a bounded channel.
/// Follows the same channel pattern as EnrichmentBackgroundService.
/// </summary>
public class GitSyncBackgroundService : BackgroundService
{
    private readonly Channel<GitSyncWorkItem> _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GitSyncBackgroundService> _logger;

    public GitSyncBackgroundService(
        Channel<GitSyncWorkItem> channel,
        IServiceScopeFactory scopeFactory,
        ILogger<GitSyncBackgroundService> logger)
    {
        _channel = channel;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("GitSyncBackgroundService started");

        try
        {
            await foreach (var workItem in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    _logger.LogInformation("Processing git sync for VaultId {VaultId}", workItem.VaultId);
                    await ProcessWorkItemAsync(workItem, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Git sync failed for VaultId {VaultId}", workItem.VaultId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
    }

    internal async Task ProcessWorkItemAsync(GitSyncWorkItem workItem, CancellationToken ct)
    {
        TenantContext.CurrentTenantId = workItem.TenantId;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            // Resolve GitSyncService dynamically to avoid circular dependency
            // (GitSyncService is registered in Application layer, not Infrastructure)
            var gitSyncService = scope.ServiceProvider.GetRequiredService<IGitSyncService>();
            await gitSyncService.ExecuteSyncAsync(workItem.VaultId, ct);
        }
        finally
        {
            TenantContext.CurrentTenantId = null;
        }
    }
}
