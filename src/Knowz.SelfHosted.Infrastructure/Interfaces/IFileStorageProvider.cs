namespace Knowz.SelfHosted.Infrastructure.Interfaces;

/// <summary>
/// Abstraction for file storage operations. Implementations: Azure Blob Storage, Local Filesystem.
/// </summary>
public interface IFileStorageProvider
{
    Task<string> UploadAsync(Guid tenantId, Guid fileRecordId, Stream stream, string contentType, CancellationToken ct = default);
    Task<(Stream stream, string contentType, string fileName)> DownloadAsync(Guid tenantId, Guid fileRecordId, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid tenantId, Guid fileRecordId, CancellationToken ct = default);
    Task<bool> ExistsAsync(Guid tenantId, Guid fileRecordId, CancellationToken ct = default);
    Task<string> GenerateDownloadUrlAsync(Guid tenantId, Guid fileRecordId, string fileName, TimeSpan? expiry = null, CancellationToken ct = default);
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}
