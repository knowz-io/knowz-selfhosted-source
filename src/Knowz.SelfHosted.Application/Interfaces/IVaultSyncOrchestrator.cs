namespace Knowz.SelfHosted.Application.Interfaces;

using Knowz.SelfHosted.Application.DTOs;

/// <summary>
/// Orchestrates bidirectional vault sync between selfhosted and platform.
/// </summary>
public interface IVaultSyncOrchestrator
{
    /// <summary>
    /// Execute a sync operation for a linked vault.
    /// </summary>
    Task<VaultSyncResult> SyncAsync(Guid localVaultId, SyncDirection direction = SyncDirection.Full, CancellationToken ct = default);

    /// <summary>
    /// Get the sync status for a linked vault.
    /// </summary>
    Task<VaultSyncStatusDto?> GetStatusAsync(Guid localVaultId, CancellationToken ct = default);

    /// <summary>
    /// List all vault sync links.
    /// </summary>
    Task<List<VaultSyncStatusDto>> ListLinksAsync(CancellationToken ct = default);

    /// <summary>
    /// Establish a new sync link between a local vault and a platform vault.
    /// </summary>
    Task<VaultSyncStatusDto> EstablishLinkAsync(EstablishSyncLinkRequest request, CancellationToken ct = default);

    /// <summary>
    /// Remove a sync link.
    /// </summary>
    Task<bool> RemoveLinkAsync(Guid localVaultId, CancellationToken ct = default);

    /// <summary>
    /// Pull or push a single knowledge item. Enforces the shared rate limiter (V-SEC-09)
    /// and the conflict strategy rules (V-SEC-11): Pull defaults to Skip when the local
    /// item exists; Overwrite must be opt-in via <paramref name="overwriteLocal"/>.
    /// </summary>
    /// <param name="vaultSyncLinkId">Existing VaultSyncLink row id (not the vault id).</param>
    /// <param name="knowledgeId">Remote id for Pull, or local id for Push. Must be a strict GUID (V-SEC-12).</param>
    /// <param name="direction">Pull (remote→local) or Push (local→remote).</param>
    /// <param name="overwriteLocal">Only honoured for Pull. Default false.</param>
    Task<SyncItemResult> SyncItemAsync(
        Guid vaultSyncLinkId,
        Guid knowledgeId,
        SyncItemDirection direction,
        bool overwriteLocal = false,
        CancellationToken ct = default);
}
