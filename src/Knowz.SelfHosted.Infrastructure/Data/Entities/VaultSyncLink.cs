namespace Knowz.SelfHosted.Infrastructure.Data.Entities;

/// <summary>
/// Tracks the binding between a local vault and a remote platform vault for bidirectional sync.
/// </summary>
public class VaultSyncLink
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The local vault ID (FK → Vaults). One sync link per local vault.
    /// </summary>
    public Guid LocalVaultId { get; set; }

    /// <summary>
    /// The remote vault ID on the platform side.
    /// </summary>
    public Guid RemoteVaultId { get; set; }

    /// <summary>
    /// The remote tenant ID on the platform side.
    /// </summary>
    public Guid RemoteTenantId { get; set; }

    /// <summary>
    /// FK → PlatformConnection. Per-tenant credential store that holds the encrypted
    /// API key and platform URL. Nullable during data-copy migration for legacy rows;
    /// populated for all new links.
    /// </summary>
    public Guid? PlatformConnectionId { get; set; }

    /// <summary>
    /// Legacy: platform API base URL. Moved to <c>PlatformConnection.PlatformApiUrl</c>.
    /// Retained for backwards compatibility during the data-copy migration window.
    /// </summary>
    [Obsolete("Use PlatformConnection.PlatformApiUrl via PlatformConnectionId. Removed in a future migration.")]
    public string PlatformApiUrl { get; set; } = string.Empty;

    /// <summary>
    /// Legacy: plaintext API key stored despite the column name. Moved to
    /// <c>PlatformConnection.ApiKeyProtected</c> (DataProtection ciphertext).
    /// Retained for backwards compatibility during the data-copy migration window.
    /// </summary>
    [Obsolete("Use PlatformConnection.ApiKeyProtected via PlatformConnectionId. Removed in a future migration.")]
    public string ApiKeyEncrypted { get; set; } = string.Empty;

    /// <summary>
    /// When the last successful sync completed.
    /// </summary>
    public DateTime? LastSyncCompletedAt { get; set; }

    /// <summary>
    /// Remote UpdatedAt watermark — entities with UpdatedAt > this value are pulled.
    /// Uses the platform's serverTimestamp to avoid clock skew.
    /// </summary>
    public DateTime? LastPullCursor { get; set; }

    /// <summary>
    /// Local UpdatedAt watermark — entities with UpdatedAt > this value are pushed.
    /// </summary>
    public DateTime? LastPushCursor { get; set; }

    /// <summary>
    /// Current sync status.
    /// </summary>
    public VaultSyncStatus LastSyncStatus { get; set; } = VaultSyncStatus.NotSynced;

    /// <summary>
    /// Error message from the last failed sync attempt.
    /// </summary>
    public string? LastSyncError { get; set; }

    /// <summary>
    /// Whether sync is enabled for this link.
    /// </summary>
    public bool SyncEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum VaultSyncStatus
{
    NotSynced = 0,
    InProgress = 1,
    Succeeded = 2,
    Failed = 3
}
