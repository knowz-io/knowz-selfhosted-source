using System.ComponentModel.DataAnnotations;

namespace Knowz.SelfHosted.Application.Models;

/// <summary>
/// Request model for resetting a user's password.
/// </summary>
public class ResetPasswordRequest
{
    [Required]
    [MinLength(6)]
    public string NewPassword { get; set; } = string.Empty;
}
