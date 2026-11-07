using System.Net;
using System.Text;
using System.Text.Json;
using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Application.Validators;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed class FileSyncPhysicalPullTests
{
    [PostgresTheory]
    [InlineData(true, "none")]
    [InlineData(false, "none")]
    [InlineData(true, "http-failure")]
    [InlineData(true, "revoked")]
    [InlineData(true, "link-changed")]
    [InlineData(true, "bad-length")]
    public async Task SuccessfulPullActivatesDownloadedBytesForNormalAndArchiveReaders(bool previouslyMapped, string fault)
    {
        var tenantId = Guid.NewGuid(); var fileId = Guid.NewGuid(); var oldBlob = Guid.NewGuid();
        var directory = Path.Combine(Path.GetTempPath(),"kc-pointer-pull-"+Guid.NewGuid().ToString("N"));
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(tenantId);
        var configured = Environment.GetEnvironmentVariable("KNOWZ_TEST_POSTGRES"); Assert.False(string.IsNullOrWhiteSpace(configured));
        var database = new NpgsqlConnectionStringBuilder(configured);
        Assert.True(database.Host is "localhost" or "127.0.0.1" && database.Port == 18460);
        database.Database = "postgres"; database.Pooling = false;
        await using var admin = new NpgsqlConnection(database.ConnectionString); await admin.OpenAsync();
        var databaseName = "kc_pointer_pull_" + Guid.NewGuid().ToString("N");
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"",admin)) await create.ExecuteNonQueryAsync();
        database.Database = databaseName;
        await using var db = new SelfHostedDbContext(new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseNpgsql(database.ConnectionString,provider => provider.EnableRetryOnFailure()).Options,tenant);
        var storage = new LocalFileStorageProvider(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Storage:Local:RootPath"] = directory }).Build(),NullLogger<LocalFileStorageProvider>.Instance);
        var bytes = Encoding.UTF8.GetBytes("Downloaded exact replacement — 猫"); var old = Encoding.UTF8.GetBytes("Previous physical object");
        try
        {
            await db.Database.MigrateAsync();
            var actor = new User { TenantId = tenantId, Username = "pull-fixture", PasswordHash = "synthetic", Role = UserRole.Admin };
            var vault = new Vault { TenantId = tenantId, Name = "Pull vault" };
            db.Tenants.Add(new() { Id = tenantId, Name = "Pull tenant", Slug = "pull-fixture" }); db.Users.Add(actor); db.Vaults.Add(vault);
            db.UserTenantMemberships.Add(new() { UserId = actor.Id, TenantId = tenantId, Role = UserRole.Admin });
            var protection = new EphemeralDataProtectionProvider();
            var connection = new PlatformConnection { TenantId = tenantId, PlatformApiUrl = "https://pointer-pull.example", CreatedByUserId = actor.Id,
                ApiKeyProtected = protection.CreateProtector(PlatformConnectionService.MasterPurpose).CreateProtector($"{PlatformConnectionService.MasterPurpose}.{tenantId}").Protect("ukz_synthetic-pointer-pull") };
            db.PlatformConnections.Add(connection);
            var link = new VaultSyncLink { LocalVaultId = vault.Id, RemoteVaultId = Guid.NewGuid(), RemoteTenantId = Guid.NewGuid(), PlatformConnectionId = connection.Id };
            db.VaultSyncLinks.Add(link);
            if (previouslyMapped)
            {
                db.FileRecords.Add(new() { Id = fileId, TenantId = tenantId, FileName = "previous.txt", SizeBytes = old.Length, BlobUri = "local://previous", ContentType = "text/plain" });
                db.ShFileObjectReferences.Add(new() { TenantId = tenantId, FileRecordId = fileId, BlobStorageId = oldBlob, Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(old)).ToLowerInvariant() });
                await storage.UploadAsync(tenantId,oldBlob,new MemoryStream(old),"text/plain");
            }
            await db.SaveChangesAsync();
            using var handler = new PullHandler(fileId,bytes,fault, async () =>
            {
                if (fault == "revoked") actor.IsActive = false;
                if (fault == "link-changed") link.SyncEnabled = false;
                await db.SaveChangesAsync();
            }); var factory = Substitute.For<IHttpClientFactory>(); factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler,false));
            var validator = Substitute.For<IUrlValidator>(); validator.ValidatePlatformUrl(Arg.Any<string>()).Returns(new UrlValidationResult(true,null));
            var credentials = new PlatformSyncCredentialResolver(factory,protection,validator,db,NullLogger<PlatformSyncCredentialResolver>.Instance);
            var result = await new FileSyncService(db,tenant,storage,credentials,NullLogger<FileSyncService>.Instance).SyncFilesAsync(link);
            if (fault != "none")
            {
                Assert.False(result.Success); Assert.NotEmpty(result.Errors); Assert.Equal(0,result.Downloaded);
                Assert.Equal(oldBlob, (await db.ShFileObjectReferences.SingleAsync()).BlobStorageId);
            }
            else { Assert.True(result.Success, string.Join("; ",result.Errors)); Assert.Empty(result.Errors); Assert.Equal(1,result.Downloaded); Assert.Equal(0,result.Uploaded); }
            var expected = fault == "none" ? bytes : old;
            var ordinary = new FileStorageService(storage,new SelfHostedRepository<FileRecord>(db),db,tenant,NullLogger<FileStorageService>.Instance);
            var download = await ordinary.DownloadAsync(fileId); Assert.NotNull(download);
            await using (var stream = download.Value.stream) { using var read = new MemoryStream(); await stream.CopyToAsync(read); Assert.Equal(expected,read.ToArray()); }
            var graph = new SelfHostedGraphStore(db,tenant,new PortableImportService(db,tenant,storage,NullLogger<PortableImportService>.Instance),
                new PortableExportService(db,tenant,storage,Options.Create(new SelfHostedOptions()),NullLogger<PortableExportService>.Instance),storage);
            await using var archiveRead = await graph.OpenFileAsync(fileId,default); using var archiveBytes = new MemoryStream(); await archiveRead.CopyToAsync(archiveBytes); Assert.Equal(expected,archiveBytes.ToArray());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)",admin);
            await drop.ExecuteNonQueryAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory,true);
        }
    }

    private sealed class PullHandler(Guid fileId,byte[] bytes,string fault,Func<Task> beforeDownload) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get,request.Method);
            if (request.RequestUri!.AbsolutePath.EndsWith("/manifest",StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { data = new { files = new[] { new { fileRecordId = fileId, fileName = "download.txt", contentType = "text/plain", sizeBytes = bytes.Length + (fault == "bad-length" ? 1 : 0), updatedAt = DateTime.UtcNow } } } }),Encoding.UTF8,"application/json") };
            Assert.EndsWith(fileId.ToString("D"),request.RequestUri.AbsolutePath);
            await beforeDownload();
            return new HttpResponseMessage(fault == "http-failure" ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }
}
