using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Knowz.Core.Enums;
using Knowz.SelfHosted.API.Models;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;

namespace Knowz.SelfHosted.API.Endpoints;

public static class VaultEndpoints
{
    public static void MapVaultEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/vaults").WithTags("Vaults");

        group.MapGet("/", async (
            VaultService svc,
            IVaultAccessService vaultAccessService,
            HttpContext context,
            bool includeStats = true,
            CancellationToken ct = default) =>
        {
            var result = await svc.ListVaultsAsync(includeStats, ct);

            // Filter by user vault permissions
            var accessibleVaultIds = await ResolveAccessibleVaultIdsAsync(context, vaultAccessService, ct);
            if (accessibleVaultIds != null)
            {
                var filteredVaults = result.Vaults.Where(v => accessibleVaultIds.Contains(v.Id)).ToList();
                result = new VaultListResponse(filteredVaults);
            }

            return Results.Ok(result);
        }).Produces<VaultListResponse>();

        group.MapGet("/{id:guid}", async (
            VaultService svc,
            IVaultAccessService vaultAccessService,
            HttpContext context,
            Guid id,
            CancellationToken ct = default) =>
        {
            // Check vault access
            var hasAccess = await HasVaultAccessAsync(context, vaultAccessService, id, ct: ct);
            if (!hasAccess)
                return Results.Json(new { error = "Access denied to this vault." }, statusCode: 403);

            var result = await svc.GetVaultAsync(id, ct);
            return result is not null ? Results.Ok(result) : Results.NotFound();
        }).Produces<VaultResponse>().Produces(404);

        group.MapGet("/{id:guid}/contents", async (
            VaultService svc,
            IVaultAccessService vaultAccessService,
            HttpContext context,
            Guid id,
            bool includeChildren = true,
            int limit = 100,
            CancellationToken ct = default) =>
        {
            // Check vault access
            var hasAccess = await HasVaultAccessAsync(context, vaultAccessService, id, ct: ct);
            if (!hasAccess)
                return Results.Json(new { error = "Access denied to this vault." }, statusCode: 403);

            limit = Math.Clamp(limit, 1, 100);
            var result = await svc.ListVaultContentsAsync(id, includeChildren, limit, ct);
            return Results.Ok(result);
        }).Produces<VaultContentsResponse>();

        group.MapPost("/", async (
            VaultService svc,
            IVaultAccessService vaultAccessService,
            HttpContext context,
            CreateVaultRequest req,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "name is required" });

            // Check CanCreateVaults permission
            var userId = GetUserIdFromContext(context);
            if (userId.HasValue)
            {
                var canCreate = await vaultAccessService.CanCreateVaultsAsync(userId.Value, ct);
                if (!canCreate)
                    return Results.Json(new { error = "You do not have permission to create vaults." }, statusCode: 403);
            }

            var result = await svc.CreateVaultAsync(
                req.Name, req.Description, req.ParentVaultId, req.VaultType, ct);
            return Results.Created($"/api/v1/vaults/{result.Id}", result);
        }).Produces<CreateVaultResult>(201).Produces(400);

        group.MapPut("/{id:guid}", async (
            VaultService svc,
            IVaultAccessService vaultAccessService,
            HttpContext context,
            Guid id,
            UpdateVaultRequest req,
            CancellationToken ct) =>
        {
            var hasAccess = await HasVaultAccessAsync(context, vaultAccessService, id, requireManage: true, ct: ct);
            if (!hasAccess)
                return Results.Json(new { error = "Access denied to this vault." }, statusCode: 403);

            var result = await svc.UpdateVaultAsync(id, req.Name, req.Description, ct);
            return result is not null ? Results.Ok(result) : Results.NotFound();
        }).Produces<UpdateVaultResult>().Produces(404);

        group.MapDelete("/{id:guid}", async (
            VaultService svc,
            IVaultAccessService vaultAccessService,
            HttpContext context,
            Guid id,
            CancellationToken ct) =>
        {
            var hasAccess = await HasVaultAccessAsync(context, vaultAccessService, id, requireDelete: true, ct: ct);
            if (!hasAccess)
                return Results.Json(new { error = "Access denied to this vault." }, statusCode: 403);

            var result = await svc.DeleteVaultAsync(id, ct);
            return result is not null ? Results.Ok(result) : Results.NotFound();
        }).Produces<DeleteVaultResult>().Produces(404);

        group.MapGet("/{id:guid}/knowledge-item-types", async (
            IVaultAccessService vaultAccessService,
            HttpContext context,
            Guid id,
            CancellationToken ct) =>
        {
            var hasAccess = await HasVaultAccessAsync(context, vaultAccessService, id, ct: ct);
            if (!hasAccess)
                return Results.Json(new { error = "Access denied to this vault." }, statusCode: 403);

            // Return all knowledge types (vault-specific filtering can be added later)
            var types = Enum.GetValues<KnowledgeType>()
                .Select(t => new { name = t.ToString(), value = (int)t })
                .ToList();

            return Results.Ok(new { success = true, data = types });
        });
    }

    internal static Guid? GetUserIdFromContext(HttpContext context)
    {
        var claim = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                 ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return !string.IsNullOrEmpty(claim) && Guid.TryParse(claim, out var id) ? id : null;
    }

    internal static Guid? GetTenantIdFromContext(HttpContext context)
    {
        var claim = context.User.FindFirst("tenantId")?.Value;
        return !string.IsNullOrEmpty(claim) && Guid.TryParse(claim, out var id) ? id : null;
    }

    /// <summary>
    /// Returns null if user has unrestricted access, or a list of accessible vault IDs.
    /// Returns empty list (deny all) if userId is present but tenantId is missing.
    /// </summary>
    internal static async Task<List<Guid>?> ResolveAccessibleVaultIdsAsync(
        HttpContext context, IVaultAccessService vaultAccessService, CancellationToken ct = default)
    {
        var userId = GetUserIdFromContext(context);
        if (!userId.HasValue) return null;

        var hasAll = await vaultAccessService.HasAllVaultsAccessAsync(userId.Value, ct);
        if (hasAll) return null;

        var tenantId = GetTenantIdFromContext(context);
        // Fix D: null tenantId with a restricted user = deny all, not unrestricted
        if (!tenantId.HasValue) return new List<Guid>();

        return await vaultAccessService.GetAccessibleVaultIdsAsync(userId.Value, tenantId.Value, ct: ct);
    }

    internal static async Task<bool> HasVaultAccessAsync(
        HttpContext context, IVaultAccessService vaultAccessService, Guid vaultId,
        bool requireWrite = false, bool requireDelete = false, bool requireManage = false,
        CancellationToken ct = default)
    {
        var userId = GetUserIdFromContext(context);
        if (!userId.HasValue) return true; // No user context (API key auth) = full access

        return await vaultAccessService.HasVaultAccessAsync(
            userId.Value, vaultId, requireWrite, requireDelete, requireManage, ct);
    }
}
