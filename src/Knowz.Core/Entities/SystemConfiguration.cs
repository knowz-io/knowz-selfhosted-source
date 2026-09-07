using System.ComponentModel.DataAnnotations;

namespace Knowz.Core.Entities;

/// <summary>
/// Represents a system-wide configuration entry in the self-hosted deployment.
/// NOT tenant-scoped - this is an admin-level entity with no query filter.
/// Does NOT implement ISelfHostedEntity (no TenantId, IsDeleted, PlatformData).
/// </summary>
public class SystemConfiguration
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(100)]
    public string Category { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Key { get; set; } = string.Empty;

    public string? EncryptedValue { get; set; }

    public bool IsSecret { get; set; }

    public bool RequiresRestart { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    public DateTime LastModifiedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(100)]
    public string? LastModifiedBy { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
