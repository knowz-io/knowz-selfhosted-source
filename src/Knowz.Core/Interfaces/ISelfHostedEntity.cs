namespace Knowz.Core.Interfaces;

/// <summary>
/// Marker interface documenting the contract all self-hosted entities satisfy.
/// Used as the constraint for ISelfHostedRepository&lt;T&gt;.
/// Zero coupling to Knowz.Domain.BaseEntity.
/// </summary>
public interface ISelfHostedEntity
{
    Guid Id { get; set; }
    Guid TenantId { get; set; }
    bool IsDeleted { get; set; }
    DateTime CreatedAt { get; set; }
    DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Opaque JSON blob for preserving platform-specific entity data during cross-edition round-trips.
    /// Self-hosted code MUST NOT read, modify, or depend on this value.
    /// Populated by import service from portable DTO ExtensionData. Read by export service for re-export.
    /// </summary>
    string? PlatformData { get; set; }
}
