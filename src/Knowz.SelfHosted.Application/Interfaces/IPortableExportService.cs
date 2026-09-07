namespace Knowz.SelfHosted.Application.Interfaces;

using Knowz.Core.Portability;

public interface IPortableExportService
{
    Task<PortableExportPackage> ExportAsync(CancellationToken ct = default);
    Task<Stream> ExportZipAsync(CancellationToken ct = default);
}
