using Knowz.Core.Enums;
using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Models;

namespace Knowz.SelfHosted.API.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/admin").WithTags("Administration");

        // --- Tenant endpoints (SuperAdmin only) ---

        group.MapGet("/tenants", async (HttpContext context, ITenantManagementService svc) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(context))
                return AuthorizationHelpers.Forbidden();
            return Results.Ok(await svc.ListTenantsAsync());
        }).Produces<List<TenantDto>>().Produces(403);

        group.MapGet("/tenants/{id:guid}", async (HttpContext context, ITenantManagementService svc, Guid id) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(context))
                return AuthorizationHelpers.Forbidden();
            var result = await svc.GetTenantAsync(id);
            return result is null
                ? Results.NotFound(new { error = "Tenant not found." })
                : Results.Ok(result);
        }).Produces<TenantDto>().Produces(403).Produces(404);

        group.MapPost("/tenants", async (HttpContext context, ITenantManagementService svc, CreateTenantRequest request) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(context))
                return AuthorizationHelpers.Forbidden();

            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "Name is required." });
            if (string.IsNullOrWhiteSpace(request.Slug))
                return Results.BadRequest(new { error = "Slug is required." });

            try
            {
                var result = await svc.CreateTenantAsync(request);
                return Results.Created($"/api/v1/admin/tenants/{result.Id}", result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        }).Produces<TenantDto>(201).Produces(400).Produces(403).Produces(409);

        group.MapPut("/tenants/{id:guid}", async (HttpContext context, ITenantManagementService svc, Guid id, UpdateTenantRequest request) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(context))
                return AuthorizationHelpers.Forbidden();

            try
            {
                var result = await svc.UpdateTenantAsync(id, request);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "Tenant not found." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        }).Produces<TenantDto>().Produces(403).Produces(404).Produces(409);

        group.MapDelete("/tenants/{id:guid}", async (HttpContext context, ITenantManagementService svc, Guid id) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(context))
                return AuthorizationHelpers.Forbidden();

            try
            {
                await svc.DeleteTenantAsync(id);
                return Results.Ok(new { message = "Tenant deleted successfully." });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "Tenant not found." });
            }
        }).Produces(200).Produces(403).Produces(404);

        // --- User endpoints (Admin or above, with tenant scoping) ---

        group.MapGet("/users", async (HttpContext context, IUserManagementService svc, Guid? tenantId) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden();

            // Admin: force-scope to own tenant, ignore query param
            if (!AuthorizationHelpers.IsSuperAdmin(context))
            {
                tenantId = AuthorizationHelpers.GetCallerTenantId(context);
                if (!tenantId.HasValue)
                    return AuthorizationHelpers.Forbidden("Tenant context required.");
            }

            return Results.Ok(await svc.ListUsersAsync(tenantId));
        }).Produces<List<UserDto>>().Produces(403);

        group.MapGet("/users/{id:guid}", async (HttpContext context, IUserManagementService svc, Guid id) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden();

            var result = await svc.GetUserAsync(id);
            if (result is null)
                return Results.NotFound(new { error = "User not found." });

            // Admin: validate same tenant
            if (!AuthorizationHelpers.IsSuperAdmin(context))
            {
                var callerTenantId = AuthorizationHelpers.GetCallerTenantId(context);
                if (result.TenantId != callerTenantId)
                    return Results.NotFound(new { error = "User not found." });
            }

            return Results.Ok(result);
        }).Produces<UserDto>().Produces(403).Produces(404);

        group.MapPost("/users", async (HttpContext context, IUserManagementService svc, CreateUserRequest request) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden();

            // Admin: force tenant to own tenant
            if (!AuthorizationHelpers.IsSuperAdmin(context))
            {
                var callerTenantId = AuthorizationHelpers.GetCallerTenantId(context);
                if (!callerTenantId.HasValue)
                    return AuthorizationHelpers.Forbidden("Tenant context required.");
                request.TenantId = callerTenantId.Value;
            }

            // Role-cap validation
            if (!AuthorizationHelpers.CanAssignRole(context, request.Role))
                return AuthorizationHelpers.Forbidden(
                    $"Cannot assign role '{request.Role}'. Insufficient privileges.");

            if (string.IsNullOrWhiteSpace(request.Username))
                return Results.BadRequest(new { error = "Username is required." });
            if (string.IsNullOrWhiteSpace(request.Password))
                return Results.BadRequest(new { error = "Password is required." });
            if (request.Password.Length < 6)
                return Results.BadRequest(new { error = "Password must be at least 6 characters." });

            try
            {
                var result = await svc.CreateUserAsync(request);
                return Results.Created($"/api/v1/admin/users/{result.Id}", result);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        }).Produces<UserDto>(201).Produces(400).Produces(403).Produces(404).Produces(409);

        group.MapPut("/users/{id:guid}", async (HttpContext context, IUserManagementService svc, Guid id, UpdateUserRequest request) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden();

            // Admin: validate target user is in same tenant and not a higher role
            if (!AuthorizationHelpers.IsSuperAdmin(context))
            {
                var callerTenantId = AuthorizationHelpers.GetCallerTenantId(context);
                var targetUser = await svc.GetUserAsync(id);
                if (targetUser is null || targetUser.TenantId != callerTenantId)
                    return Results.NotFound(new { error = "User not found." });
                if (AuthorizationHelpers.IsPrivilegedRole(targetUser.Role))
                    return AuthorizationHelpers.Forbidden("Cannot modify a user with Admin or higher role.");
            }

            // Role-cap validation
            if (request.Role.HasValue && !AuthorizationHelpers.CanAssignRole(context, request.Role.Value))
                return AuthorizationHelpers.Forbidden(
                    $"Cannot assign role '{request.Role.Value}'. Insufficient privileges.");

            try
            {
                var result = await svc.UpdateUserAsync(id, request);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "User not found." });
            }
        }).Produces<UserDto>().Produces(403).Produces(404);

        group.MapDelete("/users/{id:guid}", async (HttpContext context, IUserManagementService svc, Guid id) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden();

            // Admin: validate target user is in same tenant and not a higher role
            if (!AuthorizationHelpers.IsSuperAdmin(context))
            {
                var callerTenantId = AuthorizationHelpers.GetCallerTenantId(context);
                var targetUser = await svc.GetUserAsync(id);
                if (targetUser is null || targetUser.TenantId != callerTenantId)
                    return Results.NotFound(new { error = "User not found." });
                if (AuthorizationHelpers.IsPrivilegedRole(targetUser.Role))
                    return AuthorizationHelpers.Forbidden("Cannot delete a user with Admin or higher role.");
            }

            try
            {
                await svc.DeleteUserAsync(id);
                return Results.Ok(new { message = "User deleted successfully." });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "User not found." });
            }
        }).Produces(200).Produces(403).Produces(404);

        group.MapPost("/users/{id:guid}/generate-api-key", async (HttpContext context, IUserManagementService svc, Guid id) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden();

            // Admin: validate target user is in same tenant and not a higher role
            if (!AuthorizationHelpers.IsSuperAdmin(context))
            {
                var callerTenantId = AuthorizationHelpers.GetCallerTenantId(context);
                var targetUser = await svc.GetUserAsync(id);
                if (targetUser is null || targetUser.TenantId != callerTenantId)
                    return Results.NotFound(new { error = "User not found." });
                if (AuthorizationHelpers.IsPrivilegedRole(targetUser.Role))
                    return AuthorizationHelpers.Forbidden("Cannot generate API key for a user with Admin or higher role.");
            }

            try
            {
                var apiKey = await svc.GenerateApiKeyAsync(id);
                return Results.Ok(new { apiKey });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "User not found." });
            }
        }).Produces(200).Produces(403).Produces(404);

        group.MapPost("/users/{id:guid}/reset-password", async (HttpContext context, IUserManagementService svc, Guid id, ResetPasswordRequest request) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden();

            // Admin: validate target user is in same tenant and not a higher role
            if (!AuthorizationHelpers.IsSuperAdmin(context))
            {
                var callerTenantId = AuthorizationHelpers.GetCallerTenantId(context);
                var targetUser = await svc.GetUserAsync(id);
                if (targetUser is null || targetUser.TenantId != callerTenantId)
                    return Results.NotFound(new { error = "User not found." });
                if (AuthorizationHelpers.IsPrivilegedRole(targetUser.Role))
                    return AuthorizationHelpers.Forbidden("Cannot reset password for a user with Admin or higher role.");
            }

            if (string.IsNullOrWhiteSpace(request.NewPassword))
                return Results.BadRequest(new { error = "New password is required." });
            if (request.NewPassword.Length < 6)
                return Results.BadRequest(new { error = "Password must be at least 6 characters." });

            try
            {
                var message = await svc.ResetPasswordAsync(id, request.NewPassword);
                return Results.Ok(new { message });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "User not found." });
            }
        }).Produces(200).Produces(400).Produces(403).Produces(404);

        // --- User Tenant Memberships ---

        group.MapGet("/users/{userId:guid}/tenants", async (HttpContext context, IUserManagementService svc, Guid userId) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden();

            // Admin: validate target user is in same tenant
            if (!AuthorizationHelpers.IsSuperAdmin(context))
            {
                var callerTenantId = AuthorizationHelpers.GetCallerTenantId(context);
                var targetUser = await svc.GetUserAsync(userId);
                if (targetUser is null || targetUser.TenantId != callerTenantId)
                    return Results.NotFound(new { error = "User not found." });
            }

            var memberships = await svc.GetUserTenantsAsync(userId);
            return Results.Ok(memberships);
        }).Produces<List<TenantMembershipDto>>().Produces(403).Produces(404);

        group.MapPost("/users/{userId:guid}/tenants", async (HttpContext context, IUserManagementService svc, Guid userId, AddUserToTenantRequest request) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden();

            // Admin can only add users to their own tenant
            if (!AuthorizationHelpers.IsSuperAdmin(context))
            {
                var callerTenantId = AuthorizationHelpers.GetCallerTenantId(context);
                if (!callerTenantId.HasValue || request.TenantId != callerTenantId.Value)
                    return AuthorizationHelpers.Forbidden("Can only add users to your own tenant.");
            }

            // Role-cap validation
            if (!AuthorizationHelpers.CanAssignRole(context, request.Role))
                return AuthorizationHelpers.Forbidden($"Cannot assign role '{request.Role}'.");

            try
            {
                var result = await svc.AddUserToTenantAsync(userId, request.TenantId, request.Role);
                return Results.Created($"/api/v1/admin/users/{userId}/tenants", result);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        }).Produces<TenantMembershipDto>(201).Produces(400).Produces(403).Produces(409);

        group.MapPut("/users/{userId:guid}/tenants/{tenantId:guid}", async (HttpContext context, IUserManagementService svc, Guid userId, Guid tenantId, UpdateUserTenantRoleRequest request) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden();

            // Admin: can only manage memberships in own tenant
            if (!AuthorizationHelpers.IsSuperAdmin(context))
            {
                var callerTenantId = AuthorizationHelpers.GetCallerTenantId(context);
                if (tenantId != callerTenantId)
                    return AuthorizationHelpers.Forbidden("Can only manage memberships in your own tenant.");
            }

            if (!AuthorizationHelpers.CanAssignRole(context, request.Role))
                return AuthorizationHelpers.Forbidden($"Cannot assign role '{request.Role}'.");

            try
            {
                var result = await svc.UpdateUserTenantRoleAsync(userId, tenantId, request.Role);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "Membership not found." });
            }
        }).Produces<TenantMembershipDto>().Produces(403).Produces(404);

        group.MapDelete("/users/{userId:guid}/tenants/{tenantId:guid}", async (HttpContext context, IUserManagementService svc, Guid userId, Guid tenantId) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden();

            // Admin: can only remove memberships from own tenant
            if (!AuthorizationHelpers.IsSuperAdmin(context))
            {
                var callerTenantId = AuthorizationHelpers.GetCallerTenantId(context);
                if (tenantId != callerTenantId)
                    return AuthorizationHelpers.Forbidden("Can only manage memberships in your own tenant.");
            }

            try
            {
                await svc.RemoveUserFromTenantAsync(userId, tenantId);
                return Results.Ok(new { message = "User removed from tenant." });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "Membership not found." });
            }
        }).Produces(200).Produces(403).Produces(404);
    }
}
