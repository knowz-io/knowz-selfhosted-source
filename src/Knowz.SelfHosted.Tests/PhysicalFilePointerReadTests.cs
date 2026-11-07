using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.API.Endpoints;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Application.Validators;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed class PhysicalFilePointerReadTests : IDisposable
{
    private readonly Guid tenantId = Guid.NewGuid(), fileId = Guid.NewGuid(), blobId = Guid.NewGuid(), vaultId = Guid.NewGuid();
    private readonly string directory = Path.Combine(Path.GetTempPath(), "kc-pointer-read-" + Guid.NewGuid().ToString("N"));
    private readonly ITenantProvider tenant = Substitute.For<ITenantProvider>();
    private readonly SelfHostedDbContext db;
    private readonly LocalFileStorageProvider storage;
    private static readonly byte[] Current = Encoding.UTF8.GetBytes("Committed replacement bytes — 猫");
    private static readonly byte[] Old = Encoding.UTF8.GetBytes("Obsolete logical-ID bytes must not be returned");

    public PhysicalFilePointerReadTests()
    {
        tenant.TenantId.Returns(tenantId);
        db = new(new DbContextOptionsBuilder<SelfHostedDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
        storage = new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Storage:Local:RootPath"] = directory }).Build(), NullLogger<LocalFileStorageProvider>.Instance);
    }
    public void Dispose() { db.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory,true); }
    private async Task Seed(string pointer)
    {
        var item = new Knowledge { TenantId = tenantId, Title = "Readable owner", Content = "Readable body" };
        db.KnowledgeItems.Add(item); db.Vaults.Add(new() { Id = vaultId, TenantId = tenantId, Name = "Pointer vault" });
        db.KnowledgeVaults.Add(new() { KnowledgeId = item.Id, VaultId = vaultId, TenantId = tenantId });
        db.FileRecords.Add(new() { Id = fileId, TenantId = tenantId, FileName = "sample.txt", ContentType = "text/plain", SizeBytes = Current.Length, BlobMigrationPending = false });
        db.FileAttachments.Add(new() { FileRecordId = fileId, KnowledgeId = item.Id, TenantId = tenantId });
        if (pointer != "none") db.ShFileObjectReferences.Add(new() { FileRecordId = fileId, TenantId = pointer == "foreign" ? Guid.NewGuid() : tenantId,
            BlobStorageId = pointer == "empty" ? Guid.Empty : blobId, Sha256 = Convert.ToHexString(SHA256.HashData(Current)).ToLowerInvariant() });
        await db.SaveChangesAsync();
        await storage.UploadAsync(tenantId,fileId,new MemoryStream(Old),"text/plain");
        await storage.UploadAsync(tenantId,blobId,new MemoryStream(Current),"text/plain");
    }

    [Theory]
    [InlineData(false,"valid")]
    [InlineData(true,"valid")]
    [InlineData(false,"foreign")]
    [InlineData(true,"foreign")]
    [InlineData(false,"empty")]
    [InlineData(true,"empty")]
    [InlineData(false,"none")]
    [InlineData(true,"none")]
    public async Task LegacyExportsResolveActivePhysicalIdentityAndNeverFallbackFromCorruptPointer(bool zip,string pointer)
    {
        await Seed(pointer);
        var exporter = new PortableExportService(db,tenant,storage,Options.Create(new SelfHostedOptions { IncludeBinaryContent = !zip }),NullLogger<PortableExportService>.Instance);
        byte[]? actual = null;
        if (zip)
        {
            await using var stream = await exporter.ExportZipAsync(); using var archive = new ZipArchive(stream,ZipArchiveMode.Read);
            var package = PortableZipReader.ReadFromZip(archive); var file = Assert.Single(package.Data.FileRecords);
            if (file.BinaryFilePath != null) { await using var binary = archive.GetEntry(file.BinaryFilePath)!.Open(); using var buffer = new MemoryStream(); await binary.CopyToAsync(buffer); actual = buffer.ToArray(); }
        }
        else
        {
            var file = Assert.Single((await exporter.ExportAsync()).Data.FileRecords);
            if (file.BinaryContentBase64 != null) actual = Convert.FromBase64String(file.BinaryContentBase64);
        }
        if (pointer is "foreign" or "empty") Assert.Null(actual);
        else Assert.Equal(pointer == "none" ? Old : Current,actual);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("foreign")]
    public async Task FileSyncPushReadsCommittedPhysicalBytesOnly(string pointer)
    {
        await Seed(pointer); var protection = new EphemeralDataProtectionProvider();
        var connection = new PlatformConnection { TenantId = tenantId, PlatformApiUrl = "https://pointer-source.example",
            ApiKeyProtected = protection.CreateProtector(PlatformConnectionService.MasterPurpose).CreateProtector($"{PlatformConnectionService.MasterPurpose}.{tenantId}").Protect("ukz_synthetic-pointer-fixture") };
        db.PlatformConnections.Add(connection); await db.SaveChangesAsync();
        var link = new VaultSyncLink { LocalVaultId = vaultId, RemoteVaultId = Guid.NewGuid(), PlatformConnectionId = connection.Id };
        using var handler = new PushHandler(); var factory = Substitute.For<IHttpClientFactory>(); factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler,false));
        var validator = Substitute.For<IUrlValidator>(); validator.ValidatePlatformUrl(Arg.Any<string>()).Returns(new UrlValidationResult(true,null));
        var credentials = new PlatformSyncCredentialResolver(factory,protection,validator,db,NullLogger<PlatformSyncCredentialResolver>.Instance);
        var result = await new FileSyncService(db,tenant,storage,credentials,NullLogger<FileSyncService>.Instance).SyncFilesAsync(link);
        Assert.Equal(pointer != "foreign",result.Success);
        if (pointer == "foreign") { Assert.Equal(0,result.Uploaded); Assert.Single(result.Errors); Assert.Null(handler.Bytes); }
        else { Assert.Equal(1,result.Uploaded); Assert.Empty(result.Errors); Assert.Equal(Current,handler.Bytes); }
    }

    [Theory]
    [InlineData("valid",true)]
    [InlineData("foreign",true)]
    [InlineData("valid",false)]
    public async Task AuthorizedAdminReadUsesPointerAndPreservesExistingRoleGate(string pointer,bool admin)
    {
        await Seed(pointer); var extractor = Substitute.For<IFileContentExtractor>(); byte[]? actual = null;
        extractor.CanExtract(Arg.Any<string?>()).Returns(true);
        extractor.ExtractAsync(Arg.Any<FileRecord>(),Arg.Any<Stream>(),Arg.Any<CancellationToken>()).Returns(async c => { using var buffer = new MemoryStream(); await c.Arg<Stream>().CopyToAsync(buffer); actual = buffer.ToArray(); return new FileExtractionResult(true,"fixture-extracted"); });
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Services.AddSingleton(db); builder.Services.AddSingleton<IFileStorageProvider>(storage); builder.Services.AddSingleton(extractor);
        builder.Services.AddSingleton(Substitute.For<IEnrichmentOutboxWriter>()); builder.Services.AddSingleton(Substitute.For<IAttachmentAIProvider>());
        await using var app = builder.Build(); app.Use(async (context,next) => { context.User = new(new ClaimsIdentity([new(ClaimTypes.Role,admin ? "Admin" : "User")],"fixture")); await next(); });
        app.MapAttachmentAIAdminEndpoints(); await app.StartAsync(); using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync("/api/admin/files/reprocess",new { fileIds = new[] { fileId } });
        if (!admin) { Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode); Assert.Null(actual); return; }
        response.EnsureSuccessStatusCode(); var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (pointer == "foreign") { Assert.Equal(1,body.GetProperty("errors").GetInt32()); Assert.Null(actual); }
        else { Assert.Equal(1,body.GetProperty("queued").GetInt32()); Assert.Equal(Current,actual); }
    }

    [Theory]
    [InlineData("valid",false)]
    [InlineData("valid",true)]
    [InlineData("none",false)]
    [InlineData("none",true)]
    [InlineData("foreign",false)]
    [InlineData("foreign",true)]
    [InlineData("empty",false)]
    [InlineData("empty",true)]
    [InlineData("deleted",false)]
    [InlineData("deleted",true)]
    public async Task NormalReadAndUrlResolveOnlyLiveTenantPhysicalIdentity(string pointer,bool url)
    {
        await Seed(pointer);
        if (pointer == "deleted") { (await db.FileRecords.SingleAsync()).IsDeleted = true; await db.SaveChangesAsync(); }
        var service = new FileStorageService(storage,new SelfHostedRepository<FileRecord>(db),db,tenant,NullLogger<FileStorageService>.Instance);
        if (pointer is "foreign" or "empty")
        {
            if (url) await Assert.ThrowsAsync<InvalidDataException>(() => service.GenerateDownloadUrlAsync(fileId));
            else await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(fileId));
            return;
        }
        if (url)
        {
            if (pointer == "deleted") await Assert.ThrowsAsync<FileNotFoundException>(() => service.GenerateDownloadUrlAsync(fileId));
            else Assert.Contains((pointer == "none" ? fileId : blobId).ToString("N"),await service.GenerateDownloadUrlAsync(fileId));
        }
        else
        {
            var result = await service.DownloadAsync(fileId);
            if (pointer == "deleted") { Assert.Null(result); return; }
            Assert.NotNull(result); await using var stream = result.Value.stream; using var bytes = new MemoryStream(); await stream.CopyToAsync(bytes);
            Assert.Equal(pointer == "none" ? Old : Current,bytes.ToArray());
        }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("foreign")]
    [InlineData("empty")]
    public async Task NormalDeleteUsesValidatedPointerBeforeSoftDelete(string pointer)
    {
        await Seed(pointer); var service = new FileStorageService(storage,new SelfHostedRepository<FileRecord>(db),db,tenant,NullLogger<FileStorageService>.Instance);
        if (pointer == "valid")
        {
            Assert.NotNull(await service.DeleteAsync(fileId)); Assert.False(await storage.ExistsAsync(tenantId,blobId));
            Assert.True((await db.FileRecords.IgnoreQueryFilters().SingleAsync()).IsDeleted);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => service.DeleteAsync(fileId));
            Assert.False((await db.FileRecords.SingleAsync()).IsDeleted); Assert.True(await storage.ExistsAsync(tenantId,blobId));
        }
        Assert.True(await storage.ExistsAsync(tenantId,fileId));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("none")]
    [InlineData("foreign")]
    [InlineData("empty")]
    [InlineData("deleted")]
    public async Task GraphBinaryReadRequiresLiveFileAndExactTenantPointer(string pointer)
    {
        await Seed(pointer);
        if (pointer == "deleted") { (await db.FileRecords.SingleAsync()).IsDeleted = true; await db.SaveChangesAsync(); }
        var importer = new PortableImportService(db,tenant,storage,NullLogger<PortableImportService>.Instance);
        var exporter = new PortableExportService(db,tenant,storage,Options.Create(new SelfHostedOptions()),NullLogger<PortableExportService>.Instance);
        var graph = new SelfHostedGraphStore(db,tenant,importer,exporter,storage);
        if (pointer is "foreign" or "empty") { await Assert.ThrowsAsync<InvalidDataException>(() => graph.OpenFileAsync(fileId,default)); return; }
        if (pointer == "deleted") { await Assert.ThrowsAsync<FileNotFoundException>(() => graph.OpenFileAsync(fileId,default)); return; }
        await using var stream = await graph.OpenFileAsync(fileId,default); using var bytes = new MemoryStream(); await stream.CopyToAsync(bytes);
        Assert.Equal(pointer == "none" ? Old : Current,bytes.ToArray());
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("none")]
    [InlineData("foreign")]
    [InlineData("empty")]
    public async Task CommentLastLinkDeleteUsesExactPointerAndRejectsCorruptionBeforeSaving(string pointer)
    {
        await Seed(pointer); var knowledgeId = (await db.KnowledgeItems.SingleAsync()).Id;
        var comment = new KnowledgeComment { TenantId = tenantId, KnowledgeId = knowledgeId, Body = "Comment" };
        db.Comments.Add(comment); var attachment = await db.FileAttachments.SingleAsync(); attachment.CommentId = comment.Id;
        await db.SaveChangesAsync();
        var service = new CommentService(db,tenant,NullLogger<CommentService>.Instance,storage);
        if (pointer is "foreign" or "empty")
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => service.DeleteCommentAsync(comment.Id,true,default));
            db.ChangeTracker.Clear(); Assert.False((await db.FileRecords.SingleAsync()).IsDeleted);
            Assert.False((await db.Comments.SingleAsync()).IsDeleted); Assert.Single(await db.FileAttachments.ToListAsync());
            Assert.True(await storage.ExistsAsync(tenantId,blobId)); Assert.True(await storage.ExistsAsync(tenantId,fileId));
            return;
        }
        var result = await service.DeleteCommentAsync(comment.Id,true,default); Assert.NotNull(result); Assert.Equal(1,result.FilesDeleted);
        Assert.False(await storage.ExistsAsync(tenantId,pointer == "none" ? fileId : blobId));
        if (pointer == "valid") Assert.True(await storage.ExistsAsync(tenantId,fileId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommentDeletionPreservesBytesReferencedByAnotherAttachmentOrRetainedHistory(bool history)
    {
        await Seed("valid"); var knowledgeId = (await db.KnowledgeItems.SingleAsync()).Id;
        var comment = new KnowledgeComment { TenantId = tenantId, KnowledgeId = knowledgeId, Body = "Comment" };
        db.Comments.Add(comment); (await db.FileAttachments.SingleAsync()).CommentId = comment.Id;
        if (history)
        {
            (await db.FileRecords.SingleAsync()).IsCurrentVersion = false;
            var successor = new FileRecord { TenantId = tenantId, FileName = "Version 2", FileVersion = 2, RootFileRecordId = fileId, PreviousFileRecordId = fileId };
            db.FileRecords.Add(successor); db.FileAttachments.Add(new() { TenantId = tenantId, KnowledgeId = knowledgeId, FileRecordId = successor.Id });
        }
        else db.FileAttachments.Add(new() { TenantId = tenantId, KnowledgeId = knowledgeId, FileRecordId = fileId });
        await db.SaveChangesAsync(); var result = await new CommentService(db,tenant,NullLogger<CommentService>.Instance,storage).DeleteCommentAsync(comment.Id,true,default);
        Assert.NotNull(result); Assert.Equal(1,result.FilesPreserved); Assert.Equal(0,result.FilesDeleted);
        Assert.True(await db.FileRecords.AnyAsync(f => f.Id == fileId)); Assert.True(await storage.ExistsAsync(tenantId,blobId));
        Assert.True(await storage.ExistsAsync(tenantId,fileId));
    }

    private sealed class PushHandler : HttpMessageHandler
    {
        public byte[]? Bytes { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{\"files\":[]}}",Encoding.UTF8,"application/json") };
            Bytes = await Assert.Single(Assert.IsType<MultipartFormDataContent>(request.Content)).ReadAsByteArrayAsync(ct);
            return new(HttpStatusCode.OK);
        }
    }
}
