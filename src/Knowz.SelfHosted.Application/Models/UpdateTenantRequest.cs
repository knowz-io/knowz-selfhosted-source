using System.ComponentModel.DataAnnotations;

namespace Knowz.SelfHosted.Application.Models;

/// <summary>
/// Request model for updating an existing tenant.
/// </summary>
public class UpdateTenantRequest
{
    [MaxLength(200)]
    public string? Name { get; set; }

    [MaxLength(100)]
    public string? Slug { get; set; }

    [MaxLength(1000)]
    public string? Description { get; set; }

    public bool? IsActive { get; set; }
}
