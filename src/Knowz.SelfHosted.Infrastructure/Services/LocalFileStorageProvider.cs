using System.Security;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// Local filesystem implementation of IFileStorageProvider.
/// Stores files in {RootPath}/{tenantId:N}/{fileRecordId:N}.{ext}.
/// Intended as development/fallback option.
/// </summary>
public class LocalFileStorageProvider : IFileStorageProvider
{
    private readonly string _rootPath;
    private readonly ILogger<LocalFileStorageProvider> _logger;

    private static readonly Dictionary<string, string> MimeToExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        { "application/pdf", ".pdf" },
        { "image/jpeg", ".jpg" },
        { "image/png", ".png" },
        { "image/gif", ".gif" },
        { "text/plain", ".txt" },
        { "application/json", ".json" },
        { "application/xml", ".xml" },
        { "application/zip", ".zip" },
        { "text/csv", ".csv" },
        { "text/markdown", ".md" },
        { "application/octet-stream", ".bin" }
    };

    private static readonly Dictionary<string, string> ExtensionToMime = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".pdf", "application/pdf" },
        { ".jpg", "image/jpeg" },
        { ".jpeg", "image/jpeg" },
        { ".png", "image/png" },
        { ".gif", "image/gif" },
        { ".txt", "text/plain" },
        { ".json", "application/json" },
        { ".xml", "application/xml" },
        { ".zip", "application/zip" },
        { ".csv", "text/csv" },
        { ".md", "text/markdown" },
        { ".bin", "application/octet-stream" }
    };

    public LocalFileStorageProvider(
        IConfiguration configuration,
        ILogger<LocalFileStorageProvider> logger)
    {
        var configuredPath = configuration["Storage:Local:RootPath"];

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            configuredPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".knowz-selfhosted", "files");
        }
        else if (configuredPath.StartsWith("~"))
        {
            configuredPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                configuredPath[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        _rootPath = Path.GetFullPath(configuredPath);
        Directory.CreateDirectory(_rootPath);
        _logger = logger;

        _logger.LogInformation("LocalFileStorageProvider initialized with root path: {RootPath}", _rootPath);
    }

    private string GetTenantDirectory(Guid tenantId)
        => Path.Combine(_rootPath, tenantId.ToString("N"));

    private string? FindFile(Guid tenantId, Guid fileRecordId)
    {
        var tenantDir = GetTenantDirectory(tenantId);
        if (!Directory.Exists(tenantDir))
            return null;

        var pattern = $"{fileRecordId:N}.*";
        var files = Directory.GetFiles(tenantDir, pattern);
        return files.Length > 0 ? files[0] : null;
    }

    private void ValidatePath(string resolvedPath)
    {
        var fullResolved = Path.GetFullPath(resolvedPath);
        if (!fullResolved.StartsWith(_rootPath, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException($"Directory traversal detected: {resolvedPath}");
    }

    public async Task<string> UploadAsync(
        Guid tenantId, Guid fileRecordId, Stream stream, string contentType, CancellationToken ct = default)
    {
        var tenantDir = GetTenantDirectory(tenantId);
        ValidatePath(tenantDir);
        Directory.CreateDirectory(tenantDir);

        var ext = MimeToExtension.GetValueOrDefault(contentType, ".bin");
        var filePath = Path.Combine(tenantDir, $"{fileRecordId:N}{ext}");
        ValidatePath(filePath);

        await using var fileStream = File.Create(filePath);
        await stream.CopyToAsync(fileStream, ct).ConfigureAwait(false);

        _logger.LogInformation("Uploaded file to {FilePath} ({ContentType})", filePath, contentType);
        return $"file:///{filePath.Replace('\\', '/')}";
    }

    public Task<(Stream stream, string contentType, string fileName)> DownloadAsync(
        Guid tenantId, Guid fileRecordId, CancellationToken ct = default)
    {
        var filePath = FindFile(tenantId, fileRecordId);
        if (filePath == null)
            throw new FileNotFoundException($"File not found for tenant {tenantId:N}, file {fileRecordId:N}");

        var ext = Path.GetExtension(filePath);
        var contentType = ExtensionToMime.GetValueOrDefault(ext, "application/octet-stream");
        var fileName = Path.GetFileName(filePath);

        Stream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult((stream, contentType, fileName));
    }

    public Task<bool> DeleteAsync(
        Guid tenantId, Guid fileRecordId, CancellationToken ct = default)
    {
        var filePath = FindFile(tenantId, fileRecordId);
        if (filePath == null)
            return Task.FromResult(false);

        File.Delete(filePath);
        _logger.LogInformation("Deleted file {FilePath}", filePath);
        return Task.FromResult(true);
    }

    public Task<bool> ExistsAsync(
        Guid tenantId, Guid fileRecordId, CancellationToken ct = default)
    {
        var filePath = FindFile(tenantId, fileRecordId);
        return Task.FromResult(filePath != null);
    }

    public Task<string> GenerateDownloadUrlAsync(
        Guid tenantId, Guid fileRecordId, string fileName, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        var filePath = FindFile(tenantId, fileRecordId);
        if (filePath == null)
            throw new FileNotFoundException($"File not found for tenant {tenantId:N}, file {fileRecordId:N}");

        _logger.LogWarning("Local file URIs do not support expiry or Content-Disposition headers");
        return Task.FromResult($"file:///{Path.GetFullPath(filePath).Replace('\\', '/')}");
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            if (!Directory.Exists(_rootPath))
                return Task.FromResult(false);

            // Test writability
            var testFile = Path.Combine(_rootPath, $".healthcheck-{Guid.NewGuid():N}");
            File.WriteAllText(testFile, "ok");
            File.Delete(testFile);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}
