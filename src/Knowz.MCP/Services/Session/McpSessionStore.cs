using System.Collections.Concurrent;

namespace Knowz.MCP.Services.Session;

/// <summary>
/// Thread-safe storage for MCP session data, persisting across HTTP requests within a session.
/// </summary>
public interface IMcpSessionStore
{
    void StoreApiKey(string sessionId, string apiKey);
    string? GetApiKey(string sessionId);
    void RemoveSession(string sessionId);
    void CleanupExpiredSessions();

    /// <summary>
    /// Sliding inactivity timeout after which stored entries (sessions and opaque
    /// OAuth access tokens) expire. Reported to OAuth clients as expires_in.
    /// </summary>
    TimeSpan SessionTimeout { get; }
}

public class McpSessionStore : IMcpSessionStore
{
    private readonly ConcurrentDictionary<string, SessionData> _sessions = new();
    private readonly TimeSpan _sessionTimeout = TimeSpan.FromDays(30);
    private readonly ILogger<McpSessionStore> _logger;

    public McpSessionStore(ILogger<McpSessionStore> logger)
    {
        _logger = logger;
    }

    public TimeSpan SessionTimeout => _sessionTimeout;

    public void StoreApiKey(string sessionId, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(apiKey))
            return;

        _sessions.AddOrUpdate(
            sessionId,
            _ => new SessionData { ApiKey = apiKey, LastActivity = DateTime.UtcNow },
            (_, existing) =>
            {
                existing.ApiKey = apiKey;
                existing.LastActivity = DateTime.UtcNow;
                return existing;
            });

        _logger.LogDebug("Stored API key for MCP session {SessionId}", sessionId);
    }

    public string? GetApiKey(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        if (_sessions.TryGetValue(sessionId, out var data))
        {
            data.LastActivity = DateTime.UtcNow;
            return data.ApiKey;
        }

        return null;
    }

    public void RemoveSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out _))
        {
            _logger.LogDebug("Removed MCP session {SessionId}", sessionId);
        }
    }

    public void CleanupExpiredSessions()
    {
        var cutoff = DateTime.UtcNow - _sessionTimeout;
        var expiredSessions = _sessions
            .Where(kvp => kvp.Value.LastActivity < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var sessionId in expiredSessions)
        {
            RemoveSession(sessionId);
        }

        if (expiredSessions.Count > 0)
        {
            _logger.LogInformation("Cleaned up {Count} expired MCP sessions", expiredSessions.Count);
        }
    }

    private class SessionData
    {
        public string? ApiKey { get; set; }
        public DateTime LastActivity { get; set; }
    }
}
