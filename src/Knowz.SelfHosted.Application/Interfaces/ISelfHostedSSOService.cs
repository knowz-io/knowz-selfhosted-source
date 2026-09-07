namespace Knowz.SelfHosted.Application.Interfaces;

/// <summary>
/// Encapsulates SSO-specific logic for self-hosted deployments.
/// Handles OIDC authorization URL generation, callback processing, and user matching.
/// </summary>
public interface ISelfHostedSSOService
{
    /// <summary>Returns list of enabled SSO providers based on config.</summary>
    Task<List<SSOProviderInfo>> GetEnabledProvidersAsync();

    /// <summary>Generates OIDC authorize URL with PKCE for the given provider.</summary>
    Task<SSOAuthorizeResult> GenerateAuthorizeUrlAsync(string provider, string redirectUri);

    /// <summary>Handles OIDC callback: exchanges code, validates ID token, finds/creates user, returns JWT.</summary>
    Task<SSOCallbackResult> HandleCallbackAsync(string code, string state);
}

public class SSOProviderInfo
{
    public string Provider { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Mode { get; set; }
}

public class SSOAuthorizeResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? AuthorizationUrl { get; set; }
    public string? State { get; set; }
}

public class SSOCallbackResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Token { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public bool WasAutoProvisioned { get; set; }
}
