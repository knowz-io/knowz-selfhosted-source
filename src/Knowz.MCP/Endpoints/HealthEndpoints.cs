using Knowz.MCP.Services.Session;

namespace Knowz.MCP.Endpoints;

public static class HealthEndpoints
{
    public static WebApplication MapHealthEndpoints(this WebApplication app)
    {
        var methods = new[] { "GET", "HEAD" };

        app.MapMethods("/health", methods, GetHealth);
        app.MapMethods("/healthz", methods, GetHealth);

        return app;
    }

    private static IResult GetHealth(IMcpSessionStore sessionStore)
    {
        var redisAvailable = sessionStore is RedisMcpSessionStore redisStore && redisStore.IsRedisAvailable;
        var fallbackCount = sessionStore is RedisMcpSessionStore rs ? rs.FallbackSessionCount : -1;

        return Results.Ok(new
        {
            status = "healthy",
            mode = "proxy",
            version = "2.0.0",
            sessionStore = redisAvailable ? "redis" : "in-memory-fallback",
            fallbackSessionCount = fallbackCount
        });
    }
}
