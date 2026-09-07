using System.ComponentModel.DataAnnotations;

namespace Knowz.Core.Entities;

/// <summary>
/// Represents a tenant in the self-hosted deployment.
/// NOT scoped by ISelfHostedEntity query filters - this is an admin-level entity.
/// </summary>
public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string Slug { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public virtual ICollection<User> Users { get; set; } = new List<User>();
}
