using System.Collections.Concurrent;

namespace Knowz.MCP.Services.Session;

/// <summary>
/// Tracks MCP session IDs that the SDK's in-memory transport layer knows about.
/// After a container deployment/restart, the SDK's session store is empty but clients
/// still send old Mcp-Session-Id headers. The middleware uses this tracker to detect
/// stale session IDs and strip them before the SDK returns 404, forcing transparent
/// session re-creation from Redis-backed auth state.
/// </summary>
public interface IActiveSessionTracker
{
    void Track(string sessionId);
    bool IsKnown(string sessionId);
    void Remove(string sessionId);
    int Count { get; }
}

public class ActiveSessionTracker : IActiveSessionTracker
{
    private readonly ConcurrentDictionary<string, byte> _sessions = new();

    public void Track(string sessionId) => _sessions.TryAdd(sessionId, 0);
    public bool IsKnown(string sessionId) => _sessions.ContainsKey(sessionId);
    public void Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);
    public int Count => _sessions.Count;
}
