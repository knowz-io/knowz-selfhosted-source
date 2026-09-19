using System.Text.Json;

namespace Knowz.MCP.Services.Proxy;

/// <summary>
/// Proxy mode tool backend: forwards tool calls to the Knowz Platform API.
/// Wraps the existing McpApiProxyService.
/// </summary>
public class ProxyToolBackend : IToolBackend
{
    private readonly IMcpApiProxyService _proxyService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ProxyToolBackend> _logger;

    public ProxyToolBackend(
        IMcpApiProxyService proxyService,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ProxyToolBackend> logger)
    {
        _proxyService = proxyService;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    /// <summary>Reads errors[0] / message and code from an ApiResponse envelope body; null on anything else.</summary>
    private static (string? Error, string? Code) TryReadApiEnvelope(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null);
            string? error = null;
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                error = errors[0].GetString();
            if (string.IsNullOrWhiteSpace(error) && doc.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                error = message.GetString();
            string? code = doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            return (string.IsNullOrWhiteSpace(error) ? null : error, string.IsNullOrWhiteSpace(code) ? null : code);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // JsonException: not JSON. InvalidOperationException: valid JSON with an unexpected element kind
            // (e.g. errors[0] is a number). Both mean "no envelope copy" — never escape the proxy error handler.
            return (null, null);
        }
    }

    public async Task<string> ExecuteToolAsync(
        string toolName,
        Dictionary<string, object> arguments,
        CancellationToken cancellationToken = default)
    {
        var context = _httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("No HttpContext available");

        var apiKey = context.Items["ApiKey"] as string
            ?? throw new InvalidOperationException("No API key available for proxy mode");

        _logger.LogInformation("ProxyToolBackend: Forwarding tool {ToolName} to Knowz API", toolName);

        try
        {
            var result = await _proxyService.ProxyRequestAsync<Dictionary<string, object>, object>(
                $"tools/call",
                new Dictionary<string, object>
                {
                    ["name"] = toolName,
                    ["arguments"] = arguments
                },
                apiKey,
                cancellationToken);

            if (TryExtractToolText(result, out var toolText))
            {
                return toolText;
            }

            return result != null
                ? JsonSerializer.Serialize(result)
                : "{}";
        }
        catch (McpProxyException ex)
        {
            // Prefer the API's own envelope copy (ApiResponse.Fail puts it in errors[0]; Code carries the
            // frozen machine code, e.g. AI_UPGRADE_REQUIRED for a plan without AI chat) so a plan gate
            // reads as a plan gate to the MCP client, not as a generic transport failure.
            var (apiError, apiCode) = TryReadApiEnvelope(ex.ResponseBody);
            var errorMessage = ex.StatusCode switch
            {
                401 => "Authentication failed — API key is invalid or expired. Check your MCP server configuration.",
                402 => apiError ?? "This plan does not include AI chat. Upgrade the Knowz plan (Settings → Plan) to use AI tools.",
                403 => apiError ?? "Access denied — your API key does not have permission for this operation.",
                404 => $"Tool '{toolName}' not found on the Knowz API.",
                429 => "Rate limited — too many requests. Please wait and try again.",
                >= 500 => $"Knowz API server error (HTTP {ex.StatusCode}). The service may be temporarily unavailable.",
                _ => apiError ?? $"Knowz API request failed (HTTP {ex.StatusCode})."
            };

            _logger.LogWarning(ex, "ProxyToolBackend: Tool {ToolName} failed with HTTP {StatusCode}: {Error}",
                toolName, ex.StatusCode, errorMessage);

            return apiCode is null
                ? JsonSerializer.Serialize(new { error = errorMessage, statusCode = ex.StatusCode })
                : JsonSerializer.Serialize(new { error = errorMessage, statusCode = ex.StatusCode, code = apiCode });
        }
    }

    private static bool TryExtractToolText(object? result, out string toolText)
    {
        toolText = string.Empty;
        if (result is not JsonElement element || element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!element.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array ||
            content.GetArrayLength() == 0)
        {
            return false;
        }

        var first = content[0];
        if (first.ValueKind != JsonValueKind.Object ||
            !first.TryGetProperty("text", out var textElement) ||
            textElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = textElement.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        toolText = text;
        return true;
    }
}
