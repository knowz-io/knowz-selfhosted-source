namespace Knowz.MCP.Services;

public interface IMcpSSOService
{
    /// <summary>Returns auth page policy and enabled SSO providers.</summary>
    Task<McpAuthPageOptions> GetAuthPageOptionsAsync(CancellationToken ct = default);

    /// <summary>Returns enabled SSO providers based on platform config.</summary>
    List<McpSSOProvider> GetEnabledProviders();

    /// <summary>Starts the OIDC flow: generates authorize URL, stores state.</summary>
    Task<McpSSOStartResult> StartSSOFlowAsync(string provider, string requestId, string callbackUrl);

    /// <summary>Handles OIDC callback: validates token, resolves email to API key via platform.</summary>
    Task<McpSSOCallbackResult> HandleSSOCallbackAsync(string code, string state);
}

public class McpSSOProvider
{
    public string Provider { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsForcedDefault { get; set; }
}

public class McpAuthPageOptions
{
    public bool ShowPasswordLogin { get; set; } = true;
    public bool ShowApiKeyLogin { get; set; } = true;
    public bool ForcedSso { get; set; }
    public string ConfigurationStatus { get; set; } = "disabled";
    public string? OrganizationName { get; set; }
    public string? ErrorCode { get; set; }
    public List<McpSSOProvider> SsoProviders { get; set; } = new();
}

public class McpSSOStartResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? AuthorizationUrl { get; set; }
}

public class McpSSOCallbackResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? RequestId { get; set; }
    public string? ApiKey { get; set; }
    public string? Email { get; set; }
}
