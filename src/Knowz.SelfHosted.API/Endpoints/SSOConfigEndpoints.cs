using System.Security.Claims;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Models;

namespace Knowz.SelfHosted.API.Endpoints;

public static class SSOConfigEndpoints
{
    public static void MapSSOConfigEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sso/config").WithTags("SSO Configuration");

        group.MapGet("/", async (HttpContext ctx, IConfigurationManagementService configSvc,
            ISelfHostedSSOService ssoService) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(ctx))
                return AuthorizationHelpers.Forbidden();

            var category = await configSvc.GetCategoryAsync("SSO");
            if (category is null)
                return Results.Ok(new SelfHostedSSOConfigDto());

            var entries = category.Entries;
            string? getValue(string key) => entries.FirstOrDefault(e => e.Key == key)?.Value;

            var dto = new SelfHostedSSOConfigDto
            {
                IsEnabled = getValue("Enabled")?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false,
                ClientId = getValue("Microsoft:ClientId"),
                HasClientSecret = !string.IsNullOrEmpty(getValue("Microsoft:ClientSecret")),
                DirectoryTenantId = getValue("Microsoft:DirectoryTenantId"),
                AutoProvisionUsers = getValue("AutoProvisionUsers")?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false,
                DefaultRole = getValue("DefaultRole") ?? "User",
            };

            var providers = await ssoService.GetEnabledProvidersAsync();
            var msProvider = providers.FirstOrDefault(p => p.Provider == "Microsoft");
            dto.DetectedMode = msProvider?.Mode ?? "Disabled";

            return Results.Ok(dto);
        }).Produces<SelfHostedSSOConfigDto>().Produces(403);

        group.MapPut("/", async (HttpContext ctx, IConfigurationManagementService configSvc,
            ISelfHostedSSOService ssoService, SelfHostedSSOConfigRequest request) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(ctx))
                return AuthorizationHelpers.Forbidden();

            var username = ctx.User.FindFirst(ClaimTypes.Name)?.Value ?? "unknown";

            var entries = new List<ConfigEntryUpdateDto>
            {
                new() { Key = "Enabled", Value = request.IsEnabled ? "true" : "false" },
                new() { Key = "Microsoft:ClientId", Value = request.ClientId ?? "" },
                new() { Key = "Microsoft:DirectoryTenantId", Value = request.DirectoryTenantId ?? "" },
                new() { Key = "AutoProvisionUsers", Value = request.AutoProvisionUsers ? "true" : "false" },
                new() { Key = "DefaultRole", Value = request.DefaultRole },
            };

            if (request.ClientSecret is not null)
            {
                entries.Add(new ConfigEntryUpdateDto
                {
                    Key = "Microsoft:ClientSecret",
                    Value = request.ClientSecret
                });
            }

            var result = await configSvc.UpdateCategoryAsync("SSO", entries, username);
            if (!result.Success)
                return Results.BadRequest(new { errors = result.Errors });

            // Return updated config
            var category = await configSvc.GetCategoryAsync("SSO");
            var updatedEntries = category?.Entries ?? new List<ConfigEntryDto>();
            string? getValue(string key) => updatedEntries.FirstOrDefault(e => e.Key == key)?.Value;

            var dto = new SelfHostedSSOConfigDto
            {
                IsEnabled = getValue("Enabled")?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false,
                ClientId = getValue("Microsoft:ClientId"),
                HasClientSecret = !string.IsNullOrEmpty(getValue("Microsoft:ClientSecret")),
                DirectoryTenantId = getValue("Microsoft:DirectoryTenantId"),
                AutoProvisionUsers = getValue("AutoProvisionUsers")?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false,
                DefaultRole = getValue("DefaultRole") ?? "User",
            };

            var providers = await ssoService.GetEnabledProvidersAsync();
            var msProvider = providers.FirstOrDefault(p => p.Provider == "Microsoft");
            dto.DetectedMode = msProvider?.Mode ?? "Disabled";

            return Results.Ok(dto);
        }).Produces<SelfHostedSSOConfigDto>().Produces(400).Produces(403);

        group.MapDelete("/", async (HttpContext ctx, IConfigurationManagementService configSvc) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(ctx))
                return AuthorizationHelpers.Forbidden();

            var username = ctx.User.FindFirst(ClaimTypes.Name)?.Value ?? "unknown";

            var entries = new List<ConfigEntryUpdateDto>
            {
                new() { Key = "Enabled", Value = "false" },
                new() { Key = "Microsoft:ClientId", Value = "" },
                new() { Key = "Microsoft:ClientSecret", Value = "" },
                new() { Key = "Microsoft:DirectoryTenantId", Value = "" },
                new() { Key = "AutoProvisionUsers", Value = "false" },
                new() { Key = "DefaultRole", Value = "User" },
            };

            await configSvc.UpdateCategoryAsync("SSO", entries, username);
            return Results.Ok(new { message = "SSO configuration cleared" });
        }).Produces(403);

        group.MapPost("/test", async (HttpContext ctx, IConfigurationManagementService configSvc) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(ctx))
                return AuthorizationHelpers.Forbidden();

            var result = await configSvc.TestConnectionAsync("SSO");

            return Results.Ok(new SelfHostedSSOTestResultDto
            {
                Success = result.IsHealthy,
                Status = result.Status,
                DetectedMode = result.Status?.Contains("PKCE") == true ? "PKCE Public Client"
                    : result.Status?.Contains("Confidential") == true ? "Confidential Client"
                    : "Unknown",
                TestedAt = DateTime.UtcNow,
            });
        }).Produces<SelfHostedSSOTestResultDto>().Produces(403);

        group.MapGet("/mode", async (HttpContext ctx, ISelfHostedSSOService ssoService) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(ctx))
                return AuthorizationHelpers.Forbidden();

            var providers = await ssoService.GetEnabledProvidersAsync();
            var msProvider = providers.FirstOrDefault(p => p.Provider == "Microsoft");

            return Results.Ok(new { mode = msProvider?.Mode ?? "Disabled" });
        }).Produces(403);
    }
}
