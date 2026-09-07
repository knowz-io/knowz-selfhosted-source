using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// Azure Blob Storage implementation of IFileStorageProvider.
/// Uses BlobServiceClient (singleton, connection-pooled) for all operations.
/// </summary>
public class AzureBlobStorageProvider : IFileStorageProvider
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly string _containerName;
    private readonly ILogger<AzureBlobStorageProvider> _logger;

    public AzureBlobStorageProvider(
        BlobServiceClient blobServiceClient,
        IConfiguration configuration,
        ILogger<AzureBlobStorageProvider> logger)
    {
        _blobServiceClient = blobServiceClient;
        _containerName = configuration["Storage:Azure:ContainerName"] ?? "selfhosted-files";
        _logger = logger;
    }

    private static string GetBlobKey(Guid tenantId, Guid fileRecordId)
        => $"{tenantId:N}/{fileRecordId:N}";

    private async Task<BlobContainerClient> GetContainerAsync(CancellationToken ct)
    {
        var container = _blobServiceClient.GetBlobContainerClient(_containerName);
        await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct)
            .ConfigureAwait(false);
        return container;
    }

    public async Task<string> UploadAsync(
        Guid tenantId, Guid fileRecordId, Stream stream, string contentType, CancellationToken ct = default)
    {
        var container = await GetContainerAsync(ct).ConfigureAwait(false);
        var blobKey = GetBlobKey(tenantId, fileRecordId);
        var blobClient = container.GetBlobClient(blobKey);

        var headers = new BlobHttpHeaders { ContentType = contentType };
        // Transfer options for large uploads (up to 500 MB). The Azure Blob SDK
        // auto-parallelizes into blocks when the stream exceeds InitialTransferSize.
        // 8 MB blocks × 4 concurrent = efficient for files in the 50-500 MB range.
        var uploadOptions = new BlobUploadOptions
        {
            HttpHeaders = headers,
            TransferOptions = new Azure.Storage.StorageTransferOptions
            {
                InitialTransferSize = 8 * 1024 * 1024,       // 8 MB one-shot cap
                MaximumTransferSize = 8 * 1024 * 1024,       // 8 MB per block
                MaximumConcurrency = 4
            }
        };
        await blobClient.UploadAsync(stream, uploadOptions, ct).ConfigureAwait(false);

        _logger.LogInformation("Uploaded blob {BlobKey} ({ContentType})", blobKey, contentType);
        return blobClient.Uri.ToString();
    }

    public async Task<(Stream stream, string contentType, string fileName)> DownloadAsync(
        Guid tenantId, Guid fileRecordId, CancellationToken ct = default)
    {
        var container = await GetContainerAsync(ct).ConfigureAwait(false);
        var blobKey = GetBlobKey(tenantId, fileRecordId);
        var blobClient = container.GetBlobClient(blobKey);

        if (!await blobClient.ExistsAsync(ct).ConfigureAwait(false))
            throw new FileNotFoundException($"Blob not found: {blobKey}");

        var response = await blobClient.DownloadStreamingAsync(cancellationToken: ct).ConfigureAwait(false);
        var contentType = response.Value.Details.ContentType ?? "application/octet-stream";
        var fileName = fileRecordId.ToString("N");

        return (response.Value.Content, contentType, fileName);
    }

    public async Task<bool> DeleteAsync(
        Guid tenantId, Guid fileRecordId, CancellationToken ct = default)
    {
        var container = await GetContainerAsync(ct).ConfigureAwait(false);
        var blobKey = GetBlobKey(tenantId, fileRecordId);
        var blobClient = container.GetBlobClient(blobKey);

        var response = await blobClient.DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false);
        if (response.Value)
            _logger.LogInformation("Deleted blob {BlobKey}", blobKey);

        return response.Value;
    }

    public async Task<bool> ExistsAsync(
        Guid tenantId, Guid fileRecordId, CancellationToken ct = default)
    {
        var container = await GetContainerAsync(ct).ConfigureAwait(false);
        var blobKey = GetBlobKey(tenantId, fileRecordId);
        var blobClient = container.GetBlobClient(blobKey);

        return await blobClient.ExistsAsync(ct).ConfigureAwait(false);
    }

    public async Task<string> GenerateDownloadUrlAsync(
        Guid tenantId, Guid fileRecordId, string fileName, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        var container = await GetContainerAsync(ct).ConfigureAwait(false);
        var blobKey = GetBlobKey(tenantId, fileRecordId);
        var blobClient = container.GetBlobClient(blobKey);

        if (!await blobClient.ExistsAsync(ct).ConfigureAwait(false))
            throw new FileNotFoundException($"Blob not found: {blobKey}");

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = _containerName,
            BlobName = blobKey,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.Add(expiry ?? TimeSpan.FromMinutes(60)),
            ContentDisposition = $"attachment; filename=\"{fileName}\""
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        var sasUri = blobClient.GenerateSasUri(sasBuilder);
        return sasUri.ToString();
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var container = _blobServiceClient.GetBlobContainerClient(_containerName);
            await container.ExistsAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
