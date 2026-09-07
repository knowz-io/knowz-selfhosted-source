using System.ComponentModel.DataAnnotations;
using Knowz.Core.Enums;

namespace Knowz.SelfHosted.Application.Models;

/// <summary>
/// Request model for updating an existing user.
/// </summary>
public class UpdateUserRequest
{
    [MaxLength(255)]
    public string? Email { get; set; }

    [MaxLength(200)]
    public string? DisplayName { get; set; }

    public UserRole? Role { get; set; }

    public bool? IsActive { get; set; }
}
