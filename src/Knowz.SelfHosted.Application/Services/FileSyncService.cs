namespace Knowz.SelfHosted.Application.Services;

using System.Net.Http.Json;
using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Portability;
using Knowz.Core.Schema;
using Knowz.SelfHosted.Application.DTOs;
using Microsoft.Extensions.Logging.Abstractions;
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

            result.Success = result.Errors.Count == 0;
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
        var savedLink = await _db.VaultSyncLinks.AsNoTracking().SingleOrDefaultAsync(l => l.Id == link.Id, ct)
            ?? throw new UnauthorizedAccessException("The saved sync link is no longer available.");
        var connection = await _db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(c =>
            c.Id == savedLink.PlatformConnectionId && c.TenantId == tenantId, ct)
            ?? throw new UnauthorizedAccessException("Configure a saved platform connection before importing file bytes.");
        var binding = JsonSerializer.Serialize(new { savedLink.Id, savedLink.SecurityRevision, savedLink.LocalVaultId,
            savedLink.RemoteVaultId, savedLink.RemoteTenantId, connectionId = connection.Id,
            connectionRevision = connection.SecurityRevision, connection.CreatedByUserId });
        async Task RequireAuthority(CancellationToken token)
        {
            var currentLink = await _db.VaultSyncLinks.AsNoTracking().SingleOrDefaultAsync(l => l.Id == savedLink.Id, token);
            var currentConnection = await _db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == connection.Id, token);
            if (_tenantProvider.TenantId != tenantId || currentLink == null || !currentLink.SyncEnabled ||
                currentLink.SecurityRevision != savedLink.SecurityRevision || currentLink.PlatformConnectionId != connection.Id ||
                currentConnection == null || currentConnection.TenantId != tenantId || currentConnection.SecurityRevision != connection.SecurityRevision ||
                currentConnection.CreatedByUserId != connection.CreatedByUserId ||
                !await _db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId && t.IsActive, token) ||
                !await _db.Users.AsNoTracking().AnyAsync(u => u.Id == connection.CreatedByUserId && u.IsActive, token) ||
                !await _db.UserTenantMemberships.AsNoTracking().AnyAsync(m => m.TenantId == tenantId && m.UserId == connection.CreatedByUserId && m.IsActive, token) ||
                !await _db.Vaults.AsNoTracking().AnyAsync(v => v.Id == savedLink.LocalVaultId && v.TenantId == tenantId, token))
                throw new UnauthorizedAccessException("Sync authority or saved configuration changed.");
            var access = new VaultAccessService(_db);
            if (!await access.HasVaultAccessAsync(connection.CreatedByUserId, savedLink.LocalVaultId, requireWrite: true, ct: token))
                throw new UnauthorizedAccessException("Current destination write authority is required.");
            var file = await _db.FileRecords.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(f => f.Id == remoteFile.FileRecordId, token);
            if (file != null && (file.TenantId != tenantId || file.IsDeleted || !string.IsNullOrEmpty(file.EncryptedFieldsJson)))
                throw new UnauthorizedAccessException("This file requires the complete archive sync protocol.");
            if (await _db.ShSealedFiles.IgnoreQueryFilters().AnyAsync(f => f.Id == remoteFile.FileRecordId, token))
                throw new UnauthorizedAccessException("Encrypted files require the complete archive sync protocol.");
            var owners = await _db.FileAttachments.IgnoreQueryFilters().AsNoTracking().Where(a => a.FileRecordId == remoteFile.FileRecordId)
                .Select(a => new { a.TenantId, a.KnowledgeId, a.CommentId }).ToListAsync(token);
            foreach (var owner in owners)
            {
                if (owner.TenantId != tenantId) throw new UnauthorizedAccessException("File ownership is unavailable.");
                var knowledgeId = owner.KnowledgeId;
                if (owner.CommentId.HasValue)
                    knowledgeId = await _db.Comments.AsNoTracking().Where(c => c.Id == owner.CommentId.Value).Select(c => (Guid?)c.KnowledgeId).SingleOrDefaultAsync(token);
                var knowledge = await _db.KnowledgeItems.AsNoTracking().Where(k => k.Id == knowledgeId && k.PortableRecoveryState == null)
                    .Select(k => new { k.Id, Vaults = k.KnowledgeVaults.Select(v => v.VaultId).ToList() }).SingleOrDefaultAsync(token);
                if (knowledge == null || knowledge.Vaults.Count == 0) throw new UnauthorizedAccessException("File owner is unavailable.");
                foreach (var vault in knowledge.Vaults)
                    if (!await access.HasVaultAccessAsync(connection.CreatedByUserId, vault, requireWrite: true, ct: token))
                        throw new UnauthorizedAccessException("Current file-owner write authority is required.");
            }
        }
        await RequireAuthority(ct);
        using var client = CreateClient(savedLink);
        using var response = await client.GetAsync(
            $"/api/v1/sync/vaults/{savedLink.RemoteVaultId}/files/{remoteFile.FileRecordId}", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var existing = await _db.FileRecords.AsNoTracking().SingleOrDefaultAsync(f => f.Id == remoteFile.FileRecordId, ct);
        var portable = new PortableFileRecord
        {
            Id = remoteFile.FileRecordId, OriginalFileId = existing?.OriginalFileId,
            FileName = remoteFile.FileName ?? existing?.FileName ?? "unknown", ContentType = remoteFile.ContentType ?? existing?.ContentType ?? "application/octet-stream",
            SizeBytes = remoteFile.SizeBytes, BinarySha256 = remoteFile.ContentHash, BinaryFilePath = "sync-file",
            CreatedAt = existing?.CreatedAt ?? remoteFile.UpdatedAt ?? DateTime.UtcNow, UpdatedAt = remoteFile.UpdatedAt ?? DateTime.UtcNow,
            FileVersion = existing?.FileVersion ?? 1, RootFileRecordId = existing?.RootFileRecordId,
            PreviousFileRecordId = existing?.PreviousFileRecordId, IsCurrentVersion = existing?.IsCurrentVersion ?? true,
            TranscriptionText = existing?.TranscriptionText, ExtractedText = existing?.ExtractedText, VisionDescription = existing?.VisionDescription,
            ExtensionData = string.IsNullOrEmpty(existing?.PlatformData) ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existing.PlatformData)
        };
        var package = new PortableExportPackage { SchemaVersion = CoreSchema.Version, SourceEdition = "selfhosted", SourceTenantId = tenantId,
            Data = new() { FileRecords = [portable] } };
        var importer = new PortableImportService(_db, _tenantProvider, _storageProvider, NullLogger<PortableImportService>.Instance);
        var result = await importer.ImportStreamAsync(package, ImportConflictStrategy.Overwrite, _ => stream, RequireAuthority,
            connection.CreatedByUserId, binding, ct);
        if (!result.Success) throw new InvalidOperationException(result.Error ?? "File import did not commit.");

        _logger.LogInformation("Downloaded file {FileId} ({FileName})", remoteFile.FileRecordId, remoteFile.FileName);
    }

    private async Task UploadFileToPlatformAsync(
        VaultSyncLink link, FileRecord localFile, Guid tenantId, CancellationToken ct)
    {
        // Download from local storage
        var (stream, contentType, _) = await _storageProvider.DownloadAsync(tenantId, await SelfHostedFileObjects.ResolveStorageIdAsync(_db, tenantId, localFile.Id, ct), ct);

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
