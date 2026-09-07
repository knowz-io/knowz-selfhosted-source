using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;

namespace Knowz.SelfHosted.API.Endpoints;

public static class TagEndpoints
{
    public static void MapTagEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/tags").WithTags("Tags");

        group.MapGet("/", async (
            TagService svc,
            string? q = null,
            int limit = 50,
            CancellationToken ct = default) =>
        {
            limit = Math.Clamp(limit, 1, 100);
            var result = await svc.ListTagsAsync(q, limit, ct);
            return Results.Ok(result);
        }).Produces<List<TagListItem>>();

        group.MapPost("/", async (
            TagService svc, CreateTagRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "name is required" });

            try
            {
                var result = await svc.CreateTagAsync(req.Name.Trim(), ct);
                return Results.Created($"/api/v1/tags/{result.Id}", result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        }).Produces<TagListItem>(201).Produces(400).Produces(409);

        group.MapPut("/{id:guid}", async (
            TagService svc, Guid id, UpdateTagRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "name is required" });

            try
            {
                var result = await svc.UpdateTagAsync(id, req.Name.Trim(), ct);
                return result is null
                    ? Results.NotFound(new { error = "Tag not found" })
                    : Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        }).Produces<TagListItem>().Produces(400).Produces(404).Produces(409);

        group.MapDelete("/{id:guid}", async (
            TagService svc, Guid id, CancellationToken ct) =>
        {
            var result = await svc.DeleteTagAsync(id, ct);
            return result is null
                ? Results.NotFound(new { error = "Tag not found" })
                : Results.Ok(result);
        }).Produces<DeleteResult>().Produces(404);
    }
}

public record CreateTagRequest(string Name);

public record UpdateTagRequest(string Name);
