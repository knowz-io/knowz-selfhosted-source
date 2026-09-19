namespace Knowz.SelfHosted.Application.Interfaces;

using Knowz.Core.Portability;
using System.IO.Compression;
using Knowz.SelfHosted.Application.DTOs;

public interface IPortableImportService
{
    /// <summary>
    /// Validate an import package without writing to database (dry-run).
    /// </summary>
    Task<ImportValidationResult> ValidateAsync(
        PortableExportPackage package,
        CancellationToken ct = default);

    Task<PortableImportResult> ImportZipAsync(ZipArchive archive,
        ImportConflictStrategy strategy = ImportConflictStrategy.Skip, CancellationToken ct = default);

    /// <summary>
    /// Import a portable package into the self-hosted database.
    /// </summary>
    Task<PortableImportResult> ImportAsync(
        PortableExportPackage package,
        ImportConflictStrategy strategy = ImportConflictStrategy.Skip,
        CancellationToken ct = default);
}
