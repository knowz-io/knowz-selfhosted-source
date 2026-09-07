namespace Knowz.SelfHosted.Application.Services;

using System.Net.Http.Json;
using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Manifest-based file blob sync between selfhosted and platform.
/// After entity sync, compares file manifests and transfers only changed files.
/// </summary>
public class FileSyncService
{
    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly IFileStorageProvider _storageProvider;
    private readonly PlatformSyncCredentialResolver _credentials;
    private readonly ILogger<FileSyncService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Max file size for sync (50MB default) — matches the named client's MaxResponseContentBufferSize.
    private const long MaxFileSizeBytes = 50 * 1024 * 1024;

    // Per-instance timeout for file bytes. The shared "KnowzPlatformSync" registration stays at 30s
    // for browse/schema/entity calls; only the file lane overrides it.
    private static readonly TimeSpan FileTransferTimeout = TimeSpan.FromMinutes(10);

    public FileSyncService(
        SelfHostedDbContext db,
        ITenantProvider tenantProvider,
        IFileStorageProvider storageProvider,
        PlatformSyncCredentialResolver credentials,
        ILogger<FileSyncService> logger)
    {
        _db = db;
        _tenantProvider = tenantProvider;
        _storageProvider = storageProvider;
        _credentials = credentials;
        _logger = logger;
    }

    /// <summary>
    /// Sync file blobs between selfhosted and platform after entity sync.
    /// Downloads new/changed remote files; uploads new local files.
    /// </summary>
    public async Task<FileSyncResult> SyncFilesAsync(VaultSyncLink link, CancellationToken ct = default)
    {
        var result = new FileSyncResult();
        var tenantId = _tenantProvider.TenantId;

        try
        {
            // Step 1: Get remote file manifest
            var remoteManifest = await GetRemoteManifestAsync(link, ct);

            // Step 2: Get local file records for this vault
            var knowledgeIds = await _db.KnowledgeVaults
                .Where(kv => kv.VaultId == link.LocalVaultId)
                .Select(kv => kv.KnowledgeId)
                .ToListAsync(ct);

            var localFileAttachments = await _db.FileAttachments
                .Where(fa => fa.KnowledgeId.HasValue && knowledgeIds.Contains(fa.KnowledgeId.Value))
                .Select(fa => fa.FileRecordId)
                .Distinct()
                .ToListAsync(ct);

            var localFiles = await _db.FileRecords
                .Where(f => f.TenantId == tenantId && localFileAttachments.Contains(f.Id))
                .AsNoTracking()
                .ToListAsync(ct);

            var localFileIds = localFiles.Select(f => f.Id).ToHashSet();
            var remoteFileIds = remoteManifest.Select(f => f.FileRecordId).ToHashSet();

            // Step 3: Download files that exist remotely but not locally
            foreach (var remoteFile in remoteManifest)
            {
                if (localFileIds.Contains(remoteFile.FileRecordId))
                    continue; // Already exists locally

                if (remoteFile.SizeBytes > MaxFileSizeBytes)
                {
                    result.Skipped++;
                    _logger.LogInformation("Skipping large file {FileId} ({Size} bytes)", remoteFile.FileRecordId, remoteFile.SizeBytes);
                    continue;
                }

                try
                {
                    await DownloadFileFromPlatformAsync(link, remoteFile, tenantId, ct);
                    result.Downloaded++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to download file {FileId}", remoteFile.FileRecordId);
                    result.Errors.Add($"Download failed: {remoteFile.FileName} - {ex.Message}");
                }
            }

            // Step 4: Upload files that exist locally but not remotely
            foreach (var localFile in localFiles)
            {
                if (remoteFileIds.Contains(localFile.Id))
                    continue; // Already exists remotely

                if (localFile.SizeBytes > MaxFileSizeBytes)
                {
                    result.Skipped++;
                    continue;
                }

                try
                {
                    await UploadFileToPlatformAsync(link, localFile, tenantId, ct);
                    result.Uploaded++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to upload file {FileId}", localFile.Id);
                    result.Errors.Add($"Upload failed: {localFile.FileName} - {ex.Message}");
                }
            }

            result.Success = true;
            _logger.LogInformation(
                "File sync complete: {Downloaded} downloaded, {Uploaded} uploaded, {Skipped} skipped",
                result.Downloaded, result.Uploaded, result.Skipped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File sync failed");
            result.Success = false;
            result.Errors.Add(ex.Message);
        }

        return result;
    }

    private async Task<List<FileManifestEntry>> GetRemoteManifestAsync(VaultSyncLink link, CancellationToken ct)
    {
        using var client = CreateClient(link);

        var url = $"/api/v1/sync/vaults/{link.RemoteVaultId}/files/manifest";
        if (link.LastPullCursor.HasValue)
            url += $"?since={link.LastPullCursor.Value:O}";

        var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<PlatformManifestResponse>(JsonOptions, ct);
        return envelope?.Data?.Files ?? new();
    }

    private async Task DownloadFileFromPlatformAsync(
        VaultSyncLink link, FileManifestEntry remoteFile, Guid tenantId, CancellationToken ct)
    {
        using var client = CreateClient(link);
        var response = await client.GetAsync(
            $"/api/v1/sync/vaults/{link.RemoteVaultId}/files/{remoteFile.FileRecordId}", ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("File download returned {StatusCode} for {FileId}", response.StatusCode, remoteFile.FileRecordId);
            return;
        }

        // The platform streams the bytes through its API (src/Knowz.API/Endpoints/SyncEndpoints.cs,
        // Results.Stream — FIX_FederationSyncTransportContract R9). No 302 to a blob URI is ever
        // issued, which is why the named client keeps AllowAutoRedirect=false (SSRF guard).
        using var stream = await response.Content.ReadAsStreamAsync(ct);

        // Upload to local storage
        await _storageProvider.UploadAsync(tenantId, remoteFile.FileRecordId, stream,
            remoteFile.ContentType ?? "application/octet-stream", ct);

        // Create local FileRecord if not exists
        var existing = await _db.FileRecords.FindAsync([remoteFile.FileRecordId], ct);
        if (existing == null)
        {
            _db.FileRecords.Add(new FileRecord
            {
                Id = remoteFile.FileRecordId,
                TenantId = tenantId,
                FileName = remoteFile.FileName ?? "unknown",
                ContentType = remoteFile.ContentType ?? "application/octet-stream",
                SizeBytes = remoteFile.SizeBytes,
                CreatedAt = remoteFile.UpdatedAt ?? DateTime.UtcNow,
                UpdatedAt = remoteFile.UpdatedAt ?? DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation("Downloaded file {FileId} ({FileName})", remoteFile.FileRecordId, remoteFile.FileName);
    }

    private async Task UploadFileToPlatformAsync(
        VaultSyncLink link, FileRecord localFile, Guid tenantId, CancellationToken ct)
    {
        // Download from local storage
        var (stream, contentType, _) = await _storageProvider.DownloadAsync(tenantId, localFile.Id, ct);

        using var client = CreateClient(link);
        using var content = new MultipartFormDataContent();
        using var streamContent = new StreamContent(stream);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        content.Add(streamContent, "file", localFile.FileName);

        var response = await client.PostAsync(
            $"/api/v1/sync/vaults/{link.RemoteVaultId}/files/{localFile.Id}", content, ct);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("Uploaded file {FileId} ({FileName})", localFile.Id, localFile.FileName);
    }

    /// <summary>
    /// Authenticated client for the file lane. Credentials, URL re-validation and the registered
    /// "KnowzPlatformSync" client all come from <see cref="PlatformSyncCredentialResolver"/> — the
    /// same path the entity lane uses, so the two cannot drift. Only the instance timeout differs:
    /// the shared 30s default is far too short for a 50 MB transfer, so it is overridden per
    /// instance here (the registration in Program.cs is untouched).
    /// </summary>
    private HttpClient CreateClient(VaultSyncLink link) =>
        _credentials.CreatePlatformHttpClient(link, FileTransferTimeout);
}

public class FileSyncResult
{
    public bool Success { get; set; }
    public int Downloaded { get; set; }
    public int Uploaded { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; set; } = new();
}

public class FileManifestEntry
{
    public Guid FileRecordId { get; set; }
    public string? FileName { get; set; }
    public string? ContentHash { get; set; }
    public long SizeBytes { get; set; }
    public string? ContentType { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Wraps the platform's ApiResponse{FileManifestResponse} for deserialization.
/// </summary>
internal class PlatformManifestResponse
{
    public bool Success { get; set; }
    public PlatformManifestData? Data { get; set; }
}

internal class PlatformManifestData
{
    public List<FileManifestEntry> Files { get; set; } = new();
    public DateTime ServerTimestamp { get; set; }
}
