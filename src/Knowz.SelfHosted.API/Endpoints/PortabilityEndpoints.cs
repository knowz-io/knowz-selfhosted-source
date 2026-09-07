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
            CancellationToken ct = default) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(context))
                return AuthorizationHelpers.Forbidden();

            if (!Enum.TryParse<ImportConflictStrategy>(strategy, true, out var conflictStrategy))
                return Results.BadRequest(new { error = $"Invalid strategy: {strategy}. Use: skip, overwrite, merge" });

            try
            {
                var result = await importService.ImportAsync(package, conflictStrategy, ct);
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
            CancellationToken ct = default) =>
        {
            if (!AuthorizationHelpers.IsSuperAdmin(context))
                return AuthorizationHelpers.Forbidden();

            if (!Enum.TryParse<ImportConflictStrategy>(strategy, true, out var conflictStrategy))
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

                var package = PortableZipReader.ReadFromZip(archive);

                var tenantId = tenantProvider.TenantId;
                foreach (var fileRecord in package.Data.FileRecords)
                {
                    if (string.IsNullOrEmpty(fileRecord.BinaryFilePath))
                        continue;

                    var entry = archive.GetEntry(fileRecord.BinaryFilePath);
                    if (entry == null)
                    {
                        logger.LogWarning(
                            "ZIP entry not found for file record {FileRecordId}: {Path}",
                            fileRecord.Id, fileRecord.BinaryFilePath);
                        continue;
                    }

                    using var entryStream = entry.Open();
                    var storagePath = await storageProvider.UploadAsync(
                        tenantId, fileRecord.Id, entryStream,
                        fileRecord.ContentType ?? "application/octet-stream", ct);
                    fileRecord.BlobUri = storagePath;
                }

                var result = await importService.ImportAsync(package, conflictStrategy, ct);
                return result.Success
                    ? Results.Ok(result)
                    : Results.UnprocessableEntity(result);
            }
            catch (PortableZipSecurityException ex)
            {
                logger.LogWarning(ex, "ZIP security validation failed");
                return Results.BadRequest(new { error = ex.Message });
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
                compatibility = CoreSchema.GetCompatibilityInfo()
            });
        });
    }
}
