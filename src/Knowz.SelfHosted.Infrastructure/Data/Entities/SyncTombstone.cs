namespace Knowz.SelfHosted.Infrastructure.Data.Entities;

/// <summary>
/// Tracks soft-deleted entities in synced vaults that need to be propagated to the remote side.
/// Created when an entity in a synced vault is soft-deleted locally.
/// </summary>
public class SyncTombstone
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The sync link this tombstone belongs to.
    /// </summary>
    public Guid VaultSyncLinkId { get; set; }

    /// <summary>
    /// The entity type name (e.g., "Knowledge", "Tag", "Person").
    /// </summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>
    /// The local entity ID that was deleted.
    /// </summary>
    public Guid LocalEntityId { get; set; }

    /// <summary>
    /// The remote entity ID (if known from a previous sync). Null for entities never pushed.
    /// </summary>
    public Guid? RemoteEntityId { get; set; }

    /// <summary>
    /// When the entity was deleted locally.
    /// </summary>
    public DateTime DeletedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether this tombstone has been propagated to the remote side.
    /// </summary>
    public bool Propagated { get; set; }

    /// <summary>
    /// When the tombstone was propagated to the remote side.
    /// </summary>
    public DateTime? PropagatedAt { get; set; }

    // Navigation
    public virtual VaultSyncLink? VaultSyncLink { get; set; }
}
