namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// Interface for git sync operations. Defined in Infrastructure so the
/// GitSyncBackgroundService can resolve it without coupling to Application layer.
/// Implemented by GitSyncService in Application layer.
/// </summary>
public interface IGitSyncService
{
    Task ExecuteSyncAsync(Guid vaultId, CancellationToken ct);
}
