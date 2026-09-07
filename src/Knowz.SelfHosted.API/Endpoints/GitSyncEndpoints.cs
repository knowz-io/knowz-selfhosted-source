using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Application.Services.GitCommitHistory;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.API.Endpoints;

public static class GitSyncEndpoints
{
    public static void MapGitSyncEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/vaults/{vaultId:guid}/git-sync")
            .WithTags("GitSync");

        // POST /api/v1/vaults/{vaultId}/git-sync — Configure git repo for vault
        group.MapPost("/", async (
            HttpContext context,
            Guid vaultId,
            [FromBody] ConfigureGitSyncRequest request,
            GitSyncService gitSyncService,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden("Admin access required for git sync configuration.");

            // Defense-in-depth: reject out-of-range CommitHistoryDepth with a 400 before
            // hitting the service layer (which also enforces the ceiling).
            if (request.CommitHistoryDepth.HasValue &&
                (request.CommitHistoryDepth.Value < 1 || request.CommitHistoryDepth.Value > 2000))
            {
                return Results.BadRequest(new
                {
                    error = $"CommitHistoryDepth must be between 1 and 2000 (got {request.CommitHistoryDepth.Value})."
                });
            }

            try
            {
                var result = await gitSyncService.ConfigureAsync(
                    vaultId, request.Url, request.Branch, request.Pat, request.FilePatterns,
                    request.TrackCommitHistory, request.CommitHistoryDepth, ct);
                return Results.Ok(new GitSyncStatusDto
                {
                    Id = result.Id,
                    VaultId = result.VaultId,
                    RepositoryUrl = result.RepositoryUrl,
                    Branch = result.Branch,
                    LastSyncCommitSha = result.LastSyncCommitSha,
                    LastSyncAt = result.LastSyncAt,
                    Status = result.Status,
                    FilePatterns = result.FilePatterns,
                    ErrorMessage = result.ErrorMessage,
                    CreatedAt = result.CreatedAt,
                    TrackCommitHistory = result.TrackCommitHistory,
                    CommitHistoryDepth = result.CommitHistoryDepth
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).Produces<GitSyncStatusDto>();

        // GET /api/v1/vaults/{vaultId}/git-sync — Get config + status
        group.MapGet("/", async (
            HttpContext context,
            Guid vaultId,
            GitSyncService gitSyncService,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden("Admin access required for git sync configuration.");

            var status = await gitSyncService.GetStatusAsync(vaultId, ct);
            return status != null ? Results.Ok(status) : Results.NotFound();
        }).Produces<GitSyncStatusDto>();

        // POST /api/v1/vaults/{vaultId}/git-sync/trigger — Trigger manual sync
        group.MapPost("/trigger", async (
            HttpContext context,
            Guid vaultId,
            GitSyncService gitSyncService,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden("Admin access required for git sync operations.");

            try
            {
                await gitSyncService.TriggerSyncAsync(vaultId, ct);
                return Results.Ok(new { message = "Sync queued successfully." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // GET /api/v1/vaults/{vaultId}/git-sync/history — Sync history
        group.MapGet("/history", async (
            HttpContext context,
            Guid vaultId,
            GitSyncService gitSyncService,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden("Admin access required for git sync operations.");

            var history = await gitSyncService.GetHistoryAsync(vaultId, ct);
            return Results.Ok(history);
        }).Produces<List<GitSyncHistoryDto>>();

        // DELETE /api/v1/vaults/{vaultId}/git-sync — Remove config
        group.MapDelete("/", async (
            HttpContext context,
            Guid vaultId,
            GitSyncService gitSyncService,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden("Admin access required for git sync configuration.");

            var removed = await gitSyncService.RemoveAsync(vaultId, ct);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        // POST /api/v1/vaults/{vaultId}/git-sync/repositories/{repositoryId}/commits/relink
        // Admin-only backfill endpoint for commit → file References edges.
        // Reads changedFilePaths from each commit row's PlatformData and re-runs the
        // shared file-resolution helper. Pre-NODE-3 rows (missing changedFilePaths) are
        // counted as "skipped" — they need a fresh repo sync to recover.
        //
        // WorkGroupID: kc-feat-commit-history-polish-20260411-051000
        // NodeID: NODE-3 CommitBackfillEndpoint
        group.MapPost("/repositories/{repositoryId:guid}/commits/relink", async (
            HttpContext context,
            Guid vaultId,
            Guid repositoryId,
            CommitRelinkService relinkService,
            IVaultAccessService vaultAccessService,
            SelfHostedDbContext db,
            CancellationToken ct) =>
        {
            // Gate 1: admin-only (first, before any DB work).
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden("Admin access required for commit relink.");

            // Gate 2: vault access (same pattern as the rest of the endpoints in this file).
            var accessibleVaultIds = await VaultEndpoints.ResolveAccessibleVaultIdsAsync(
                context, vaultAccessService, ct);
            if (accessibleVaultIds != null && !accessibleVaultIds.Contains(vaultId))
                return Results.Json(new { error = "Access denied to this vault." }, statusCode: 403);

            // Gate 3: repository must exist AND belong to the named vault.
            var repo = await db.GitRepositories
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(g => g.Id == repositoryId, ct);
            if (repo == null || repo.VaultId != vaultId)
                return Results.NotFound(new { error = "Repository not found in this vault." });

            try
            {
                var result = await relinkService.RelinkRepositoryAsync(repositoryId, ct);
                return Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .Produces<CommitBackfillResult>()
        .Produces(403)
        .Produces(404);
    }
}
