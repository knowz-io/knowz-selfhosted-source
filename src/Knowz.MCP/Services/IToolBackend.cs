namespace Knowz.MCP.Services;

/// <summary>
/// Backend interface for MCP tool execution.
/// Forwards all requests to the Knowz API.
/// </summary>
public interface IToolBackend
{
    Task<string> ExecuteToolAsync(
        string toolName,
        Dictionary<string, object> arguments,
        CancellationToken cancellationToken = default);
}
