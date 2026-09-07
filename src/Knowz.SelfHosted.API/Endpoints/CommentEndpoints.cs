using Knowz.SelfHosted.API.Models;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;

namespace Knowz.SelfHosted.API.Endpoints;

public static class CommentEndpoints
{
    public static void MapCommentEndpoints(this WebApplication app)
    {
        var knowledgeComments = app.MapGroup("/api/v1/knowledge/{knowledgeId:guid}/comments")
            .WithTags("Comments");

        var comments = app.MapGroup("/api/v1/comments")
            .WithTags("Comments");

        knowledgeComments.MapPost("/", CreateComment)
            .Produces<CommentResponse>(201).Produces(400).Produces(403).Produces(404);

        knowledgeComments.MapGet("/", ListComments)
            .Produces<List<CommentResponse>>().Produces(403).Produces(404);

        comments.MapPut("/{id:guid}", UpdateComment)
            .Produces<CommentResponse>().Produces(400).Produces(403).Produces(404);

        comments.MapDelete("/{id:guid}", DeleteComment)
            .Produces<CommentDeleteResult>().Produces(403).Produces(404);
    }

    private static async Task<IResult> CreateComment(
        Guid knowledgeId,
        CreateCommentRequest req,
        CommentService commentService,
        KnowledgeService knowledgeService,
        IVaultAccessService vaultAccessService,
        HttpContext context,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Body))
            return Results.BadRequest(new { error = "Body is required" });

        if (req.Body.Length > 50_000)
            return Results.BadRequest(new { error = "Body exceeds maximum allowed size of 50,000 characters" });

        // Check knowledge item exists
        var knowledge = await knowledgeService.GetKnowledgeItemAsync(knowledgeId, ct);
        if (knowledge == null)
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

        var authorName = ResolveAuthorName(context, req.AuthorName);

        var result = await commentService.AddCommentAsync(
            knowledgeId, req.Body, authorName, req.ParentCommentId, req.Sentiment, ct);

        if (result == null)
            return Results.NotFound(new { error = "Knowledge item or parent comment not found" });

        return Results.Created($"/api/v1/comments/{result.Id}", result);
    }

    private static async Task<IResult> ListComments(
        Guid knowledgeId,
        CommentService commentService,
        KnowledgeService knowledgeService,
        IVaultAccessService vaultAccessService,
        HttpContext context,
        CancellationToken ct)
    {
        // Check knowledge item exists
        var knowledge = await knowledgeService.GetKnowledgeItemAsync(knowledgeId, ct);
        if (knowledge == null)
            return Results.NotFound(new { error = "Knowledge item not found" });

        // Check vault read access
        var vaultIds = await knowledgeService.GetKnowledgeVaultIdsAsync(knowledgeId, ct);
        if (vaultIds.Count > 0)
        {
            var accessibleVaultIds = await VaultEndpoints.ResolveAccessibleVaultIdsAsync(context, vaultAccessService, ct);
            if (accessibleVaultIds != null && !vaultIds.Any(v => accessibleVaultIds.Contains(v)))
                return Results.Json(new { error = "Access denied to this knowledge item." }, statusCode: 403);
        }

        var result = await commentService.ListCommentsAsync(knowledgeId, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> UpdateComment(
        Guid id,
        UpdateCommentRequest req,
        CommentService commentService,
        KnowledgeService knowledgeService,
        IVaultAccessService vaultAccessService,
        HttpContext context,
        CancellationToken ct)
    {
        if (req.Body != null && string.IsNullOrWhiteSpace(req.Body))
            return Results.BadRequest(new { error = "Body cannot be empty when provided" });

        if (req.Body != null && req.Body.Length > 50_000)
            return Results.BadRequest(new { error = "Body exceeds maximum allowed size of 50,000 characters" });

        // Get comment to find its knowledge ID
        var comment = await commentService.GetCommentAsync(id, ct);
        if (comment == null)
            return Results.NotFound(new { error = "Comment not found" });

        // Check vault write access on the parent knowledge item
        var vaultIds = await knowledgeService.GetKnowledgeVaultIdsAsync(comment.KnowledgeId, ct);
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

        var result = await commentService.UpdateCommentAsync(id, req.Body, req.Sentiment, ct);
        return result == null
            ? Results.NotFound(new { error = "Comment not found" })
            : Results.Ok(result);
    }

    // DELETE /api/v1/comments/{id}?deleteFiles={bool}
    // WorkGroupID: kc-fix-attach-delete-transcript-20260411-080000 — FEAT_CommentDeleteAttachmentChoice.
    // `deleteFiles` defaults to false so existing clients inherit the safer "preserve" behavior.
    private static async Task<IResult> DeleteComment(
        Guid id,
        CommentService commentService,
        KnowledgeService knowledgeService,
        IVaultAccessService vaultAccessService,
        HttpContext context,
        CancellationToken ct,
        bool deleteFiles = false)
    {
        // Get comment to find its knowledge ID
        var comment = await commentService.GetCommentAsync(id, ct);
        if (comment == null)
            return Results.NotFound(new { error = "Comment not found" });

        // Check vault write access on the parent knowledge item
        var vaultIds = await knowledgeService.GetKnowledgeVaultIdsAsync(comment.KnowledgeId, ct);
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

        var result = await commentService.DeleteCommentAsync(id, deleteFiles, ct);
        return result != null
            ? Results.Ok(result)
            : Results.NotFound(new { error = "Comment not found" });
    }

    private static string ResolveAuthorName(HttpContext context, string? requestAuthorName)
    {
        var displayName = context.User?.FindFirst("name")?.Value
            ?? context.User?.FindFirst("preferred_username")?.Value;
        if (!string.IsNullOrWhiteSpace(displayName))
            return displayName;

        if (!string.IsNullOrWhiteSpace(requestAuthorName))
            return requestAuthorName;

        return "Anonymous";
    }
}
