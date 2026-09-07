using System.Text;
using System.Text.Json;

namespace Knowz.MCP.Services.Proxy;

public class McpApiProxyService : IMcpApiProxyService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<McpApiProxyService> _logger;

    public McpApiProxyService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<McpApiProxyService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;

        var baseUrl = _configuration["Knowz:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("Configuration 'Knowz:BaseUrl' is required");
        }
    }

    public async Task<TResponse?> ProxyRequestAsync<TRequest, TResponse>(
        string method,
        TRequest? request,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient("McpApiClient");
        var baseUrl = _configuration["Knowz:BaseUrl"];
        var url = $"{baseUrl}/api/v1/mcp/{method}";

        _logger.LogInformation("Proxying MCP request: {Method} to {Url}", method, url);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Add("X-Api-Key", apiKey);

        var json = request switch
        {
            null => "{}",
            JsonElement elem when elem.ValueKind == JsonValueKind.Null || elem.ValueKind == JsonValueKind.Undefined => "{}",
            _ => JsonSerializer.Serialize(request)
        };
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await client.SendAsync(httpRequest, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("MCP proxy request failed: {StatusCode} - {Body}", response.StatusCode, errorBody);

                throw new McpProxyException(
                    $"API request failed with status {response.StatusCode}",
                    (int)response.StatusCode,
                    errorBody);
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return default;
            }

            return JsonSerializer.Deserialize<TResponse>(responseBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "MCP proxy request timed out: {Method}", method);
            throw new TimeoutException($"Request to {method} timed out", ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "MCP proxy request failed: {Method}", method);
            throw;
        }
    }
}
