using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace Knowz.MCP.Services.Session;

/// <summary>
/// Redis-backed implementation of IMcpSessionStore that persists sessions across deployments and restarts.
/// Falls back to in-memory storage if Redis is unavailable.
/// </summary>
public class RedisMcpSessionStore : IMcpSessionStore
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<RedisMcpSessionStore> _logger;
    private readonly TimeSpan _sessionTimeout;
    private readonly ConcurrentDictionary<string, SessionData> _fallbackStore = new();
    private bool _redisAvailable = true;

    private const string KeyPrefix = "session:";

    public RedisMcpSessionStore(IDistributedCache cache, ILogger<RedisMcpSessionStore> logger, IConfiguration configuration)
    {
        _cache = cache;
        _logger = logger;

        var timeoutHours = configuration.GetValue<int>("MCP_SESSION_TIMEOUT_HOURS", 720); // 30 days default
        _sessionTimeout = TimeSpan.FromHours(timeoutHours);

        _logger.LogInformation("MCP session store initialized with {TimeoutHours}h timeout", timeoutHours);
    }

    public TimeSpan SessionTimeout => _sessionTimeout;

    public void StoreApiKey(string sessionId, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(apiKey))
            return;

        var data = new SessionData { ApiKey = apiKey, LastActivity = DateTime.UtcNow };

        try
        {
            var json = JsonSerializer.Serialize(data);
            var options = new DistributedCacheEntryOptions
            {
                SlidingExpiration = _sessionTimeout
            };

            _cache.SetString(KeyPrefix + sessionId, json, options);
            _redisAvailable = true;

            _logger.LogDebug("Stored API key for MCP session {SessionId} in Redis", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis unavailable, falling back to in-memory for session {SessionId}", sessionId);
            _redisAvailable = false;

            _fallbackStore.AddOrUpdate(
                sessionId,
                _ => data,
                (_, _) => data);
        }
    }

    public string? GetApiKey(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        try
        {
            // Redis SlidingExpiration auto-extends TTL on each Get — no manual re-write needed
            var json = _cache.GetString(KeyPrefix + sessionId);
            if (json != null)
            {
                var data = JsonSerializer.Deserialize<SessionData>(json);
                _redisAvailable = true;
                return data?.ApiKey;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis unavailable during GetApiKey for session {SessionId}, checking fallback", sessionId);
            _redisAvailable = false;
        }

        if (_fallbackStore.TryGetValue(sessionId, out var fallbackData))
        {
            fallbackData.LastActivity = DateTime.UtcNow;
            return fallbackData.ApiKey;
        }

        return null;
    }

    public void RemoveSession(string sessionId)
    {
        try
        {
            _cache.Remove(KeyPrefix + sessionId);
            _logger.LogDebug("Removed MCP session {SessionId} from Redis", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis unavailable during RemoveSession for {SessionId}", sessionId);
        }

        _fallbackStore.TryRemove(sessionId, out _);
    }

    public void CleanupExpiredSessions()
    {
        var cutoff = DateTime.UtcNow - _sessionTimeout;
        var expiredSessions = _fallbackStore
            .Where(kvp => kvp.Value.LastActivity < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var sessionId in expiredSessions)
        {
            _fallbackStore.TryRemove(sessionId, out _);
        }

        if (expiredSessions.Count > 0)
        {
            _logger.LogInformation("Cleaned up {Count} expired in-memory fallback sessions", expiredSessions.Count);
        }
    }

    public bool IsRedisAvailable => _redisAvailable;

    public int FallbackSessionCount => _fallbackStore.Count;

    private class SessionData
    {
        public string? ApiKey { get; set; }
        public DateTime LastActivity { get; set; }
    }

}
