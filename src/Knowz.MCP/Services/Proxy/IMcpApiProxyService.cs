namespace Knowz.MCP.Services.Proxy;

public interface IMcpApiProxyService
{
    Task<TResponse?> ProxyRequestAsync<TRequest, TResponse>(
        string method,
        TRequest? request,
        string apiKey,
        CancellationToken cancellationToken = default);
}

public class McpProxyException : Exception
{
    public int StatusCode { get; init; }
    public string? ResponseBody { get; init; }

    public McpProxyException(string message, int statusCode, string? responseBody = null)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
