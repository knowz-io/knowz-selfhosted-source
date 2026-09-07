namespace Knowz.SelfHosted.API.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Ok(new { status = "healthy", version = "1.0.0" }))
            .WithTags("Health")
            .DisableRateLimiting()
            .ExcludeFromDescription();
    }
}
