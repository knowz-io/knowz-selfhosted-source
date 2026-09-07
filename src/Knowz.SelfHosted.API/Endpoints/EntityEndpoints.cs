using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;

namespace Knowz.SelfHosted.API.Endpoints;

public static class EntityEndpoints
{
    public static void MapEntityEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/entities").WithTags("Entities");

        group.MapGet("/", async (
            EntityService svc,
            string? type,
            string? q = null,
            int limit = 50,
            CancellationToken ct = default) =>
        {
            limit = Math.Clamp(limit, 1, 100);
            if (string.IsNullOrWhiteSpace(type))
                return Results.BadRequest(new { error = "type parameter is required (person, location, or event)" });

            try
            {
                var result = await svc.FindEntitiesAsync(type, q, limit, ct);
                return Results.Ok(result);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).Produces<EntitySearchResponse>().Produces(400);

        group.MapPost("/", async (
            EntityService svc, CreateEntityRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Type))
                return Results.BadRequest(new { error = "type is required (person, location, or event)" });
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "name is required" });

            try
            {
                var result = await svc.CreateEntityAsync(req.Type, req.Name.Trim(), ct);
                return Results.Created($"/api/v1/entities/{result.Id}", result);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).Produces<EntityItem>(201).Produces(400);

        group.MapPut("/{id:guid}", async (
            EntityService svc, Guid id, UpdateEntityRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Type))
                return Results.BadRequest(new { error = "type is required (person, location, or event)" });
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "name is required" });

            try
            {
                var result = await svc.UpdateEntityAsync(req.Type, id, req.Name.Trim(), ct);
                return result is null
                    ? Results.NotFound(new { error = "Entity not found" })
                    : Results.Ok(result);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).Produces<EntityItem>().Produces(400).Produces(404);

        group.MapDelete("/{id:guid}", async (
            EntityService svc, Guid id, string? type, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(type))
                return Results.BadRequest(new { error = "type query parameter is required (person, location, or event)" });

            try
            {
                var deleted = await svc.DeleteEntityAsync(type, id, ct);
                return deleted
                    ? Results.Ok(new { id, deleted = true })
                    : Results.NotFound(new { error = "Entity not found" });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).Produces(200).Produces(400).Produces(404);
    }
}

public record CreateEntityRequest(string Type, string Name);

public record UpdateEntityRequest(string Type, string Name);
