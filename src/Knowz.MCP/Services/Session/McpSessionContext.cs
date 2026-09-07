namespace Knowz.MCP.Services.Session;

/// <summary>
/// Placeholder for future session-specific context if needed.
/// Currently tools access API key directly via IHttpContextAccessor.
/// </summary>
public interface IMcpSessionContext
{
    string? ApiKey { get; set; }
}

public class McpSessionContext : IMcpSessionContext
{
    public string? ApiKey { get; set; }
}
