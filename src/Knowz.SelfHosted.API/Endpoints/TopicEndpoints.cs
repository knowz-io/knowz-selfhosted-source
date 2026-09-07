using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;

namespace Knowz.SelfHosted.API.Endpoints;

public static class TopicEndpoints
{
    public static void MapTopicEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/topics").WithTags("Topics");

        group.MapGet("/", async (
            TopicService svc,
            int limit = 50,
            CancellationToken ct = default) =>
        {
            limit = Math.Clamp(limit, 1, 100);
            var result = await svc.ListTopicsAsync(limit, ct);
            return Results.Ok(result);
        }).Produces<TopicListResponse>();

        group.MapGet("/{id:guid}", async (
            TopicService svc, Guid id, CancellationToken ct) =>
        {
            var result = await svc.GetTopicDetailsAsync(id, ct);
            return result is null
                ? Results.NotFound(new { error = "Topic not found" })
                : Results.Ok(result);
        }).Produces<TopicDetailResponse>().Produces(404);
    }
}
