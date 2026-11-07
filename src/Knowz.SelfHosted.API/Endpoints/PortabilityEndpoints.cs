namespace Knowz.SelfHosted.API.Endpoints;

using System.IO.Compression;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.Core.Schema;
using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.AspNetCore.Mvc;

public static class PortabilityEndpoints
{
    private const long MaxImportBodySize = 50 * 1024 * 1024; // 50MB

    private const long MaxZipImportSize = 200 * 1024 * 1024; // 200MB

    public static void MapPortabilityEndpoints(this WebApplication app)
    {
        app.MapArchivePortabilityEndpoints();
        app.MapArchivePeerEndpoints();
        app.MapRecoveredCustodyEndpoints();
        var group = app.MapGroup("/api/v1/portability")
            .WithTags("Portability");

        // GET /api/portability/export
        group.MapGet("/export", async (
            HttpContext context,
            IPortableExportService exportService,
            ILogger<PortableExportService> logger,
            string mode = "light",
            CancellationToken ct = default) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(context))
                return AuthorizationHelpers.Forbidden();

            try
            {
                if (string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase))
                {
                    var zipStream = await exportService.ExportZipAsync(ct);
                    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd-HHmmss");
                    return Results.Stream(
                        zipStream,
                        contentType: "application/zip",
                        fileDownloadName: $"knowz-export-{timestamp}.zip");
                }

                var package = await exportService.ExportAsync(ct);
                return Results.Ok(package);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Export failed");
                return Results.Problem(
                    detail: ex.Message,
                    title: "Export failed",
                    statusCode: 500);
            }
        }).Produces<PortableExportPackage>();

        // POST supports the same scope/mode request shape as the platform, synchronously.
        group.MapPost("/export", async (
            HttpContext context,
            PortableExportRequest request,
            IPortableExportService exportService,
            ILogger<PortableExportService> logger,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(context))
                return AuthorizationHelpers.Forbidden();
            if (!Enum.TryParse<ExportScopeType>(request.ScopeType, true, out var scopeType) ||
                !Enum.IsDefined(scopeType) || !Enum.GetNames<ExportScopeType>().Contains(request.ScopeType, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Invalid scopeType. Use Tenant, Vault, VaultWithChildren, MultiSelect or SingleItem." });
            if (!Enum.TryParse<ExportMode>(request.Mode, true, out var mode) ||
                !Enum.IsDefined(mode) || !Enum.GetNames<ExportMode>().Contains(request.Mode, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Invalid mode. Use Light or Full." });
            var scope = new ExportScope { Type = scopeType, VaultIds = request.VaultIds, KnowledgeItemIds = request.KnowledgeItemIds };
            try
            {
                if (mode == ExportMode.Full)
                    return Results.Stream(await exportService.ExportZipAsync(scope, ct), "application/zip",
                        $"knowz-export-{DateTime.UtcNow:yyyy-MM-dd-HHmmss}.zip");
                return Results.Ok(await exportService.ExportAsync(scope, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scoped export failed");
                return Results.Problem(title: "Export failed", statusCode: 500);
            }
        }).Produces<PortableExportPackage>().Produces(400);

        // POST /api/portability/import/validate
        group.MapPost("/import/validate", async (
            HttpContext context,
            IPortableImportService importService,
            ILogger<PortableImportService> logger,
            PortableExportPackage package,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(context))
                return AuthorizationHelpers.Forbidden();

            try
            {
                var result = await importService.ValidateAsync(package, ct);
                return result.IsValid
                    ? Results.Ok(result)
                    : Results.UnprocessableEntity(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Import validation failed");
                return Results.Problem(
                    detail: ex.Message,
                    title: "Import validation failed",
                    statusCode: 500);
            }
        }).Produces<ImportValidationResult>()
          .Produces<ImportValidationResult>(422);

        // POST /api/portability/import
        group.MapPost("/import", async (
            HttpContext context,
            IPortableImportService importService,
            ILogger<PortableImportService> logger,
            PortableExportPackage package,
            string strategy = "skip",
            Guid? targetVaultId = null,
            CancellationToken ct = default) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(context))
                return AuthorizationHelpers.Forbidden();

            if ((!Enum.TryParse<ImportConflictStrategy>(strategy, true, out var conflictStrategy) || !Enum.IsDefined(conflictStrategy)))
                return Results.BadRequest(new { error = $"Invalid strategy: {strategy}. Use: skip, overwrite, merge" });

            try
            {
                var result = targetVaultId.HasValue
                    ? await importService.ImportAsync(package, targetVaultId.Value, conflictStrategy, ct)
                    : await importService.ImportAsync(package, conflictStrategy, ct);
                return result.Success
                    ? Results.Ok(result)
                    : Results.UnprocessableEntity(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Import failed");
                return Results.Problem(
                    detail: ex.Message,
                    title: "Import failed",
                    statusCode: 500);
            }
        }).Produces<PortableImportResult>()
          .Produces<PortableImportResult>(422);

        // POST /api/portability/import/zip
        group.MapPost("/import/zip", async (
            HttpContext context,
            IPortableImportService importService,
            IFileStorageProvider storageProvider,
            ITenantProvider tenantProvider,
            ILogger<PortableImportService> logger,
            string strategy = "skip",
            Guid? targetVaultId = null,
            CancellationToken ct = default) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(context))
                return AuthorizationHelpers.Forbidden();

            if ((!Enum.TryParse<ImportConflictStrategy>(strategy, true, out var conflictStrategy) || !Enum.IsDefined(conflictStrategy)))
                return Results.BadRequest(new { error = $"Invalid strategy: {strategy}. Use: skip, overwrite, merge" });

            var form = await context.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file == null || file.Length == 0)
                return Results.BadRequest(new { error = "No ZIP file provided. Upload a file with field name 'file'." });

            if (file.Length > MaxZipImportSize)
                return Results.BadRequest(new { error = $"File too large. Maximum size is {MaxZipImportSize / (1024 * 1024)} MB." });

            try
            {
                using var stream = file.OpenReadStream();
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

                var result = targetVaultId.HasValue
                    ? await importService.ImportZipAsync(archive, targetVaultId.Value, conflictStrategy, ct)
                    : await importService.ImportZipAsync(archive, conflictStrategy, ct);
                return result.Success
                    ? Results.Ok(result)
                    : Results.UnprocessableEntity(result);
            }
            catch (PortableZipSecurityException ex)
            {
                logger.LogWarning(ex, "ZIP security validation failed");
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidDataException ex)
            {
                return Results.BadRequest(new { error = $"Invalid ZIP archive: {ex.Message}" });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ZIP import failed");
                return Results.Problem(
                    detail: ex.Message,
                    title: "ZIP import failed",
                    statusCode: 500);
            }
        }).Produces<PortableImportResult>()
          .Produces<PortableImportResult>(422)
          .DisableAntiforgery();

        // GET /api/portability/schema
        group.MapGet("/schema", (HttpContext context) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(context))
                return AuthorizationHelpers.Forbidden();

            return Results.Ok(new
            {
                version = CoreSchema.Version,
                minReadableVersion = CoreSchema.MinReadableVersion,
                compatibility = CoreSchema.GetCompatibilityInfo(),
                capabilities = new { scopedExport = true, targetVaultImport = true, asyncExport = false }
            });
        });
    }
}
