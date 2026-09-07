using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.API.Models;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;

namespace Knowz.SelfHosted.API.Endpoints;

public static class InboxEndpoints
{
    public static void MapInboxEndpoints(this WebApplication app)
    {
        // POST /api/inbox -- create (existing)
        app.MapPost("/api/v1/inbox", async (
            InboxService svc, HttpContext context, CreateInboxItemRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Body))
                return Results.BadRequest(new { error = "body is required" });

            var userId = AuthorizationHelpers.GetCallerId(context);
            var result = await svc.CreateInboxItemAsync(req.Body, userId, ct);
            return Results.Created($"/api/v1/inbox/{result.Id}", result);
        }).WithTags("Inbox").Produces<InboxItemResult>(201).Produces(400);

        // GET /api/inbox -- list (paginated, searchable, filterable)
        app.MapGet("/api/v1/inbox", async (
            InboxService svc,
            HttpContext context,
            int page, int pageSize,
            string? search, string? type,
            string? userFilter,
            CancellationToken ct) =>
        {
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 20;
            if (pageSize > 100) pageSize = 100;

            var callerUserId = AuthorizationHelpers.GetCallerId(context);
            var isCallerAdmin = AuthorizationHelpers.IsAdminOrAbove(context);

            // If admin requests "mine", treat as non-admin for filtering purposes
            if (string.Equals(userFilter, "mine", StringComparison.OrdinalIgnoreCase))
                isCallerAdmin = false;

            var result = await svc.ListInboxItemsAsync(page, pageSize, search, type, callerUserId, isCallerAdmin, ct);
            return Results.Ok(result);
        }).WithTags("Inbox").Produces<InboxListResponse>();

        // GET /api/inbox/{id} -- get by ID
        app.MapGet("/api/v1/inbox/{id:guid}", async (
            InboxService svc, Guid id, CancellationToken ct) =>
        {
            var result = await svc.GetInboxItemAsync(id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithTags("Inbox").Produces<InboxItemDto>().Produces(404);

        // PUT /api/inbox/{id} -- update
        app.MapPut("/api/v1/inbox/{id:guid}", async (
            InboxService svc, Guid id, UpdateInboxItemRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Body))
                return Results.BadRequest(new { error = "body is required" });

            var result = await svc.UpdateInboxItemAsync(id, req.Body, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithTags("Inbox").Produces<InboxItemDto>().Produces(400).Produces(404);

        // DELETE /api/inbox/{id} -- soft delete
        app.MapDelete("/api/v1/inbox/{id:guid}", async (
            InboxService svc, Guid id, CancellationToken ct) =>
        {
            var result = await svc.DeleteInboxItemAsync(id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithTags("Inbox").Produces<DeleteResult>().Produces(404);

        // POST /api/inbox/{id}/convert -- convert to knowledge
        app.MapPost("/api/v1/inbox/{id:guid}/convert", async (
            InboxService svc, IVaultAccessService vaultAccessService, HttpContext context,
            Guid id, ConvertInboxItemRequest req, CancellationToken ct) =>
        {
            // Check write access to target vault
            if (!string.IsNullOrWhiteSpace(req.VaultId) && Guid.TryParse(req.VaultId, out var targetVaultId))
            {
                var hasAccess = await VaultEndpoints.HasVaultAccessAsync(
                    context, vaultAccessService, targetVaultId, requireWrite: true, ct: ct);
                if (!hasAccess)
                    return Results.Json(new { error = "Access denied to the target vault." }, statusCode: 403);
            }

            var result = await svc.ConvertToKnowledgeAsync(id, req.VaultId, req.Tags, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithTags("Inbox").Produces<ConvertToKnowledgeResult>().Produces(404);

        // POST /api/inbox/batch-convert -- batch convert to knowledge
        app.MapPost("/api/v1/inbox/batch-convert", async (
            InboxService svc, IVaultAccessService vaultAccessService, HttpContext context,
            BatchConvertRequest req, CancellationToken ct) =>
        {
            if (req.Ids.Count > 50)
                return Results.BadRequest(new { error = "Batch convert is limited to 50 items at a time." });

            if (req.Ids.Count == 0)
                return Results.BadRequest(new { error = "At least one ID is required." });

            // Check write access to target vault
            if (!string.IsNullOrWhiteSpace(req.VaultId) && Guid.TryParse(req.VaultId, out var targetVaultId))
            {
                var hasAccess = await VaultEndpoints.HasVaultAccessAsync(
                    context, vaultAccessService, targetVaultId, requireWrite: true, ct: ct);
                if (!hasAccess)
                    return Results.Json(new { error = "Access denied to the target vault." }, statusCode: 403);
            }

            var result = await svc.BatchConvertToKnowledgeAsync(req.Ids, req.VaultId, req.Tags, ct);
            return Results.Ok(result);
        }).WithTags("Inbox").Produces<BatchConvertResult>().Produces(400);
    }
}
