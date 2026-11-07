namespace Knowz.SelfHosted.API.Endpoints;

using System.Reflection;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.Core.Schema;
using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Passive receive surface so a peer (portable / platform / another self-hosted instance) can
/// pull from or push to THIS instance — ADR_G6_Allowlisted_Peer_Destinations (#1043).
///
/// Mirrors the SaaS/portable vault-sync contract in <c>src/Knowz.API/Endpoints/SyncEndpoints.cs</c>
/// (the routes <c>PlatformSyncClient</c> and the platform federation transport already call):
/// <c>GET /schema</c>, <c>POST /vaults/{id}/register</c>, <c>GET /vaults/{id}/schema</c>,
/// <c>GET /vaults/{id}/export</c>, <c>GET /vaults/{id}/items/{kid}/export</c>,
/// <c>POST /vaults/{id}/import</c>. Responses use the <c>{ success, data }</c> envelope both
/// clients parse.
///
/// Auth: an authenticated Knowz user with a <c>sub</c> claim (JWT or personal <c>ksh_</c> key) —
/// the SH equivalent of the SaaS "UserId claim required" rule. Vault access is gated through
/// <see cref="IVaultAccessService"/> exactly as on SaaS. The file lane
/// (<c>/vaults/{id}/files/*</c>) is NOT part of this vertical — see the ADR's Debt &amp; Gaps.
/// </summary>
public static class SyncReceiveEndpoints
{
    internal const int MaxExportPageSize = 500;

    /// <summary>Header set on 403 when the caller lacks a user identity (mirrors SaaS).</summary>
    public const string AuthRequirementHeader = "X-Knowz-Auth-Requirement";

    public static void MapSyncReceiveEndpoints(this WebApplication app, RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (invocation, next) => {
            try { return await next(invocation); }
            catch (ArchiveOperationException ex) when (ex.StatusCode == 409) { return ArchiveRequired(); }
        });
        // GET /api/v1/sync/schema — schema handshake used by connection tests.
        group.MapGet("/schema", (
            HttpContext context,
            ITenantProvider tenantProvider) =>
        {
            if (!TryRequirePeerCaller(context, out _, out var denied))
                return denied!;

            context.Response.Headers.CacheControl = "no-store";
            return Ok(new
            {
                version = CoreSchema.Version,
                minReadableVersion = CoreSchema.MinReadableVersion,
                compatibility = CoreSchema.GetCompatibilityInfo(),
                serverVersion = GetServerVersion(),
                schemaVersion = CoreSchema.Version.ToString(),
                serverTime = DateTime.UtcNow,
                userIdClaimRequired = true,
                tenantId = tenantProvider.TenantId,
                sourceEdition = "selfhosted",
                capabilities = new[] { "knowz.archive-peer/v1" },
            });
        }).WithName("SelfHostedGetSyncSchema");

        // POST /api/v1/sync/vaults/{vaultId}/register — acknowledged, NOT persisted (SH has no
        // SyncPartners table). Kept so peers that register before their first push succeed.
        group.MapPost("/vaults/{vaultId:guid}/register", async (
            Guid vaultId,
            [FromBody] PeerRegisterRequest? request,
            HttpContext context,
            SelfHostedDbContext db,
            IVaultAccessService vaultAccess,
            CancellationToken ct) =>
        {
            if (!TryRequirePeerCaller(context, out var userId, out var denied))
                return denied!;

            if (!await vaultAccess.HasVaultAccessAsync(userId, vaultId, requireWrite: true, ct: ct))
                return Fail(StatusCodes.Status403Forbidden, "No write access to vault");

            var vault = await db.Vaults.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vaultId, ct);
            if (vault is null)
                return Fail(StatusCodes.Status404NotFound, "Vault not found");

            return Ok(new
            {
                partnerId = vaultId,
                vaultId,
                partnerName = request?.PartnerName ?? string.Empty,
                schemaVersion = CoreSchema.Version,
                persisted = false,
            });
        }).WithName("SelfHostedRegisterSyncPartner");

        // GET /api/v1/sync/vaults/{vaultId}/schema — per-vault manifest (federation probe).
        group.MapGet("/vaults/{vaultId:guid}/schema", async (
            Guid vaultId,
            HttpContext context,
            SelfHostedDbContext db,
            ITenantProvider tenantProvider,
            IVaultAccessService vaultAccess,
            CancellationToken ct) =>
        {
            if (!TryRequirePeerCaller(context, out var userId, out var denied))
                return denied!;

            if (!await vaultAccess.HasVaultAccessAsync(userId, vaultId, ct: ct))
                return Fail(StatusCodes.Status403Forbidden, "No access to vault");

            var vault = await db.Vaults.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vaultId, ct);
            if (vault is null)
                return Fail(StatusCodes.Status404NotFound, "Vault not found");

            var tenantId = tenantProvider.TenantId;
            var knowledgeIds = db.KnowledgeVaults
                .IgnoreQueryFilters()
                .Where(kv => kv.VaultId == vaultId)
                .Select(kv => kv.KnowledgeId);

            var vaultKnowledge = db.KnowledgeItems
                .IgnoreQueryFilters()
                .Where(k => k.TenantId == tenantId && knowledgeIds.Contains(k.Id));

            var itemCount = await vaultKnowledge.CountAsync(k => !k.IsDeleted, ct);
            // Cursor spans soft-deleted rows too — they ride the delta as tombstones.
            var maxUpdatedAt = await vaultKnowledge
                .Select(k => (DateTime?)k.UpdatedAt)
                .MaxAsync(ct);

            context.Response.Headers.CacheControl = "no-store";
            return Ok(new
            {
                remoteVaultId = vaultId,
                name = vault.Name,
                description = vault.Description,
                itemCount,
                estimatedBytes = 0L,
                cursor = maxUpdatedAt?.ToString("O"),
                manifestGeneratedAt = DateTime.UtcNow,
            });
        }).WithName("SelfHostedGetVaultSyncSchema");

        // GET /api/v1/sync/vaults/{vaultId}/export?since=&page=&pageSize= — delta export.
        group.MapGet("/vaults/{vaultId:guid}/export", async (
            Guid vaultId,
            [FromQuery] DateTime? since,
            HttpContext context,
            SelfHostedDbContext db,
            VaultScopedExportService exportService,
            IVaultAccessService vaultAccess,
            CancellationToken ct,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = MaxExportPageSize) =>
        {
            if (!TryRequirePeerCaller(context, out var userId, out var denied))
                return denied!;

            if (!await vaultAccess.HasVaultAccessAsync(userId, vaultId, ct: ct))
                return Fail(StatusCodes.Status403Forbidden, "No access to vault");

            var vaultExists = await db.Vaults.AsNoTracking().AnyAsync(v => v.Id == vaultId, ct);
            if (!vaultExists)
                return Fail(StatusCodes.Status404NotFound, "Vault not found");

            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > MaxExportPageSize) pageSize = MaxExportPageSize;

            var package = await exportService.ExportDeltaAsync(vaultId, since, syncLink: null, ct);
            ApplyPaging(package, page, pageSize);

            context.Response.Headers.CacheControl = "no-store";
            return Ok(package);
        }).WithName("SelfHostedExportVaultDelta");

        // GET /api/v1/sync/vaults/{vaultId}/items/{knowledgeId}/export — single-item export.
        group.MapGet("/vaults/{vaultId:guid}/items/{knowledgeId:guid}/export", async (
            Guid vaultId,
            Guid knowledgeId,
            HttpContext context,
            SelfHostedDbContext db,
            VaultScopedExportService exportService,
            IVaultAccessService vaultAccess,
            CancellationToken ct) =>
        {
            if (!TryRequirePeerCaller(context, out var userId, out var denied))
                return denied!;

            if (!await vaultAccess.HasVaultAccessAsync(userId, vaultId, ct: ct))
                return Fail(StatusCodes.Status403Forbidden, "No access to vault");

            var vaultExists = await db.Vaults.AsNoTracking().AnyAsync(v => v.Id == vaultId, ct);
            if (!vaultExists)
                return Fail(StatusCodes.Status404NotFound, "Vault not found");

            try
            {
                var package = await exportService.ExportSingleItemAsync(vaultId, knowledgeId, ct);
                context.Response.Headers.CacheControl = "no-store";
                return Ok(package);
            }
            catch (ArchiveOperationException ex) when (ex.StatusCode == 409) { return ArchiveRequired(); }
            catch (InvalidOperationException)
            {
                // Not a member of the vault, or deleted — never echo internals.
                return Fail(StatusCodes.Status404NotFound, "Knowledge item not found");
            }
        }).WithName("SelfHostedExportVaultItem");

        // POST /api/v1/sync/vaults/{vaultId}/import — delta import (peer push).
        group.MapPost("/vaults/{vaultId:guid}/import", async (
            Guid vaultId,
            [FromBody] PortableExportPackage? package,
            HttpContext context,
            SelfHostedDbContext db,
            IVaultSyncOrchestrator orchestrator,
            IVaultAccessService vaultAccess,
            CancellationToken ct) =>
        {
            if (!TryRequirePeerCaller(context, out var userId, out var denied))
                return denied!;

            if (package?.Data is null)
                return Fail(StatusCodes.Status400BadRequest, "A sync package body is required");

            if (!await vaultAccess.HasVaultAccessAsync(userId, vaultId, requireWrite: true, ct: ct))
                return Fail(StatusCodes.Status403Forbidden, "No write access to vault");

            var vaultExists = await db.Vaults.AsNoTracking().AnyAsync(v => v.Id == vaultId, ct);
            if (!vaultExists)
                return Fail(StatusCodes.Status404NotFound, "Vault not found");

            if (SelfHostedInlineSyncPolicy.HasProtectedGraph(package) || await SelfHostedInlineSyncPolicy.IsProtectedVaultAsync(db, AuthorizationHelpers.GetCallerTenantId(context)!.Value, vaultId, ct)) return ArchiveRequired();
            if (!CoreSchema.CanRead(package.SchemaVersion))
            {
                return Fail(StatusCodes.Status400BadRequest,
                    $"Package schema v{package.SchemaVersion} is not readable by this instance ({CoreSchema.GetCompatibilityInfo()})");
            }

            // Same 100-items-per-run cap the initiator enforces (V-SEC-09 / G7 honesty). A peer
            // cannot bypass it by pushing a larger package.
            if (package.Data.KnowledgeItems.Count > VaultSyncOrchestrator.BulkItemCap)
            {
                return Fail(StatusCodes.Status400BadRequest,
                    $"Package exceeds the {VaultSyncOrchestrator.BulkItemCap}-item per-run limit " +
                    $"({package.Data.KnowledgeItems.Count} items). Split the push and re-run.");
            }

            var result = await orchestrator.ImportSyncDeltaAsync(package, vaultId, ct);
            var body = new
            {
                success = result.Success,
                accepted = result.Accepted,
                skipped = result.Skipped,
                tombstonesApplied = result.TombstonesApplied,
                details = result.Details,
                serverTimestamp = DateTime.UtcNow,
            };

            context.Response.Headers.CacheControl = "no-store";
            return result.Success
                ? Ok(body)
                : Results.Json(
                    new { success = false, error = result.Error ?? "Import failed", errors = new[] { result.Error ?? "Import failed" }, data = body },
                    statusCode: StatusCodes.Status400BadRequest);
        }).WithName("SelfHostedImportVaultDelta");
    }

    private static IResult ArchiveRequired() => Results.Json(new { success = false, error = SelfHostedInlineSyncPolicy.Message,
        code = "archive-peer-required", requiredCapability = "knowz.archive-peer/v1", preservationState = "blocked" }, statusCode: 409);

    /// <summary>Body of POST /vaults/{id}/register (mirrors <c>RegisterSyncPartnerRequest</c>).</summary>
    public sealed record PeerRegisterRequest(string? PartnerName);

    /// <summary>
    /// Authenticated Knowz user WITH a user identity (<c>sub</c>). LegacyApiKey callers and
    /// principals without a user id get the same 403 + header the SaaS receive routes emit.
    /// </summary>
    private static bool TryRequirePeerCaller(HttpContext context, out Guid userId, out IResult? denied)
    {
        userId = Guid.Empty;
        denied = null;

        if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(context))
        {
            denied = Fail(StatusCodes.Status403Forbidden, "Forbidden.");
            return false;
        }

        var callerId = AuthorizationHelpers.GetCallerId(context);
        if (callerId is null)
        {
            context.Response.Headers[AuthRequirementHeader] = "user-api-key";
            denied = Fail(StatusCodes.Status403Forbidden,
                "Authenticated principal lacks a user identity required for vault-scoped sync.");
            return false;
        }

        userId = callerId.Value;
        return true;
    }

    /// <summary>
    /// <see cref="VaultScopedExportService.ExportDeltaAsync"/> is not paged; slice the knowledge
    /// list so a paginating puller (SH pull loop: "fewer than pageSize → done") terminates.
    /// Tombstones and referenced entities travel on page 1 only.
    /// </summary>
    internal static void ApplyPaging(PortableExportPackage package, int page, int pageSize)
    {
        var all = package.Data.KnowledgeItems;
        var skip = (page - 1) * pageSize;
        package.Data.KnowledgeItems = skip >= all.Count
            ? new List<PortableKnowledge>()
            : all.Skip(skip).Take(pageSize).ToList();
        package.Metadata.TotalKnowledgeItems = package.Data.KnowledgeItems.Count;

        if (page > 1)
        {
            package.Tombstones = null;
            package.Data.Topics = new();
            package.Data.Tags = new();
            package.Data.Persons = new();
            package.Data.Locations = new();
            package.Data.Events = new();
            package.Data.Comments = new();
            package.Data.FileRecords = new();
        }
    }

    private static IResult Ok<T>(T data) =>
        Results.Ok(new { success = true, data });

    private static IResult Fail(int statusCode, string error) =>
        Results.Json(new { success = false, error, errors = new[] { error } }, statusCode: statusCode);

    private static string GetServerVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
}
