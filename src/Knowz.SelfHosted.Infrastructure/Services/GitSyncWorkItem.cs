namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// In-memory work item for the git sync Channel.
/// Lightweight -- only carries IDs. The service loads
/// full entity from DB when processing.
/// </summary>
public record GitSyncWorkItem(Guid VaultId, Guid TenantId);
