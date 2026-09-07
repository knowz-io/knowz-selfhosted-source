namespace Knowz.Core.Portability;

/// <summary>
/// Represents a soft-deleted entity that needs to be propagated during sync.
/// </summary>
public class SyncTombstoneDto
{
    public string EntityType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public DateTime DeletedAt { get; set; }
}
