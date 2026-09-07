using System.Text;
using System.Text.Json;
using Knowz.MCP.Middleware;
using Knowz.MCP.Services.Proxy;
using Knowz.MCP.Services.Session;

namespace Knowz.MCP.Endpoints;

public static class LegacySseEndpoints
{
    public static WebApplication MapLegacySseEndpoints(this WebApplication app)
    {
        // Legacy SSE endpoint - MCP 2024-11-05 spec compliant (keep for backwards compatibility)
        app.MapGet("/sse", async (HttpContext context, ISseConnectionManager connectionManager) =>
        {
            var apiKey = context.GetApiKey();
            if (apiKey == null)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
                return;
            }

            var sessionId = Guid.NewGuid().ToString();

            context.Response.Headers["Content-Type"] = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-cache";
            context.Response.Headers["Connection"] = "keep-alive";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            var connection = new SseConnection
            {
                ConnectionId = sessionId,
                ApiKey = apiKey,
                ResponseStream = context.Response.Body,
                LastActivity = DateTime.UtcNow
            };

            connectionManager.AddConnection(sessionId, connection);

            // MCP 2024-11-05 spec: Send endpoint event with POST URL
            var scheme = context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault()
                         ?? (context.Request.IsHttps ? "https" : "http");
            var host = context.Request.Headers["X-Forwarded-Host"].FirstOrDefault()
                       ?? context.Request.Host.ToString();
            var endpointUrl = $"{scheme}://{host}/message?sessionId={sessionId}";

            var endpointBytes = Encoding.UTF8.GetBytes($"event: endpoint\ndata: {endpointUrl}\n\n");
            await context.Response.Body.WriteAsync(endpointBytes);
            await context.Response.Body.FlushAsync();

            try
            {
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
                    await connectionManager.SendKeepAliveAsync(sessionId, context.RequestAborted);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                connectionManager.RemoveConnection(sessionId);
            }
        });

        // Legacy message endpoint for SSE transport
        app.MapPost("/message", async (
            HttpContext context,
            ISseConnectionManager connectionManager,
            IMcpApiProxyService proxyService) =>
        {
            var apiKey = context.GetApiKey();
            if (apiKey == null)
            {
                return Results.Unauthorized();
            }

            var sessionId = context.Request.Query["sessionId"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return Results.BadRequest(new { error = "Missing sessionId" });
            }

            var connection = connectionManager.GetConnection(sessionId);
            if (connection == null)
            {
                return Results.NotFound(new { error = "Session not found or expired" });
            }

            JsonElement? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<JsonElement>();
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Invalid JSON" });
            }

            var method = request?.GetProperty("method").GetString();
            var requestParams = request?.TryGetProperty("params", out var p) == true ? p : (JsonElement?)null;

            var result = await proxyService.ProxyRequestAsync<JsonElement?, object>(
                method ?? "",
                requestParams,
                apiKey,
                context.RequestAborted);

            object? requestId = null;
            if (request?.TryGetProperty("id", out var idElement) == true)
            {
                requestId = idElement.ValueKind switch
                {
                    JsonValueKind.String => idElement.GetString(),
                    JsonValueKind.Number => idElement.GetInt64(),
                    _ => null
                };
            }

            var response = new
            {
                jsonrpc = "2.0",
                id = requestId,
                result
            };

            await connectionManager.SendMessageAsync(sessionId, "message", response);
            return Results.Accepted();
        });

        return app;
    }
}
