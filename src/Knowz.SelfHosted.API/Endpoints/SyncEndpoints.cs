namespace Knowz.SelfHosted.API.Endpoints;

using Knowz.Core.Interfaces;
using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

public static class SyncEndpoints
{
    public static void MapSyncEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sync")
            .WithTags("Sync");

        // GET /api/v1/sync/links — List all sync links
        group.MapGet("/links", async (
            HttpContext context,
            IVaultSyncOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(context))
                return AuthorizationHelpers.Forbidden();

            var links = await orchestrator.ListLinksAsync(ct);
            return Results.Ok(links);
        }).Produces<List<VaultSyncStatusDto>>();

        // GET /api/v1/sync/links/{localVaultId} — Get sync status for a vault
        group.MapGet("/links/{localVaultId:guid}", async (
            Guid localVaultId,
            HttpContext context,
            IVaultSyncOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(context))
                return AuthorizationHelpers.Forbidden();

            var status = await orchestrator.GetStatusAsync(localVaultId, ct);
            return status != null ? Results.Ok(status) : Results.NotFound();
        }).Produces<VaultSyncStatusDto>();

        // POST /api/v1/sync/links — Establish a new sync link
        group.MapPost("/links", async (
            HttpContext context,
            [FromBody] EstablishSyncLinkRequest request,
            IVaultSyncOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(context))
                return AuthorizationHelpers.Forbidden();

            try
            {
                var link = await orchestrator.EstablishLinkAsync(request, ct);
                return Results.Created($"/api/v1/sync/links/{link.LocalVaultId}", link);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).Produces<VaultSyncStatusDto>(StatusCodes.Status201Created);

        // DELETE /api/v1/sync/links/{localVaultId} — Remove a sync link
        group.MapDelete("/links/{localVaultId:guid}", async (
            Guid localVaultId,
            HttpContext context,
            IVaultSyncOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(context))
                return AuthorizationHelpers.Forbidden();

            var removed = await orchestrator.RemoveLinkAsync(localVaultId, ct);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        // POST /api/v1/sync/run/{localVaultId} — Trigger a sync operation
        group.MapPost("/run/{localVaultId:guid}", async (
            Guid localVaultId,
            HttpContext context,
            [FromBody] TriggerSyncRequest? request,
            IVaultSyncOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(context))
                return AuthorizationHelpers.Forbidden();

            var direction = request?.Direction ?? SyncDirection.Full;
            try
            {
                var result = await orchestrator.SyncAsync(localVaultId, direction, ct);
                return result.Success
                    ? Results.Ok(result)
                    : Results.UnprocessableEntity(result);
            }
            catch (RateLimitExceededException ex)
            {
                return MapRateLimitError(context, ex);
            }
        }).Produces<VaultSyncResult>()
          .Produces<VaultSyncResult>(StatusCodes.Status422UnprocessableEntity)
          .Produces(StatusCodes.Status429TooManyRequests);

        // --- Single-item pull/push (NodeID: PlatformSyncItemOps) ---

        // POST /api/v1/sync/links/{linkId}/pull-item — Pull a single knowledge item
        group.MapPost("/links/{linkId:guid}/pull-item", async (
            Guid linkId,
            HttpContext context,
            [FromBody] SyncItemRequest? request,
            IVaultSyncOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(context))
                return AuthorizationHelpers.Forbidden();
            if (request is null || request.KnowledgeId == Guid.Empty)
                return Results.BadRequest(new { error = "knowledgeId required" });

            try
            {
                var result = await orchestrator.SyncItemAsync(
                    linkId, request.KnowledgeId, SyncItemDirection.Pull, request.OverwriteLocal, ct);
                return MapSyncItemResult(result);
            }
            catch (RateLimitExceededException ex)
            {
                return MapRateLimitError(context, ex);
            }
        }).Produces<SyncItemResult>()
          .Produces(StatusCodes.Status429TooManyRequests);

        // POST /api/v1/sync/links/{linkId}/push-item — Push a single knowledge item
        group.MapPost("/links/{linkId:guid}/push-item", async (
            Guid linkId,
            HttpContext context,
            [FromBody] SyncItemRequest? request,
            IVaultSyncOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(context))
                return AuthorizationHelpers.Forbidden();
            if (request is null || request.KnowledgeId == Guid.Empty)
                return Results.BadRequest(new { error = "knowledgeId required" });

            try
            {
                var result = await orchestrator.SyncItemAsync(
                    linkId, request.KnowledgeId, SyncItemDirection.Push, overwriteLocal: false, ct);
                return MapSyncItemResult(result);
            }
            catch (RateLimitExceededException ex)
            {
                return MapRateLimitError(context, ex);
            }
        }).Produces<SyncItemResult>()
          .Produces(StatusCodes.Status429TooManyRequests);

        // --- Platform Browse Proxy (NodeID: PlatformBrowsing) ---
        // ADR_G6 (#1043): every browse route accepts an optional ?connectionId= so a non-primary
        // (allowlisted peer) connection can be browsed when establishing a link. Omitted → primary.

        // GET /api/v1/sync/platform/vaults — List vaults on the platform
        group.MapGet("/platform/vaults", async (
            HttpContext context,
            IPlatformSyncClient platformClient,
            SelfHostedDbContext db,
            IPlatformConnectionService connectionService,
            IPlatformAuditLog auditLog,
            [FromQuery] Guid? connectionId,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(context))
                return AuthorizationHelpers.Forbidden();

            var credential = await ResolvePlatformCredentialAsync(db, connectionService, connectionId, ct);
            if (credential is null)
            {
                return Results.Json(
                    new { error = "Platform connection not configured" },
                    statusCode: StatusCodes.Status412PreconditionFailed);
            }

            var start = new PlatformSyncRunStart(
                AuthorizationHelpers.GetCallerId(context) ?? Guid.Empty,
                GetCallerEmail(context),
                PlatformSyncOperation.BrowseVaults,
                PlatformSyncDirection.None);

            try
            {
                var result = await platformClient.ListPlatformVaultsAsync(
                    credential.Value.Url, credential.Value.ApiKey, ct);
                context.Response.Headers.CacheControl = "no-store";
                await TryLogBrowseAuditAsync(auditLog, start, PlatformSyncRunStatus.Succeeded, null, ct);
                return Results.Ok(result);
            }
            catch (PlatformBrowseException ex)
            {
                await TryLogBrowseAuditAsync(auditLog, start, PlatformSyncRunStatus.Failed, ex.Message, ct);
                return MapBrowseError(ex);
            }
        }).Produces<PlatformVaultListDto>();

        // GET /api/v1/sync/platform/vaults/{vaultId}/knowledge — List knowledge items in a platform vault
        group.MapGet("/platform/vaults/{vaultId:guid}/knowledge", async (
            Guid vaultId,
            HttpContext context,
            IPlatformSyncClient platformClient,
            SelfHostedDbContext db,
            IPlatformConnectionService connectionService,
            IPlatformAuditLog auditLog,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            [FromQuery] string? search,
            [FromQuery] Guid? connectionId,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(context))
                return AuthorizationHelpers.Forbidden();

            var effectivePage = page ?? 1;
            var effectivePageSize = pageSize ?? 50;

            if (effectivePage < 1)
                return Results.BadRequest(new { error = "page must be >= 1" });
            if (effectivePageSize <= 0)
                return Results.BadRequest(new { error = "pageSize must be > 0" });
            if (effectivePageSize > 100)
                return Results.BadRequest(new { error = "pageSize must be <= 100" });

            var credential = await ResolvePlatformCredentialAsync(db, connectionService, connectionId, ct);
            if (credential is null)
            {
                return Results.Json(
                    new { error = "Platform connection not configured" },
                    statusCode: StatusCodes.Status412PreconditionFailed);
            }

            var start = new PlatformSyncRunStart(
                AuthorizationHelpers.GetCallerId(context) ?? Guid.Empty,
                GetCallerEmail(context),
                PlatformSyncOperation.BrowseKnowledge,
                PlatformSyncDirection.None);

            try
            {
                var result = await platformClient.ListPlatformKnowledgeAsync(
                    credential.Value.Url, credential.Value.ApiKey,
                    vaultId, effectivePage, effectivePageSize, search, ct);
                context.Response.Headers.CacheControl = "no-store";
                await TryLogBrowseAuditAsync(auditLog, start, PlatformSyncRunStatus.Succeeded, null, ct);
                return Results.Ok(result);
            }
            catch (PlatformBrowseException ex)
            {
                await TryLogBrowseAuditAsync(auditLog, start, PlatformSyncRunStatus.Failed, ex.Message, ct);
                return MapBrowseError(ex);
            }
        }).Produces<PlatformKnowledgeListDto>();

        // GET /api/v1/sync/platform/knowledge/{knowledgeId} — Get a single knowledge item detail
        group.MapGet("/platform/knowledge/{knowledgeId:guid}", async (
            Guid knowledgeId,
            HttpContext context,
            IPlatformSyncClient platformClient,
            SelfHostedDbContext db,
            IPlatformConnectionService connectionService,
            [FromQuery] Guid? connectionId,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(context))
                return AuthorizationHelpers.Forbidden();

            var credential = await ResolvePlatformCredentialAsync(db, connectionService, connectionId, ct);
            if (credential is null)
            {
                return Results.Json(
                    new { error = "Platform connection not configured" },
                    statusCode: StatusCodes.Status412PreconditionFailed);
            }

            try
            {
                var result = await platformClient.GetPlatformKnowledgeAsync(
                    credential.Value.Url, credential.Value.ApiKey, knowledgeId, ct);
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(result);
            }
            catch (PlatformBrowseException ex)
            {
                return MapBrowseError(ex);
            }
        }).Produces<PlatformKnowledgeDetailDto>();

        // GET /api/v1/sync/history — Paginated platform sync audit log for the current tenant
        group.MapGet("/history", async (
            HttpContext context,
            IPlatformAuditLog auditLog,
            ITenantProvider tenantProvider,
            CancellationToken ct,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            [FromQuery] Guid? vaultSyncLinkId = null) =>
        {
            if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(context))
                return AuthorizationHelpers.Forbidden();

            var rows = await auditLog.GetHistoryAsync(
                tenantProvider.TenantId, page, pageSize, ct, vaultSyncLinkId);
            return Results.Ok(rows);
        }).Produces<IReadOnlyList<PlatformSyncRunDto>>();

        // --- Platform Connection (NodeID: PlatformSyncConnection) ---

        // GET /api/v1/sync/connection — Current tenant's stored platform connection (masked).
        group.MapGet("/connection", async (
            HttpContext context,
            IPlatformConnectionService connectionService,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(context))
                return AuthorizationHelpers.Forbidden();

            var dto = await connectionService.GetAsync(ct);
            if (dto is null)
                return Results.NotFound();

            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(dto);
        }).Produces<PlatformConnectionDto>()
          .Produces(StatusCodes.Status404NotFound);

        // PUT /api/v1/sync/connection — Upsert the tenant's platform connection.
        group.MapPut("/connection", async (
            HttpContext context,
            [FromBody] UpsertPlatformConnectionRequest request,
            IPlatformConnectionService connectionService,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(context))
                return AuthorizationHelpers.Forbidden();

            if (RejectSelfPoint(context, request.PlatformApiUrl) is { } selfPoint)
                return selfPoint;

            var userId = AuthorizationHelpers.GetCallerId(context) ?? Guid.Empty;
            try
            {
                var dto = await connectionService.UpsertAsync(request, userId, ct);
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(dto);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).Produces<PlatformConnectionDto>();

        // DELETE /api/v1/sync/connection — Remove the tenant's platform connection.
        // Returns 409 if any VaultSyncLink still references it.
        group.MapDelete("/connection", async (
            HttpContext context,
            IPlatformConnectionService connectionService,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(context))
                return AuthorizationHelpers.Forbidden();

            try
            {
                await connectionService.DeleteAsync(
                    AuthorizationHelpers.GetCallerId(context),
                    GetCallerEmail(context),
                    ct);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        // POST /api/v1/sync/connection/test — Test the currently stored connection.
        group.MapPost("/connection/test", async (
            HttpContext context,
            IPlatformConnectionService connectionService,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(context))
                return AuthorizationHelpers.Forbidden();

            var existing = await connectionService.GetAsync(ct);
            if (existing is not null && RejectSelfPoint(context, existing.PlatformApiUrl) is { } selfPoint)
                return selfPoint;

            var result = await connectionService.TestAsync(
                AuthorizationHelpers.GetCallerId(context),
                GetCallerEmail(context),
                ct);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(result);
        }).Produces<PlatformConnectionTestResult>();

        // POST /api/v1/sync/connection/test-candidate — Test a candidate URL + key without persisting.
        group.MapPost("/connection/test-candidate", async (
            HttpContext context,
            [FromBody] TestCandidateConnectionRequest request,
            IPlatformConnectionService connectionService,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(context))
                return AuthorizationHelpers.Forbidden();

            if (RejectSelfPoint(context, request.PlatformApiUrl) is { } selfPoint)
                return selfPoint;

            var result = await connectionService.TestCandidateAsync(
                request.PlatformApiUrl, request.ApiKey,
                AuthorizationHelpers.GetCallerId(context),
                GetCallerEmail(context),
                ct);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(result);
        }).Produces<PlatformConnectionTestResult>();

        // --- Multi-connection (ADR_G6_Allowlisted_Peer_Destinations, #1043) ---
        // Additive. The singleton-shaped /connection* routes above keep addressing the tenant's
        // PRIMARY connection so the Cloud Destinations UI is unchanged.
        // UI_SH_PeerDestination: consumed by the SH web Destinations UI (ConnectionList /
        // LinkVaultModal / BrowsePlatformModal picker). Portable↔SH / SH↔SH live smoke remains open (#1043).

        // GET /api/v1/sync/connections — All of the tenant's connections (masked), oldest first.
        group.MapGet("/connections", async (
            HttpContext context,
            IPlatformConnectionService connectionService,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(context))
                return AuthorizationHelpers.Forbidden();

            var list = await connectionService.ListAsync(ct);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(list);
        }).Produces<IReadOnlyList<PlatformConnectionDto>>();

        // POST /api/v1/sync/connections — Create an additional connection (API key required).
        // 400 on URL validation failure (incl. host not in Sync:AllowedPlatformHosts) or
        // duplicate (tenant, URL).
        group.MapPost("/connections", async (
            HttpContext context,
            [FromBody] UpsertPlatformConnectionRequest request,
            IPlatformConnectionService connectionService,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(context))
                return AuthorizationHelpers.Forbidden();

            if (RejectSelfPoint(context, request.PlatformApiUrl) is { } selfPoint)
                return selfPoint;

            var userId = AuthorizationHelpers.GetCallerId(context) ?? Guid.Empty;
            try
            {
                var dto = await connectionService.CreateAsync(request, userId, ct);
                context.Response.Headers.CacheControl = "no-store";
                return Results.Created($"/api/v1/sync/connections/{dto.Id}", dto);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).Produces<PlatformConnectionDto>(StatusCodes.Status201Created);

        // GET /api/v1/sync/connections/{id}
        group.MapGet("/connections/{connectionId:guid}", async (
            Guid connectionId,
            HttpContext context,
            IPlatformConnectionService connectionService,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(context))
                return AuthorizationHelpers.Forbidden();

            var dto = await connectionService.GetByIdAsync(connectionId, ct);
            if (dto is null)
                return Results.NotFound();

            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(dto);
        }).Produces<PlatformConnectionDto>()
          .Produces(StatusCodes.Status404NotFound);

        // PUT /api/v1/sync/connections/{id} — URL / display name; key only when supplied.
        group.MapPut("/connections/{connectionId:guid}", async (
            Guid connectionId,
            HttpContext context,
            [FromBody] UpsertPlatformConnectionRequest request,
            IPlatformConnectionService connectionService,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(context))
                return AuthorizationHelpers.Forbidden();

            if (RejectSelfPoint(context, request.PlatformApiUrl) is { } selfPoint)
                return selfPoint;

            var userId = AuthorizationHelpers.GetCallerId(context) ?? Guid.Empty;
            try
            {
                var dto = await connectionService.UpdateAsync(connectionId, request, userId, ct);
                if (dto is null)
                    return Results.NotFound();

                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(dto);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).Produces<PlatformConnectionDto>()
          .Produces(StatusCodes.Status404NotFound);

        // DELETE /api/v1/sync/connections/{id} — 409 while any VaultSyncLink references it.
        group.MapDelete("/connections/{connectionId:guid}", async (
            Guid connectionId,
            HttpContext context,
            IPlatformConnectionService connectionService,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(context))
                return AuthorizationHelpers.Forbidden();

            try
            {
                var removed = await connectionService.DeleteByIdAsync(
                    connectionId,
                    AuthorizationHelpers.GetCallerId(context),
                    GetCallerEmail(context),
                    ct);
                return removed ? Results.NoContent() : Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        // POST /api/v1/sync/connections/{id}/test — Test one stored connection.
        group.MapPost("/connections/{connectionId:guid}/test", async (
            Guid connectionId,
            HttpContext context,
            IPlatformConnectionService connectionService,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(context))
                return AuthorizationHelpers.Forbidden();

            var existing = await connectionService.GetByIdAsync(connectionId, ct);
            if (existing is not null && RejectSelfPoint(context, existing.PlatformApiUrl) is { } selfPoint)
                return selfPoint;

            var result = await connectionService.TestByIdAsync(
                connectionId,
                AuthorizationHelpers.GetCallerId(context),
                GetCallerEmail(context),
                ct);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(result);
        }).Produces<PlatformConnectionTestResult>();

        // Passive receive surface for peers (ADR_G6, #1043).
        app.MapSyncReceiveEndpoints(group);
    }

    private static string? GetCallerEmail(HttpContext context)
    {
        var claim = context.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? context.User.FindFirst("email")?.Value;
        return string.IsNullOrWhiteSpace(claim) ? null : claim;
    }

    /// <summary>
    /// Fire-and-forget audit row for a browse operation. Swallows audit-log exceptions so a
    /// broken audit table does not turn every browse into a 500.
    /// </summary>
    private static async Task TryLogBrowseAuditAsync(
        IPlatformAuditLog auditLog,
        PlatformSyncRunStart start,
        PlatformSyncRunStatus status,
        string? errorMessage,
        CancellationToken ct)
    {
        try
        {
            await auditLog.LogAsync(start, status, errorMessage, ct);
        }
        catch
        {
            // Swallowed — audit failures never fail the primary browse response.
        }
    }

    /// <summary>
    /// Resolves the platform API URL + API key for the current tenant.
    /// With an explicit <paramref name="connectionId"/> resolves that connection; otherwise
    /// prefers the tenant's PRIMARY <c>PlatformConnection</c> row via
    /// <see cref="IPlatformConnectionService.ResolveForOutboundCallAsync(CancellationToken)"/>
    /// and falls back to the obsolete per-link columns for rows not yet migrated.
    /// </summary>
    private static async Task<(string Url, string ApiKey)?> ResolvePlatformCredentialAsync(
        SelfHostedDbContext db,
        IPlatformConnectionService connectionService,
        Guid? connectionId,
        CancellationToken ct)
    {
        // ADR_G6 (#1043): explicit connection id → that connection only (no primary fallback,
        // so a typo can never silently browse a different destination).
        if (connectionId is Guid id)
            return await connectionService.ResolveForOutboundCallAsync(id, ct);

        var resolved = await connectionService.ResolveForOutboundCallAsync(ct);
        if (resolved is { } tuple)
            return tuple;

#pragma warning disable CS0618 // Legacy fallback for rows not yet migrated to PlatformConnection.
        var link = await db.VaultSyncLinks
            .AsNoTracking()
            .OrderByDescending(l => l.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        if (link is null
            || string.IsNullOrWhiteSpace(link.PlatformApiUrl)
            || string.IsNullOrWhiteSpace(link.ApiKeyEncrypted))
        {
            return null;
        }

        return (link.PlatformApiUrl, link.ApiKeyEncrypted);
#pragma warning restore CS0618
    }

    private static IResult MapBrowseError(PlatformBrowseException ex)
    {
        return ex.Kind switch
        {
            PlatformBrowseErrorKind.NotConfigured => Results.Json(
                new { error = ex.Message },
                statusCode: StatusCodes.Status412PreconditionFailed),
            PlatformBrowseErrorKind.Unauthorized => Results.Json(
                new { error = ex.Message },
                statusCode: StatusCodes.Status502BadGateway),
            PlatformBrowseErrorKind.NotFound => Results.NotFound(new { error = ex.Message }),
            PlatformBrowseErrorKind.InvalidRequest => Results.BadRequest(new { error = ex.Message }),
            _ => Results.Json(
                new { error = ex.Message },
                statusCode: StatusCodes.Status502BadGateway)
        };
    }

    /// <summary>
    /// Maps a RateLimitExceededException to HTTP 429 with a Retry-After header (V-SEC-09).
    /// </summary>
    private static IResult MapRateLimitError(HttpContext context, RateLimitExceededException ex)
    {
        if (ex.RetryAfter.HasValue)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(ex.RetryAfter.Value.TotalSeconds));
            context.Response.Headers["Retry-After"] = seconds.ToString();
        }
        return Results.Json(
            new { error = ex.Message, reason = ex.Reason.ToString() },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    /// <summary>
    /// Maps a SyncItemResult to the appropriate HTTP response. Successful outcomes return 200;
    /// NotFound returns 404; everything else returns 422 with the result body.
    /// </summary>
    private static IResult MapSyncItemResult(SyncItemResult result)
    {
        if (result.Success)
            return Results.Ok(result);
        return result.Outcome switch
        {
            SyncItemOutcome.NotFound => Results.NotFound(result),
            SyncItemOutcome.PermissionDenied => Results.Json(result, statusCode: StatusCodes.Status403Forbidden),
            _ => Results.UnprocessableEntity(result),
        };
    }

    /// <summary>
    /// Reject destinations that point back at this same instance (host+port match).
    /// </summary>
    private static bool IsSelfPoint(HttpContext context, string? destinationUrl)
    {
        if (string.IsNullOrWhiteSpace(destinationUrl)) return false;
        if (!Uri.TryCreate(destinationUrl.Trim(), UriKind.Absolute, out var dest)) return false;
        var req = context.Request;
        var localHost = req.Host.Host;
        var localPort = req.Host.Port
            ?? (string.Equals(req.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
        var destPort = dest.IsDefaultPort
            ? (string.Equals(dest.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
            : dest.Port;
        return string.Equals(localHost, dest.Host, StringComparison.OrdinalIgnoreCase)
               && localPort == destPort;
    }

    private static IResult? RejectSelfPoint(HttpContext context, string? destinationUrl)
    {
        if (!IsSelfPoint(context, destinationUrl)) return null;
        return Results.BadRequest(new { error = "A destination cannot point at this instance itself." });
    }

}
