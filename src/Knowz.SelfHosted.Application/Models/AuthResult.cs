namespace Knowz.SelfHosted.Application.Models;

/// <summary>
/// Result of a successful authentication (login or API key validation).
/// </summary>
public class AuthResult
{
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public UserDto User { get; set; } = null!;
}
