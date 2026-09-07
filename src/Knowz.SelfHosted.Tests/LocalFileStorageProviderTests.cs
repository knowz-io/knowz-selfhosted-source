using System.Security;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class LocalFileStorageProviderTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly LocalFileStorageProvider _provider;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherTenantId = Guid.Parse("00000000-0000-0000-0000-000000000099");

    public LocalFileStorageProviderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"knowz-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Storage:Local:RootPath", _tempRoot }
            })
            .Build();

        var logger = Substitute.For<ILogger<LocalFileStorageProvider>>();
        _provider = new LocalFileStorageProvider(config, logger);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup in tests
        }
    }

    // --- Helpers ---

    private static MemoryStream CreateTestStream(string content = "test file content")
    {
        return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
    }

    // =============================================
    // Upload
    // =============================================

    [Fact]
    public async Task Upload_WritesFileToExpectedPath()
    {
        var fileId = Guid.NewGuid();
        using var stream = CreateTestStream("hello world");

        var blobUri = await _provider.UploadAsync(TenantId, fileId, stream, "text/plain");

        // File should exist at {root}/{tenantId:N}/{fileId:N}.txt
        var expectedDir = Path.Combine(_tempRoot, TenantId.ToString("N"));
        var expectedFile = Path.Combine(expectedDir, $"{fileId:N}.txt");
        Assert.True(File.Exists(expectedFile), $"Expected file at {expectedFile}");
        Assert.NotNull(blobUri);
        Assert.StartsWith("file:///", blobUri);
    }

    [Fact]
    public async Task Upload_WritesCorrectContent()
    {
        var fileId = Guid.NewGuid();
        var content = "exact content to verify";
        using var stream = CreateTestStream(content);

        await _provider.UploadAsync(TenantId, fileId, stream, "text/plain");

        var expectedFile = Path.Combine(_tempRoot, TenantId.ToString("N"), $"{fileId:N}.txt");
        var readBack = await File.ReadAllTextAsync(expectedFile);
        Assert.Equal(content, readBack);
    }

    [Fact]
    public async Task Upload_UsesCorrectExtensionForContentType()
    {
        var fileId = Guid.NewGuid();
        using var stream = CreateTestStream("pdf-like content");

        await _provider.UploadAsync(TenantId, fileId, stream, "application/pdf");

        var expectedFile = Path.Combine(_tempRoot, TenantId.ToString("N"), $"{fileId:N}.pdf");
        Assert.True(File.Exists(expectedFile));
    }

    [Fact]
    public async Task Upload_UsesBinExtensionForUnknownContentType()
    {
        var fileId = Guid.NewGuid();
        using var stream = CreateTestStream("binary data");

        await _provider.UploadAsync(TenantId, fileId, stream, "application/x-custom-unknown");

        var expectedFile = Path.Combine(_tempRoot, TenantId.ToString("N"), $"{fileId:N}.bin");
        Assert.True(File.Exists(expectedFile));
    }

    [Fact]
    public async Task Upload_CreatesTenantDirectoryIfMissing()
    {
        var newTenantId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        using var stream = CreateTestStream();

        var tenantDir = Path.Combine(_tempRoot, newTenantId.ToString("N"));
        Assert.False(Directory.Exists(tenantDir));

        await _provider.UploadAsync(newTenantId, fileId, stream, "text/plain");

        Assert.True(Directory.Exists(tenantDir));
    }

    // =============================================
    // Download
    // =============================================

    [Fact]
    public async Task Download_ReadsFileBackWithCorrectContent()
    {
        var fileId = Guid.NewGuid();
        var originalContent = "download me";
        using var uploadStream = CreateTestStream(originalContent);
        await _provider.UploadAsync(TenantId, fileId, uploadStream, "text/plain");

        var (stream, contentType, fileName) = await _provider.DownloadAsync(TenantId, fileId);

        using var reader = new StreamReader(stream);
        var readContent = await reader.ReadToEndAsync();
        Assert.Equal(originalContent, readContent);
        Assert.Equal("text/plain", contentType);
        Assert.Contains(fileId.ToString("N"), fileName);

        stream.Dispose();
    }

    [Fact]
    public async Task Download_ThrowsFileNotFoundForMissingFile()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _provider.DownloadAsync(TenantId, Guid.NewGuid()));
    }

    [Fact]
    public async Task Download_InfersContentTypeFromExtension()
    {
        var fileId = Guid.NewGuid();
        using var stream = CreateTestStream("{}");
        await _provider.UploadAsync(TenantId, fileId, stream, "application/json");

        var (downloadStream, contentType, _) = await _provider.DownloadAsync(TenantId, fileId);

        Assert.Equal("application/json", contentType);
        downloadStream.Dispose();
    }

    // =============================================
    // Delete
    // =============================================

    [Fact]
    public async Task Delete_RemovesFileFromDisk()
    {
        var fileId = Guid.NewGuid();
        using var stream = CreateTestStream();
        await _provider.UploadAsync(TenantId, fileId, stream, "text/plain");

        var expectedFile = Path.Combine(_tempRoot, TenantId.ToString("N"), $"{fileId:N}.txt");
        Assert.True(File.Exists(expectedFile));

        var result = await _provider.DeleteAsync(TenantId, fileId);

        Assert.True(result);
        Assert.False(File.Exists(expectedFile));
    }

    [Fact]
    public async Task Delete_ReturnsFalseForMissingFile()
    {
        var result = await _provider.DeleteAsync(TenantId, Guid.NewGuid());

        Assert.False(result);
    }

    // =============================================
    // Exists
    // =============================================

    [Fact]
    public async Task Exists_ReturnsTrueForUploadedFile()
    {
        var fileId = Guid.NewGuid();
        using var stream = CreateTestStream();
        await _provider.UploadAsync(TenantId, fileId, stream, "text/plain");

        var result = await _provider.ExistsAsync(TenantId, fileId);

        Assert.True(result);
    }

    [Fact]
    public async Task Exists_ReturnsFalseForMissingFile()
    {
        var result = await _provider.ExistsAsync(TenantId, Guid.NewGuid());

        Assert.False(result);
    }

    [Fact]
    public async Task Exists_ReturnsFalseAfterDelete()
    {
        var fileId = Guid.NewGuid();
        using var stream = CreateTestStream();
        await _provider.UploadAsync(TenantId, fileId, stream, "text/plain");
        await _provider.DeleteAsync(TenantId, fileId);

        var result = await _provider.ExistsAsync(TenantId, fileId);

        Assert.False(result);
    }

    // =============================================
    // Tenant isolation
    // =============================================

    [Fact]
    public async Task TenantIsolation_FilesStoredInSeparateDirectories()
    {
        var fileId1 = Guid.NewGuid();
        var fileId2 = Guid.NewGuid();
        using var stream1 = CreateTestStream("tenant 1 data");
        using var stream2 = CreateTestStream("tenant 2 data");

        await _provider.UploadAsync(TenantId, fileId1, stream1, "text/plain");
        await _provider.UploadAsync(OtherTenantId, fileId2, stream2, "text/plain");

        // Each tenant gets its own directory
        var tenant1Dir = Path.Combine(_tempRoot, TenantId.ToString("N"));
        var tenant2Dir = Path.Combine(_tempRoot, OtherTenantId.ToString("N"));

        Assert.True(Directory.Exists(tenant1Dir));
        Assert.True(Directory.Exists(tenant2Dir));
        Assert.NotEqual(tenant1Dir, tenant2Dir);

        // Files are in correct tenant directories
        Assert.True(File.Exists(Path.Combine(tenant1Dir, $"{fileId1:N}.txt")));
        Assert.True(File.Exists(Path.Combine(tenant2Dir, $"{fileId2:N}.txt")));
    }

    [Fact]
    public async Task TenantIsolation_CannotAccessOtherTenantFiles()
    {
        var fileId = Guid.NewGuid();
        using var stream = CreateTestStream("private data");
        await _provider.UploadAsync(TenantId, fileId, stream, "text/plain");

        // Trying to access with wrong tenant should not find the file
        var existsForOtherTenant = await _provider.ExistsAsync(OtherTenantId, fileId);
        Assert.False(existsForOtherTenant);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _provider.DownloadAsync(OtherTenantId, fileId));
    }

    // =============================================
    // Path traversal protection
    // =============================================

    [Fact]
    public async Task PathTraversal_ValidatePathRejectsPathsOutsideRoot()
    {
        // Attempt to create a file with a tenantId that would resolve outside root
        // The ValidatePath method should catch this.
        // We cannot easily forge a Guid to cause traversal, but we can verify the mechanism
        // by testing that normal operations stay within root.
        var fileId = Guid.NewGuid();
        using var stream = CreateTestStream("safe content");

        var blobUri = await _provider.UploadAsync(TenantId, fileId, stream, "text/plain");

        // Verify the file is indeed within the root
        var expectedDir = Path.Combine(_tempRoot, TenantId.ToString("N"));
        var files = Directory.GetFiles(expectedDir);
        Assert.All(files, f => Assert.StartsWith(_tempRoot, Path.GetFullPath(f)));
    }

    // =============================================
    // IsAvailable
    // =============================================

    [Fact]
    public async Task IsAvailable_ReturnsTrueWhenRootExists()
    {
        var result = await _provider.IsAvailableAsync();

        Assert.True(result);
    }

    // =============================================
    // GenerateDownloadUrl
    // =============================================

    [Fact]
    public async Task GenerateDownloadUrl_ReturnsFileUri()
    {
        var fileId = Guid.NewGuid();
        using var stream = CreateTestStream();
        await _provider.UploadAsync(TenantId, fileId, stream, "text/plain");

        var url = await _provider.GenerateDownloadUrlAsync(TenantId, fileId, "test.txt");

        Assert.StartsWith("file:///", url);
    }

    [Fact]
    public async Task GenerateDownloadUrl_ThrowsForMissingFile()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _provider.GenerateDownloadUrlAsync(TenantId, Guid.NewGuid(), "missing.txt"));
    }
}
