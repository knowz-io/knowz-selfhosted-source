using System.Net;
using System.Text;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Application.Validators;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// NodeID FIX_SelfHostedFileSyncAuth — the mothership file lane must authenticate exactly like
/// the entity lane: PlatformConnection decrypt, registered "KnowzPlatformSync" client, URL
/// re-validation, 10-minute instance timeout. No live mothership; an HttpMessageHandler stub
/// records every outbound request.
/// </summary>
public class FileSyncServiceTests : IDisposable
{
    private const string BaseUrl = "https://api.test.knowz.io";
    private const string ApiKey = "ukz_test_filesync_plaintext_key_0123";
    private const string NamedClient = "KnowzPlatformSync";

    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid LocalVaultId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid RemoteVaultId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid ConnectionId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid RemoteFileId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();
    private readonly IFileStorageProvider _storage = Substitute.For<IFileStorageProvider>();
    private readonly RecordingHandler _handler = new();
    private readonly List<HttpClient> _createdClients = new();
    private readonly IHttpClientFactory _factory = Substitute.For<IHttpClientFactory>();

    public FileSyncServiceTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _tenantProvider = Substitute.For<ITenantProvider>();
        _tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, _tenantProvider);

        // Mirror the Program.cs registration: 30s timeout, 50 MB buffer. Every name is honoured
        // so a wrong name (e.g. the old unregistered "PlatformSync") is caught by assertion, not by
        // an accidental null client.
        _factory.CreateClient(Arg.Any<string>()).Returns(_ =>
        {
            var client = new HttpClient(_handler, disposeHandler: false)
            {
                Timeout = TimeSpan.FromSeconds(30),
                MaxResponseContentBufferSize = 50 * 1024 * 1024,
            };
            _createdClients.Add(client);
            return client;
        });
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------ helpers

    private string Protect(string plaintext, Guid tenantId) =>
        _dataProtection
            .CreateProtector(PlatformConnectionService.MasterPurpose)
            .CreateProtector($"{PlatformConnectionService.MasterPurpose}.{tenantId}")
            .Protect(plaintext);

    private static IUrlValidator OkValidator()
    {
        var v = Substitute.For<IUrlValidator>();
        v.ValidatePlatformUrl(Arg.Any<string>()).Returns(new UrlValidationResult(true, null));
        return v;
    }

    private (FileSyncService service, PlatformSyncCredentialResolver resolver) Build(IUrlValidator? validator = null)
    {
        var resolver = new PlatformSyncCredentialResolver(
            _factory, _dataProtection, validator ?? OkValidator(), _db,
            Substitute.For<ILogger<PlatformSyncCredentialResolver>>());
        var service = new FileSyncService(
            _db, _tenantProvider, _storage, resolver,
            Substitute.For<ILogger<FileSyncService>>());
        return (service, resolver);
    }

    private VaultSyncLink SeedConnectionLink(string? apiKeyProtected = null)
    {
        _db.PlatformConnections.Add(new PlatformConnection
        {
            Id = ConnectionId,
            TenantId = TenantId,
            PlatformApiUrl = BaseUrl,
            ApiKeyProtected = apiKeyProtected ?? Protect(ApiKey, TenantId),
            ApiKeyLast4 = ApiKey[^4..],
            CreatedByUserId = Guid.NewGuid(),
        });
        var link = new VaultSyncLink
        {
            LocalVaultId = LocalVaultId,
            RemoteVaultId = RemoteVaultId,
            RemoteTenantId = Guid.NewGuid(),
            PlatformConnectionId = ConnectionId,
        };
        _db.VaultSyncLinks.Add(link);
        _db.SaveChanges();
        return link;
    }

    private VaultSyncLink SeedLegacyLink()
    {
#pragma warning disable CS0618
        var link = new VaultSyncLink
        {
            LocalVaultId = LocalVaultId,
            RemoteVaultId = RemoteVaultId,
            RemoteTenantId = Guid.NewGuid(),
            PlatformConnectionId = null,
            PlatformApiUrl = BaseUrl,
            ApiKeyEncrypted = ApiKey,
        };
#pragma warning restore CS0618
        _db.VaultSyncLinks.Add(link);
        _db.SaveChanges();
        return link;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static string EmptyManifest() =>
        "{\"success\":true,\"data\":{\"files\":[],\"serverTimestamp\":\"2026-09-04T00:00:00Z\"}}";

    private static string ManifestWith(Guid fileId, long size) =>
        "{\"success\":true,\"data\":{\"files\":[{\"fileRecordId\":\"" + fileId +
        "\",\"fileName\":\"notes.txt\",\"sizeBytes\":" + size +
        ",\"contentType\":\"text/plain\",\"updatedAt\":\"2026-09-01T00:00:00Z\"}],\"serverTimestamp\":\"2026-09-04T00:00:00Z\"}}";

    private static string? ApiKeyHeader(HttpRequestMessage req) =>
        req.Headers.TryGetValues("X-Api-Key", out var v) ? string.Join(",", v) : null;

    // ------------------------------------------------------------------ VERIFY-FS1

    [Fact]
    public async Task SyncFiles_ConnectionLink_SendsDecryptedPlaintextKey_NeverCiphertext()
    {
        var link = SeedConnectionLink();
        var ciphertext = _db.PlatformConnections.Single().ApiKeyProtected;
        _handler.ResponseFactory = _ => Json(EmptyManifest());
        var (service, _) = Build();

        var result = await service.SyncFilesAsync(link);

        Assert.True(result.Success, string.Join(" | ", result.Errors));
        var req = Assert.Single(_handler.Requests);
        Assert.Equal(ApiKey, ApiKeyHeader(req));
        Assert.NotEqual(ciphertext, ApiKeyHeader(req));
        Assert.DoesNotContain(ciphertext, req.Headers.ToString());
    }

    // ------------------------------------------------------------------ VERIFY-FS2

    [Fact]
    public async Task SyncFiles_LegacyLink_StillAuthenticatesFromObsoleteColumns()
    {
        var link = SeedLegacyLink();
        _handler.ResponseFactory = _ => Json(EmptyManifest());
        var (service, _) = Build();

        var result = await service.SyncFilesAsync(link);

        Assert.True(result.Success, string.Join(" | ", result.Errors));
        var req = Assert.Single(_handler.Requests);
        Assert.Equal(ApiKey, ApiKeyHeader(req));
        Assert.Equal(new Uri($"{BaseUrl}/api/v1/sync/vaults/{RemoteVaultId}/files/manifest"), req.RequestUri);
    }

    // ------------------------------------------------------------------ VERIFY-FS3

    [Fact]
    public async Task SyncFiles_UsesRegisteredNamedClient_NotUnregisteredPlatformSync()
    {
        var link = SeedConnectionLink();
        _handler.ResponseFactory = _ => Json(EmptyManifest());
        var (service, _) = Build();

        await service.SyncFilesAsync(link);

        _factory.Received().CreateClient(NamedClient);
        _factory.DidNotReceive().CreateClient("PlatformSync");
        Assert.Equal(NamedClient, PlatformSyncCredentialResolver.HttpClientName);
    }

    // ------------------------------------------------------------------ VERIFY-FS4

    [Fact]
    public async Task SyncFiles_OverridesInstanceTimeoutToTenMinutes_OnEveryFileClient()
    {
        var link = SeedConnectionLink();
        _handler.ResponseFactory = req =>
            req.RequestUri!.AbsolutePath.EndsWith("/manifest")
                ? Json(ManifestWith(RemoteFileId, 11))
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("hello-bytes") };
        _storage.UploadAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("ok");
        var (service, _) = Build();

        await service.SyncFilesAsync(link);

        Assert.Equal(2, _createdClients.Count); // manifest + one download
        Assert.All(_createdClients, c => Assert.Equal(TimeSpan.FromMinutes(10), c.Timeout));
    }

    // ------------------------------------------------------------------ VERIFY-FS5

    [Fact]
    public async Task SyncFiles_InvalidPlatformUrl_NoHttp_SameErrorClassAsPlatformSyncClient()
    {
        var link = SeedConnectionLink();
        var validator = Substitute.For<IUrlValidator>();
        validator.ValidatePlatformUrl(Arg.Any<string>())
            .Returns(new UrlValidationResult(false, "Host not on allowlist", UrlValidationErrorCode.NotAllowlisted));
        _handler.ResponseFactory = _ => Json(EmptyManifest());
        var (service, resolver) = Build(validator);

        var result = await service.SyncFilesAsync(link);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("Platform URL is no longer allowed"));
        Assert.Empty(_handler.Requests);
        // Same exception class as PlatformSyncClient.CreateClient.
        var ex = Assert.Throws<InvalidOperationException>(() => resolver.CreatePlatformHttpClient(link));
        Assert.Contains("no longer allowed", ex.Message);
        Assert.DoesNotContain(ApiKey, ex.ToString());
    }

    // ------------------------------------------------------------------ VERIFY-FS6

    [Fact]
    public async Task SyncFiles_CorruptCiphertext_NoHttp_ReportsCorruptCredentialMessage()
    {
        var link = SeedConnectionLink(apiKeyProtected: "not-a-dataprotection-payload");
        _handler.ResponseFactory = _ => Json(EmptyManifest());
        var (service, _) = Build();

        var result = await service.SyncFilesAsync(link);

        Assert.False(result.Success);
        Assert.Contains(PlatformConnectionService.MsgCorruptCiphertext, result.Errors);
        Assert.Empty(_handler.Requests);
        Assert.DoesNotContain(result.Errors, e => e.Contains("not-a-dataprotection-payload"));
    }

    [Fact]
    public async Task SyncFiles_MissingConnectionRow_NoHttp()
    {
        var link = new VaultSyncLink
        {
            LocalVaultId = LocalVaultId, RemoteVaultId = RemoteVaultId,
            RemoteTenantId = Guid.NewGuid(), PlatformConnectionId = Guid.NewGuid(),
        };
        _db.VaultSyncLinks.Add(link);
        _db.SaveChanges();
        var (service, _) = Build();

        var result = await service.SyncFilesAsync(link);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("Platform connection not found"));
        Assert.Empty(_handler.Requests);
    }

    // ------------------------------------------------------------------ stream download proof

    [Fact]
    public async Task SyncFiles_DownloadsStreamedBytesThroughApi_AndCreatesLocalFileRecord()
    {
        var link = SeedConnectionLink();
        string? stored = null;
        _handler.ResponseFactory = req =>
            req.RequestUri!.AbsolutePath.EndsWith("/manifest")
                ? Json(ManifestWith(RemoteFileId, 11))
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("hello-bytes", Encoding.UTF8, "text/plain")
                };
        _storage.UploadAsync(TenantId, RemoteFileId, Arg.Any<Stream>(), "text/plain", Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                using var reader = new StreamReader(ci.Arg<Stream>());
                stored = reader.ReadToEnd();
                return "ok";
            });
        var (service, _) = Build();

        var result = await service.SyncFilesAsync(link);

        Assert.True(result.Success, string.Join(" | ", result.Errors));
        Assert.Equal(1, result.Downloaded);
        Assert.Equal("hello-bytes", stored);
        Assert.Equal(2, _handler.Requests.Count);
        Assert.All(_handler.Requests, r => Assert.Equal(ApiKey, ApiKeyHeader(r)));
        Assert.Equal(
            new Uri($"{BaseUrl}/api/v1/sync/vaults/{RemoteVaultId}/files/{RemoteFileId}"),
            _handler.Requests[1].RequestUri);
        var record = await _db.FileRecords.FindAsync(RemoteFileId);
        Assert.NotNull(record);
        Assert.Equal("notes.txt", record!.FileName);
        Assert.Equal(TenantId, record.TenantId);
    }

    [Fact]
    public async Task SyncFiles_ManifestServerError_ErrorTextNeverContainsKey()
    {
        var link = SeedConnectionLink();
        _handler.ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom")
        };
        var (service, _) = Build();

        var result = await service.SyncFilesAsync(link);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.DoesNotContain(result.Errors, e => e.Contains(ApiKey));
    }

    // ------------------------------------------------------------------ handler

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(ResponseFactory?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
