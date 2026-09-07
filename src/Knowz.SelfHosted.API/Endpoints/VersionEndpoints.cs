using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;

namespace Knowz.SelfHosted.API.Endpoints;

public static class VersionEndpoints
{
    public static void MapVersionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/knowledge/{knowledgeId:guid}/versions")
            .WithTags("Versioning");

        group.MapGet("/", async (
            Guid knowledgeId,
            VersioningService versioningService,
            KnowledgeService knowledgeService,
            IVaultAccessService vaultAccessService,
            HttpContext context,
            CancellationToken ct) =>
        {
            // Check the knowledge item exists
            var item = await knowledgeService.GetKnowledgeItemAsync(knowledgeId, ct);
            if (item == null)
                return Results.NotFound(new { error = "Knowledge item not found" });

            // Check vault read access
            var vaultIds = await knowledgeService.GetKnowledgeVaultIdsAsync(knowledgeId, ct);
            if (vaultIds.Count > 0)
            {
                var accessibleVaultIds = await VaultEndpoints.ResolveAccessibleVaultIdsAsync(context, vaultAccessService, ct);
                if (accessibleVaultIds != null && !vaultIds.Any(v => accessibleVaultIds.Contains(v)))
                    return Results.Json(new { error = "Access denied to this knowledge item." }, statusCode: 403);
            }

            var versions = await versioningService.GetVersionsAsync(knowledgeId, ct);
            var response = versions.Select(v => new VersionResponse(
                v.Id, v.KnowledgeId, v.VersionNumber, v.Title, v.Content, v.ContentType,
                v.CreatedAt, v.CreatedByUserId, v.ChangeDescription));

            return Results.Ok(response);
        }).Produces<IEnumerable<VersionResponse>>().Produces(404);

        group.MapGet("/{versionNumber:int}", async (
            Guid knowledgeId,
            int versionNumber,
            VersioningService versioningService,
            KnowledgeService knowledgeService,
            IVaultAccessService vaultAccessService,
            HttpContext context,
            CancellationToken ct) =>
        {
            // Check the knowledge item exists
            var item = await knowledgeService.GetKnowledgeItemAsync(knowledgeId, ct);
            if (item == null)
                return Results.NotFound(new { error = "Knowledge item not found" });

            // Check vault read access
            var vaultIds = await knowledgeService.GetKnowledgeVaultIdsAsync(knowledgeId, ct);
            if (vaultIds.Count > 0)
            {
                var accessibleVaultIds = await VaultEndpoints.ResolveAccessibleVaultIdsAsync(context, vaultAccessService, ct);
                if (accessibleVaultIds != null && !vaultIds.Any(v => accessibleVaultIds.Contains(v)))
                    return Results.Json(new { error = "Access denied to this knowledge item." }, statusCode: 403);
            }

            var version = await versioningService.GetVersionAsync(knowledgeId, versionNumber, ct);
            if (version == null)
                return Results.NotFound(new { error = "Version not found" });

            var response = new VersionDetailResponse(
                version.Id, version.KnowledgeId, version.VersionNumber,
                version.Title, version.Content, version.ContentType,
                version.CreatedAt, version.CreatedByUserId, version.ChangeDescription);

            return Results.Ok(response);
        }).Produces<VersionDetailResponse>().Produces(404);

        group.MapPost("/{versionNumber:int}/restore", async (
            Guid knowledgeId,
            int versionNumber,
            VersioningService versioningService,
            KnowledgeService knowledgeService,
            IVaultAccessService vaultAccessService,
            HttpContext context,
            CancellationToken ct) =>
        {
            // Check the knowledge item exists
            var item = await knowledgeService.GetKnowledgeItemAsync(knowledgeId, ct);
            if (item == null)
                return Results.NotFound(new { error = "Knowledge item not found" });

            // Check vault write access
            var vaultIds = await knowledgeService.GetKnowledgeVaultIdsAsync(knowledgeId, ct);
            if (vaultIds.Count > 0)
            {
                var hasWriteAccess = false;
                foreach (var vaultId in vaultIds)
                {
                    if (await VaultEndpoints.HasVaultAccessAsync(context, vaultAccessService, vaultId, requireWrite: true, ct: ct))
                    {
                        hasWriteAccess = true;
                        break;
                    }
                }
                if (!hasWriteAccess)
                    return Results.Json(new { error = "Access denied. Write permission required." }, statusCode: 403);
            }

            var userId = VaultEndpoints.GetUserIdFromContext(context);
            var restored = await versioningService.RestoreVersionAsync(knowledgeId, versionNumber, userId, ct);

            if (!restored)
                return Results.NotFound(new { error = "Version not found" });

            return Results.Ok(new { status = "restored", knowledgeId, versionNumber });
        }).Produces<object>().Produces(404);
    }
}

// DTOs for version endpoints
public record VersionResponse(
    Guid Id, Guid KnowledgeId, int VersionNumber, string Title,
    string Content,
    string? ContentType, DateTime CreatedAt, Guid? CreatedByUserId,
    string? ChangeDescription);

public record VersionDetailResponse(
    Guid Id, Guid KnowledgeId, int VersionNumber, string Title,
    string Content, string? ContentType, DateTime CreatedAt,
    Guid? CreatedByUserId, string? ChangeDescription);
