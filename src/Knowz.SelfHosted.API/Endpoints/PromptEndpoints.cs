using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Services;

namespace Knowz.SelfHosted.API.Endpoints;

public static class PromptEndpoints
{
    public static void MapPromptEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/prompts").WithTags("Prompts");

        // --- Platform scope (SuperAdmin) ---

        group.MapGet("/platform", async (
            PromptManagementService svc,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(context))
                return AuthorizationHelpers.Forbidden();

            var prompts = await svc.GetPlatformPromptsAsync(ct);
            return Results.Ok(prompts.Select(ToDto));
        }).Produces<IEnumerable<PromptTemplateDto>>();

        group.MapPut("/platform/{key}", async (
            PromptManagementService svc,
            HttpContext context,
            string key,
            UpdatePromptRequest req,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(context))
                return AuthorizationHelpers.Forbidden();

            if (string.IsNullOrWhiteSpace(req.TemplateText))
                return Results.BadRequest(new { error = "templateText is required" });

            var modifiedBy = AuthorizationHelpers.GetCallerId(context)?.ToString() ?? "unknown";
            var result = await svc.UpdatePlatformPromptAsync(key, req.TemplateText, req.Description, modifiedBy, ct);
            return result != null ? Results.Ok(ToDto(result)) : Results.NotFound(new { error = $"Prompt '{key}' not found" });
        }).Produces<PromptTemplateDto>().Produces(400).Produces(404);

        group.MapPost("/platform/{key}/reset", async (
            PromptManagementService svc,
            HttpContext context,
            string key,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(context))
                return AuthorizationHelpers.Forbidden();

            var modifiedBy = AuthorizationHelpers.GetCallerId(context)?.ToString() ?? "unknown";
            var result = await svc.ResetPlatformPromptAsync(key, modifiedBy, ct);
            return result != null ? Results.Ok(ToDto(result)) : Results.NotFound(new { error = $"Prompt '{key}' not found" });
        }).Produces<PromptTemplateDto>().Produces(404);

        // --- Tenant scope (Admin) ---

        group.MapGet("/tenant", async (
            PromptManagementService svc,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden();

            var tenantId = AuthorizationHelpers.GetCallerTenantId(context);
            if (tenantId == null)
                return Results.BadRequest(new { error = "Tenant context required" });

            var prompts = await svc.GetTenantPromptsAsync(tenantId.Value, ct);
            return Results.Ok(prompts.Select(ToDto));
        }).Produces<IEnumerable<PromptTemplateDto>>();

        group.MapPut("/tenant/{key}", async (
            PromptManagementService svc,
            HttpContext context,
            string key,
            UpsertTenantPromptRequest req,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden();

            var tenantId = AuthorizationHelpers.GetCallerTenantId(context);
            if (tenantId == null)
                return Results.BadRequest(new { error = "Tenant context required" });

            if (string.IsNullOrWhiteSpace(req.TemplateText))
                return Results.BadRequest(new { error = "templateText is required" });

            if (!PromptKeys.All.Contains(key))
                return Results.BadRequest(new { error = $"Invalid prompt key: {key}" });

            var strategy = Enum.TryParse<PromptMergeStrategy>(req.MergeStrategy, true, out var ms)
                ? ms : PromptMergeStrategy.Override;

            var modifiedBy = AuthorizationHelpers.GetCallerId(context)?.ToString() ?? "unknown";
            var result = await svc.UpsertTenantPromptAsync(tenantId.Value, key, req.TemplateText, strategy, req.Description, modifiedBy, ct);
            return Results.Ok(ToDto(result));
        }).Produces<PromptTemplateDto>().Produces(400);

        group.MapDelete("/tenant/{key}", async (
            PromptManagementService svc,
            HttpContext context,
            string key,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden();

            var tenantId = AuthorizationHelpers.GetCallerTenantId(context);
            if (tenantId == null)
                return Results.BadRequest(new { error = "Tenant context required" });

            var deleted = await svc.DeleteTenantPromptAsync(tenantId.Value, key, ct);
            return deleted ? Results.NoContent() : Results.NotFound(new { error = $"Tenant prompt '{key}' not found" });
        }).Produces(204).Produces(404);

        // --- User scope ---

        group.MapGet("/user", async (
            PromptManagementService svc,
            HttpContext context,
            CancellationToken ct) =>
        {
            var userId = AuthorizationHelpers.GetCallerId(context);
            if (userId == null)
                return Results.BadRequest(new { error = "User context required" });

            var prompts = await svc.GetUserPromptsAsync(userId.Value, ct);
            return Results.Ok(prompts.Select(ToDto));
        }).Produces<IEnumerable<PromptTemplateDto>>();

        group.MapPut("/user/{key}", async (
            PromptManagementService svc,
            HttpContext context,
            string key,
            UpdatePromptRequest req,
            CancellationToken ct) =>
        {
            var userId = AuthorizationHelpers.GetCallerId(context);
            var tenantId = AuthorizationHelpers.GetCallerTenantId(context);
            if (userId == null || tenantId == null)
                return Results.BadRequest(new { error = "User and tenant context required" });

            if (string.IsNullOrWhiteSpace(req.TemplateText))
                return Results.BadRequest(new { error = "templateText is required" });

            if (!PromptKeys.UserEligible.Contains(key))
                return Results.BadRequest(new { error = $"Prompt key '{key}' is not user-customizable. Only {string.Join(", ", PromptKeys.UserEligible)} can be customized by users." });

            var modifiedBy = userId.Value.ToString();
            var result = await svc.UpsertUserPromptAsync(userId.Value, tenantId.Value, key, req.TemplateText, modifiedBy, ct);
            return Results.Ok(ToDto(result));
        }).Produces<PromptTemplateDto>().Produces(400);

        group.MapDelete("/user/{key}", async (
            PromptManagementService svc,
            HttpContext context,
            string key,
            CancellationToken ct) =>
        {
            var userId = AuthorizationHelpers.GetCallerId(context);
            if (userId == null)
                return Results.BadRequest(new { error = "User context required" });

            var deleted = await svc.DeleteUserPromptAsync(userId.Value, key, ct);
            return deleted ? Results.NoContent() : Results.NotFound(new { error = $"User prompt '{key}' not found" });
        }).Produces(204).Produces(404);

        // --- Resolved view (debugging) ---

        group.MapGet("/resolved", async (
            PromptResolutionService promptService,
            HttpContext context,
            CancellationToken ct) =>
        {
            var tenantId = AuthorizationHelpers.GetCallerTenantId(context) ?? Guid.Empty;
            var userId = AuthorizationHelpers.GetCallerId(context);
            var resolved = await promptService.ResolvePromptsAsync(tenantId, userId, ct);
            return Results.Ok(resolved);
        }).Produces<ResolvedPromptSet>();
    }

    private static PromptTemplateDto ToDto(PromptTemplate pt) => new(
        pt.Id, pt.PromptKey, pt.Scope.ToString(), pt.TemplateText,
        pt.MergeStrategy.ToString(), pt.Description, pt.IsSystemSeeded,
        pt.UpdatedAt, pt.LastModifiedBy);

    // --- Request DTOs ---

    public record UpdatePromptRequest(string TemplateText, string? Description = null);
    public record UpsertTenantPromptRequest(string TemplateText, string? MergeStrategy = null, string? Description = null);

    // --- Response DTO ---

    public record PromptTemplateDto(
        Guid Id, string PromptKey, string Scope,
        string TemplateText, string MergeStrategy,
        string? Description, bool IsSystemSeeded,
        DateTime UpdatedAt, string? LastModifiedBy);
}
