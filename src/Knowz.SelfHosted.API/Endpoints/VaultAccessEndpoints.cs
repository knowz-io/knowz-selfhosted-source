using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.API.Endpoints;

public static class VaultAccessEndpoints
{
    public static WebApplication MapVaultAccessEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/admin/users/{userId:guid}/vault-access")
            .WithTags("Vault Access");

        // Get user permissions (HasAllVaultsAccess, CanCreateVaults)
        group.MapGet("/permissions", async (
            Guid userId,
            HttpContext context,
            IVaultAccessService vaultAccessService,
            SelfHostedDbContext dbContext) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden();

            var tenantId = VaultEndpoints.GetTenantIdFromContext(context);
            if (tenantId == null)
                return Results.Json(new { error = "Tenant context required." }, statusCode: 400);

            // Validate user belongs to this tenant
            var userBelongsToTenant = await dbContext.Users
                .AnyAsync(u => u.Id == userId && u.TenantId == tenantId.Value);
            if (!userBelongsToTenant)
                return Results.NotFound(new { error = "User not found in this tenant." });

            var permissions = await vaultAccessService.GetUserPermissionsAsync(userId);
            if (permissions == null)
            {
                // No record = full access (backward compatibility)
                return Results.Ok(new UserPermissionsDto(userId, true, true));
            }
            return Results.Ok(permissions);
        });

        // Set user permissions
        group.MapPut("/permissions", async (
            Guid userId,
            SetPermissionsRequest request,
            HttpContext context,
            IVaultAccessService vaultAccessService,
            SelfHostedDbContext dbContext) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden();

            // Fix E: reuse VaultEndpoints helper instead of duplicating
            var tenantId = VaultEndpoints.GetTenantIdFromContext(context);
            if (tenantId == null)
                return Results.Json(new { error = "Tenant context required." }, statusCode: 400);

            // Validate user belongs to this tenant
            var userBelongsToTenant = await dbContext.Users
                .AnyAsync(u => u.Id == userId && u.TenantId == tenantId.Value);
            if (!userBelongsToTenant)
                return Results.NotFound(new { error = "User not found in this tenant." });

            var result = await vaultAccessService.SetUserPermissionsAsync(
                userId, tenantId.Value, request.HasAllVaultsAccess, request.CanCreateVaults);
            return Results.Ok(result);
        });

        // List vault access records for a user
        group.MapGet("/", async (
            Guid userId,
            HttpContext context,
            IVaultAccessService vaultAccessService,
            SelfHostedDbContext dbContext) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden();

            var tenantId = VaultEndpoints.GetTenantIdFromContext(context);
            if (tenantId == null)
                return Results.Json(new { error = "Tenant context required." }, statusCode: 400);

            // Validate user belongs to this tenant
            var userBelongsToTenant = await dbContext.Users
                .AnyAsync(u => u.Id == userId && u.TenantId == tenantId.Value);
            if (!userBelongsToTenant)
                return Results.NotFound(new { error = "User not found in this tenant." });

            var accessList = await vaultAccessService.GetUserVaultAccessAsync(userId, tenantId.Value);
            return Results.Ok(accessList);
        });

        // Grant vault access
        group.MapPost("/", async (
            Guid userId,
            GrantVaultAccessRequest request,
            HttpContext context,
            IVaultAccessService vaultAccessService,
            SelfHostedDbContext dbContext) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden();

            var tenantId = VaultEndpoints.GetTenantIdFromContext(context);
            if (tenantId == null)
                return Results.Json(new { error = "Tenant context required." }, statusCode: 400);

            // Validate user belongs to this tenant
            var userBelongsToTenant = await dbContext.Users
                .AnyAsync(u => u.Id == userId && u.TenantId == tenantId.Value);
            if (!userBelongsToTenant)
                return Results.NotFound(new { error = "User not found in this tenant." });

            var grantedBy = VaultEndpoints.GetUserIdFromContext(context);

            try
            {
                var result = await vaultAccessService.GrantVaultAccessAsync(
                    userId, tenantId.Value, request.VaultId,
                    request.CanRead, request.CanWrite, request.CanDelete, request.CanManage,
                    grantedBy);
                return Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Revoke vault access
        group.MapDelete("/{vaultId:guid}", async (
            Guid userId,
            Guid vaultId,
            HttpContext context,
            IVaultAccessService vaultAccessService,
            SelfHostedDbContext dbContext) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden();

            var tenantId = VaultEndpoints.GetTenantIdFromContext(context);
            if (tenantId == null)
                return Results.Json(new { error = "Tenant context required." }, statusCode: 400);

            // Validate user belongs to this tenant
            var userBelongsToTenant = await dbContext.Users
                .AnyAsync(u => u.Id == userId && u.TenantId == tenantId.Value);
            if (!userBelongsToTenant)
                return Results.NotFound(new { error = "User not found in this tenant." });

            var removed = await vaultAccessService.RevokeVaultAccessAsync(userId, tenantId.Value, vaultId);
            return removed
                ? Results.Ok(new { message = "Access revoked." })
                : Results.NotFound(new { error = "Access record not found." });
        });

        // Batch set vault access (replace all)
        group.MapPost("/batch", async (
            Guid userId,
            BatchVaultAccessRequest request,
            HttpContext context,
            IVaultAccessService vaultAccessService,
            SelfHostedDbContext dbContext) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden();

            var tenantId = VaultEndpoints.GetTenantIdFromContext(context);
            if (tenantId == null)
                return Results.Json(new { error = "Tenant context required." }, statusCode: 400);

            // Validate user belongs to this tenant
            var userBelongsToTenant = await dbContext.Users
                .AnyAsync(u => u.Id == userId && u.TenantId == tenantId.Value);
            if (!userBelongsToTenant)
                return Results.NotFound(new { error = "User not found in this tenant." });

            var grantedBy = VaultEndpoints.GetUserIdFromContext(context);

            try
            {
                await vaultAccessService.BatchSetVaultAccessAsync(
                    userId, tenantId.Value, request.Grants, grantedBy);

                var accessList = await vaultAccessService.GetUserVaultAccessAsync(userId, tenantId.Value);
                return Results.Ok(accessList);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        return app;
    }
}

// Request DTOs
public record SetPermissionsRequest(bool HasAllVaultsAccess, bool CanCreateVaults);
public record GrantVaultAccessRequest(Guid VaultId, bool CanRead = true, bool CanWrite = true, bool CanDelete = false, bool CanManage = false);
public record BatchVaultAccessRequest(List<VaultAccessGrant> Grants);
