using System.Security.Claims;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.API.Endpoints;

public static class ConfigurationEndpoints
{
    public static void MapConfigurationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/admin/config").WithTags("Configuration");

        // Instance-wide provider changes require an operator JWT and an unfiltered data census.
        group.MapGet("/embedding-space", async (HttpContext context, SelfHostedDbContext db) =>
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            var credential = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authorization[7..].Trim() : string.Empty;
            if (context.User.Identity?.IsAuthenticated != true || !AuthorizationHelpers.IsSuperAdmin(context)
                || credential.Count(c => c == '.') != 2 || context.Request.Headers.ContainsKey("X-Api-Key"))
                return AuthorizationHelpers.Forbidden();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var populated = await db.KnowledgeItems.IgnoreQueryFilters().AnyAsync(timeout.Token)
                    || await db.ContentChunks.IgnoreQueryFilters().AnyAsync(timeout.Token)
                    || await db.KnowledgeVersions.IgnoreQueryFilters().AnyAsync(timeout.Token)
                    || await db.ShSealedKnowledge.IgnoreQueryFilters().AnyAsync(timeout.Token)
                    || await db.ShSealedVersions.IgnoreQueryFilters().AnyAsync(timeout.Token);
                return Results.Ok(new { canChange = !populated, reason = populated
                    ? "This instance contains retained knowledge or index data. Changing its embedding space requires a supported reindex migration."
                    : "No retained knowledge or index data exists across this instance. Recheck immediately before applying provider configuration." });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                return Results.Json(new { canChange = false, reason = "The instance-wide data census could not be completed. Provider migration is blocked." }, statusCode: 503);
            }
        }).Produces<object>().Produces(403).Produces(503);

        group.MapGet("/categories", async (HttpContext context, IConfigurationManagementService svc) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(context))
                return AuthorizationHelpers.Forbidden();
            return Results.Ok(await svc.GetAllCategoriesAsync());
        }).Produces<List<ConfigCategoryDto>>().Produces(403);

        group.MapGet("/{category}", async (HttpContext context, IConfigurationManagementService svc, string category) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(context))
                return AuthorizationHelpers.Forbidden();
            var result = await svc.GetCategoryAsync(category);
            return result is null
                ? Results.NotFound(new { error = "Category not found." })
                : Results.Ok(result);
        }).Produces<ConfigCategoryDto>().Produces(403).Produces(404);

        group.MapPut("/{category}", async (HttpContext context, IConfigurationManagementService svc, string category, UpdateConfigRequest request) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(context))
                return AuthorizationHelpers.Forbidden();

            var username = context.User.FindFirst(ClaimTypes.Name)?.Value ?? "unknown";
            try
            {
                var result = await svc.UpdateCategoryAsync(category, request.Entries, username);
                if (!result.Success)
                {
                    // Check if it's a category-not-found error
                    if (result.Errors.Any(e => e.StartsWith("Unknown category:")))
                        return Results.NotFound(new { error = "Category not found." });

                    return Results.BadRequest(new { errors = result.Errors, fieldErrors = result.FieldErrors });
                }
                return Results.Ok(result);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new { error = "Concurrent modification detected. Please refresh and try again." });
            }
        }).Produces<ConfigUpdateResult>().Produces(400).Produces(403).Produces(404).Produces(409);

        group.MapPost("/health/{category}", async (HttpContext context, IConfigurationManagementService svc, string category) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(context))
                return AuthorizationHelpers.Forbidden();
            var result = await svc.TestConnectionAsync(category, context.RequestAborted);
            return Results.Ok(result);
        }).Produces<ServiceHealthResult>().Produces(403);

        group.MapPost("/health", async (HttpContext context, IConfigurationManagementService svc) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(context))
                return AuthorizationHelpers.Forbidden();
            return Results.Ok(await svc.TestAllConnectionsAsync());
        }).Produces<List<ServiceHealthResult>>().Produces(403);

        group.MapGet("/status", (HttpContext context, IConfigurationManagementService svc) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(context))
                return AuthorizationHelpers.Forbidden();
            return Results.Ok(svc.GetDeploymentStatus());
        }).Produces<DeploymentStatusDto>().Produces(403);
    }
}

public class UpdateConfigRequest
{
    public List<ConfigEntryUpdateDto> Entries { get; set; } = new();
}
