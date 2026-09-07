using System.ComponentModel.DataAnnotations;

namespace Knowz.SelfHosted.Application.Models;

/// <summary>
/// Request model for creating a new tenant.
/// </summary>
public class CreateTenantRequest
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string Slug { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }
}
