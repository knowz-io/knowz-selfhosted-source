using System.ComponentModel.DataAnnotations;
using Knowz.Core.Enums;

namespace Knowz.SelfHosted.Application.Models;

/// <summary>
/// Request model for creating a new user.
/// </summary>
public class CreateUserRequest
{
    [Required]
    public Guid TenantId { get; set; }

    [Required]
    [MaxLength(100)]
    public string Username { get; set; } = string.Empty;

    [Required]
    [MinLength(6)]
    public string Password { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? Email { get; set; }

    [MaxLength(200)]
    public string? DisplayName { get; set; }

    public UserRole Role { get; set; } = UserRole.User;
}
