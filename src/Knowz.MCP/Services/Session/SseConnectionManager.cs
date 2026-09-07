using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Knowz.MCP.Services.Session;

public class SseConnection
{
    public required string ConnectionId { get; init; }
    public required string ApiKey { get; init; }
    public required Stream ResponseStream { get; init; }
    public DateTime LastActivity { get; set; }
    public CancellationTokenSource CancellationToken { get; init; } = new();
}

public interface ISseConnectionManager
{
    void AddConnection(string connectionId, SseConnection connection);
    void RemoveConnection(string connectionId);
    SseConnection? GetConnection(string connectionId);
    Task SendMessageAsync(string connectionId, string eventType, object data, CancellationToken cancellationToken = default);
    Task SendKeepAliveAsync(string connectionId, CancellationToken cancellationToken = default);
}

public class SseConnectionManager : ISseConnectionManager
{
    private readonly ConcurrentDictionary<string, SseConnection> _connections = new();
    private readonly ILogger<SseConnectionManager> _logger;

    public SseConnectionManager(ILogger<SseConnectionManager> logger)
    {
        _logger = logger;
    }

    public void AddConnection(string connectionId, SseConnection connection)
    {
        _connections[connectionId] = connection;
        _logger.LogInformation("SSE connection added: {ConnectionId}", connectionId);
    }

    public void RemoveConnection(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out var connection))
        {
            connection.CancellationToken.Cancel();
            connection.CancellationToken.Dispose();
            _logger.LogInformation("SSE connection removed: {ConnectionId}", connectionId);
        }
    }

    public SseConnection? GetConnection(string connectionId)
    {
        _connections.TryGetValue(connectionId, out var connection);
        return connection;
    }

    public async Task SendMessageAsync(string connectionId, string eventType, object data, CancellationToken cancellationToken = default)
    {
        var connection = GetConnection(connectionId);
        if (connection == null)
        {
            _logger.LogWarning("Connection not found: {ConnectionId}", connectionId);
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(data);
            var message = $"event: {eventType}\ndata: {json}\n\n";
            var bytes = Encoding.UTF8.GetBytes(message);

            await connection.ResponseStream.WriteAsync(bytes, cancellationToken);
            await connection.ResponseStream.FlushAsync(cancellationToken);

            connection.LastActivity = DateTime.UtcNow;
            _logger.LogDebug("Sent SSE message to {ConnectionId}: {EventType}", connectionId, eventType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending SSE message to {ConnectionId}", connectionId);
            RemoveConnection(connectionId);
        }
    }

    public async Task SendKeepAliveAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        var connection = GetConnection(connectionId);
        if (connection == null)
        {
            return;
        }

        try
        {
            var message = Encoding.UTF8.GetBytes(": keep-alive\n\n");
            await connection.ResponseStream.WriteAsync(message, cancellationToken);
            await connection.ResponseStream.FlushAsync(cancellationToken);

            connection.LastActivity = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending keep-alive to {ConnectionId}", connectionId);
            RemoveConnection(connectionId);
        }
    }
}
