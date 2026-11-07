using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using System.Security.Claims;
using System.Text.Json;
using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.API.Endpoints;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed class ProtectedFileContinuityPostgresTests
{
    [PostgresTheory]
    [InlineData("binary", "success")]
    [InlineData("binary", "retired")]
    [InlineData("metadata", "success")]
    [InlineData("binary", "grant")]
    [InlineData("binary", "user")]
    [InlineData("binary", "tenant")]
    [InlineData("binary", "membership")]
    [InlineData("binary", "role")]
    [InlineData("binary", "owner")]
    [InlineData("metadata", "grant")]
    [InlineData("metadata", "user")]
    [InlineData("metadata", "tenant")]
    [InlineData("metadata", "membership")]
    [InlineData("metadata", "role")]
    [InlineData("metadata", "owner")]
    [InlineData("binary", "late-inactive")]
    [InlineData("metadata", "late-inactive")]
    [InlineData("binary", "storage-grant")]
    [InlineData("binary", "storage-revision")]
    public async Task ProtectedFileHttpMutationRetainsHistoryAndRechecksAuthorityAtCommit(string kind, string mode)
    {
        var configured = Environment.GetEnvironmentVariable("KNOWZ_TEST_POSTGRES"); Assert.False(string.IsNullOrWhiteSpace(configured));
        var connection = new NpgsqlConnectionStringBuilder(configured); Assert.True(connection.Host is "localhost" or "127.0.0.1" && connection.Port == 18460);
        connection.Database = "postgres"; connection.Pooling = false;
        await using var admin = new NpgsqlConnection(connection.ConnectionString); await admin.OpenAsync();
        var database = "kc_file_continuity_" + Guid.NewGuid().ToString("N");
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
        connection.Database = database;
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(Guid.NewGuid());
        var actor = Substitute.For<IPortableActorProvider>(); actor.ActorId.Returns(Guid.NewGuid());
        var fault = new ProtectedFileFault(connection.ConnectionString, mode);
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>().UseNpgsql(connection.ConnectionString, o => o.EnableRetryOnFailure()).AddInterceptors(fault).Options;
        var directory = Path.Combine(Path.GetTempPath(), database);
        var local = new LocalFileStorageProvider(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Storage:Local:RootPath"] = directory }).Build(), NullLogger<LocalFileStorageProvider>.Instance);
        var storage = new AfterUpload(local, fault);
        try
        {
            await using var db = new SelfHostedDbContext(options, tenant, actor); await db.Database.MigrateAsync();
            db.Tenants.Add(new Tenant { Id = tenant.TenantId, Name = "Protected continuity fixture", Slug = database }); await db.SaveChangesAsync();
            var graph = new SelfHostedGraphStore(db, tenant, new(db, tenant, storage, NullLogger<PortableImportService>.Instance), new(db, tenant, storage, Options.Create(new SelfHostedOptions()), NullLogger<PortableExportService>.Instance), storage);
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web); json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            var source = JsonSerializer.Deserialize<PortableExportPackage>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portable-graph-v1.json")), json)!;
            var original = source.SealedFiles!.Single();
            source.Data.FileRecords.Add(new() { Id = original.Id, OriginalFileId = original.OriginalFileId, FileName = "encrypted-file", ContentType = "application/octet-stream", SizeBytes = 3, BinaryContentBase64 = "AQID", EncryptedFieldsJson = original.EncryptedFieldsJson, Attachments = [new() { KnowledgeId = source.SealedItems!.Single().Id }] });
            source.SealedFiles.Clear(); source.PayloadKeyReferences!.RemoveAll(r => r.OwnerKind == "file" && r.Role == "binary"); Assert.Null(EncryptedGraphProfile.Validate(source));
            Assert.True((await db.Database.CreateExecutionStrategy().ExecuteAsync(() => graph.ImportAsync(source, null, "trusted:protected-continuity", ImportConflictStrategy.Skip, null, default))).Success);
            var lifecycle = new SelfHostedSealedStore(db, tenant, NullLogger<SelfHostedSealedStore>.Instance);
            var user = new User { Id = actor.ActorId!.Value, TenantId = tenant.TenantId, Username = "continuity-fixture", IsActive = true, Role = mode == "role" ? Knowz.Core.Enums.UserRole.Admin : Knowz.Core.Enums.UserRole.User }; db.Users.Add(user);
            var membership = new UserTenantMembership { TenantId = tenant.TenantId, UserId = user.Id, IsActive = true, Role = mode == "role" ? Knowz.Core.Enums.UserRole.Admin : Knowz.Core.Enums.UserRole.User }; db.UserTenantMemberships.Add(membership);
            var vault = (await db.Vaults.SingleAsync()).Id;
            db.UserPermissions.Add(new UserPermissions { TenantId = tenant.TenantId, UserId = user.Id, HasAllVaultsAccess = false });
            var grant = new UserVaultAccess { TenantId = tenant.TenantId, UserId = user.Id, VaultId = vault, CanRead = true, CanWrite = true }; db.UserVaultAccess.Add(grant); await db.SaveChangesAsync();
            var file = await db.FileRecords.AsNoTracking().SingleAsync(); var owner = await db.ShSealedKnowledge.AsNoTracking().SingleAsync();
            var projection = (await SelfHostedSealedFiles.GetProtectedFileAsync(db, file.Id, default))!;
            var revision = SelfHostedSealedStore.PayloadRevision(projection); var originalFields = projection.EncryptedFieldsJson;
            var refs = (await db.ShPayloadKeyReferences.AsNoTracking().Where(r => r.OwnerKind == "file" && r.OwnerId == file.Id).ToListAsync()).Select(PortablePublicProjection.Reference).ToList();
            var originalRefs = JsonSerializer.Serialize(refs, json); var count = await db.ShPayloadKeyReferences.CountAsync();
            var objectId = await db.ShFileObjectReferences.Where(r => r.FileRecordId == file.Id).Select(r => r.BlobStorageId).SingleAsync();
            if (mode == "retired") { var key = await db.ShCryptoKeyIdentities.SingleAsync(); key.SourceLifecycle = "retired"; key.Lifecycle = "decrypt-only"; await db.SaveChangesAsync(); }
            if (kind == "metadata")
            {
                var catalog = (await lifecycle.GetCatalogAsync(vault, default))!; var key = Assert.Single(catalog.Keys);
                Assert.True((await lifecycle.SelectPrimaryAsync(vault, key.ContextId, key.Fingerprint, catalog.Revision, default, key.Generation)).Ok);
                projection.EncryptedFieldsJson = SelfHostedGraphStoreTests.EncryptProtectedFileName("native updated file name", key.PublicKey);
            }
            var replacement = new byte[] { 8, 7, 6, 5 };
            if (kind == "binary") { projection.SizeBytes = replacement.Length; projection.BinarySha256 = ArchivePeerProof.Fingerprint(replacement); }
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Services.AddSingleton(db); builder.Services.AddSingleton(lifecycle); builder.Services.AddSingleton(graph); builder.Services.AddSingleton(new SelfHostedSealedFiles(db, tenant, lifecycle, storage)); builder.Services.AddSingleton<IVaultAccessService>(new VaultAccessService(db));
            await using var app = builder.Build(); app.Use(async (http, next) => { http.User = new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, user.Id.ToString()), new("tenantId", tenant.TenantId.ToString()), new(ClaimTypes.Role, "User")], "synthetic")); await next(); }); app.MapEncryptedVaultEndpoints(); await app.StartAsync(); using var client = app.GetTestClient();
            fault.Target = mode.Contains("grant") ? grant.Id : mode == "user" ? user.Id : mode is "membership" or "role" ? membership.Id : mode is "owner" or "late-inactive" ? owner.KnowledgeId : mode == "storage-revision" ? file.Id : tenant.TenantId;
            fault.FileId = file.Id; db.ChangeTracker.Clear(); fault.Armed = mode is not ("success" or "retired");
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/encrypted/files/{file.Id}"); request.Headers.TryAddWithoutValidation("If-Match", revision);
            if (kind == "metadata") request.Content = JsonContent.Create(new ProtectedFileWrite { VaultId = vault, Record = projection, PayloadKeyReferences = refs }, options: json);
            else { var form = new MultipartFormDataContent(); form.Add(new StringContent(vault.ToString()), "vaultId"); form.Add(new StringContent(JsonSerializer.Serialize(projection, json)), "record"); form.Add(new StringContent(JsonSerializer.Serialize(refs, json)), "payloadKeyReferences"); form.Add(new ByteArrayContent(replacement), "binary", "ordinary.bin"); request.Content = form; }
            using var response = await client.SendAsync(request);
            var success = mode is "success" or "retired" or "role";
            if (mode is not ("success" or "retired")) Assert.True(fault.Fired);
            Assert.True(response.StatusCode == (success ? HttpStatusCode.OK : mode is "storage-revision" or "late-inactive" ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden), $"{kind}/{mode}: HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            await using var fresh = new SelfHostedDbContext(new DbContextOptionsBuilder<SelfHostedDbContext>().UseNpgsql(connection.ConnectionString).Options, tenant, actor);
            var rows = await fresh.FileRecords.IgnoreQueryFilters().AsNoTracking().ToListAsync(); Assert.Equal(success && kind == "binary" ? 2 : 1, rows.Count);
            var retained = rows.Single(r => r.Id == file.Id); Assert.Equal(success && kind == "metadata" ? projection.EncryptedFieldsJson : originalFields, retained.EncryptedFieldsJson);
            Assert.Equal(!(success && kind == "binary"), retained.IsCurrentVersion);
            if (mode == "late-inactive") Assert.Null((await fresh.KnowledgeItems.SingleAsync(k => k.Id == owner.KnowledgeId)).PortableRecoveryState);
            var retainedRefs = (await fresh.ShPayloadKeyReferences.AsNoTracking().Where(r => r.OwnerKind == "file" && r.OwnerId == file.Id).ToListAsync()).Select(PortablePublicProjection.Reference).ToList(); Assert.Equal(originalRefs, JsonSerializer.Serialize(retainedRefs, json));
            var (oldStream, _, _) = await local.DownloadAsync(tenant.TenantId, objectId); await using (oldStream) { using var output = new MemoryStream(); await oldStream.CopyToAsync(output); Assert.Equal(new byte[] { 1, 2, 3 }, output.ToArray()); }
            Assert.Equal(success && kind == "binary" ? count + refs.Count : count, await fresh.ShPayloadKeyReferences.CountAsync());
            if (success && kind == "binary")
            {
                var next = rows.Single(r => r.IsCurrentVersion); Assert.Equal(file.Id, next.PreviousFileRecordId); Assert.Equal(2, next.FileVersion);
                var nextRefs = (await fresh.ShPayloadKeyReferences.Where(r => r.OwnerKind == "file" && r.OwnerId == next.Id).ToListAsync()).Select(PortablePublicProjection.Reference).ToList(); foreach (var r in nextRefs) r.OwnerId = file.Id; Assert.Equal(originalRefs, JsonSerializer.Serialize(nextRefs, json));
                var nextObject = await fresh.ShFileObjectReferences.Where(r => r.FileRecordId == next.Id).Select(r => r.BlobStorageId).SingleAsync(); var (stream, _, _) = await local.DownloadAsync(tenant.TenantId, nextObject); await using (stream) { using var output = new MemoryStream(); await stream.CopyToAsync(output); Assert.Equal(replacement, output.ToArray()); }
            }
            else if (kind == "binary") { var stage = await fresh.ShSealedFileStages.SingleAsync(); Assert.Equal("cancelled", stage.Status); Assert.Null(stage.BlobStorageId); Assert.NotNull(storage.UploadedObject); Assert.False(await local.ExistsAsync(tenant.TenantId, storage.UploadedObject!.Value)); }
            if (success)
            {
                var freshGraph = new SelfHostedGraphStore(fresh, tenant, new(fresh, tenant, local, NullLogger<PortableImportService>.Instance), new(fresh, tenant, local, Options.Create(new SelfHostedOptions()), NullLogger<PortableExportService>.Instance), local);
                var exported = await new SelfHostedArchiveExportService(fresh, tenant, freshGraph, new EphemeralDataProtectionProvider()).ExportAsync(new() { Type = ExportScopeType.Vault, VaultIds = [vault] }, false, default);
                await using (exported.Stream)
                {
                    using var archive = await KnowzArchiveReader.OpenAsync(exported.Stream, new() { ExpectedSignerFingerprint = exported.Receipt.SignerFingerprint });
                    var package = PortableGraphPackages.Combine(archive.Packages); Assert.Null(EncryptedGraphProfile.Validate(package));
                    Assert.Equal(rows.Count, package.Data.FileRecords.Count);
                    foreach (var exportedFile in package.Data.FileRecords)
                    {
                        var persisted = rows.Single(r => r.Id == exportedFile.Id); Assert.Equal(persisted.EncryptedFieldsJson, exportedFile.EncryptedFieldsJson);
                        Assert.Equal(persisted.OriginalFileId, exportedFile.OriginalFileId); Assert.Equal(persisted.PreviousFileRecordId, exportedFile.PreviousFileRecordId);
                        var exportedRefs = JsonSerializer.Deserialize<List<PortablePayloadKeyReference>>(JsonSerializer.Serialize(package.PayloadKeyReferences!.Where(r => r.OwnerKind == "file" && r.OwnerId == exportedFile.Id), json), json)!;
                        foreach (var r in exportedRefs) r.OwnerId = file.Id; Assert.Equal(originalRefs, JsonSerializer.Serialize(exportedRefs, json));
                        Assert.False(string.IsNullOrEmpty(SelfHostedGraphStoreTests.DecryptProtectedFileName(exportedFile.EncryptedFieldsJson!)));
                        await using var binary = await archive.OpenBinaryAsync(exportedFile.BinaryEntryPath!); using var bytes = new MemoryStream(); await binary.CopyToAsync(bytes);
                        Assert.Equal(kind == "binary" && exportedFile.IsCurrentVersion ? replacement : new byte[] { 1, 2, 3 }, bytes.ToArray());
                    }
                }
            }

        }
        finally { await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin); await drop.ExecuteNonQueryAsync(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class ProtectedFileFault(string connectionString, string mode) : SaveChangesInterceptor
    {
        public bool Armed { get; set; } public bool Fired { get; private set; } public Guid Target { get; set; } public Guid FileId { get; set; }
        public bool AfterStorage => mode.StartsWith("storage-");
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
        {
            if (Armed && !AfterStorage && eventData.Context!.Database.CurrentTransaction != null && eventData.Context.ChangeTracker.Entries<FileRecord>().Any(e => e.Entity.Id == FileId))
            {
                if (mode == "late-inactive")
                {
                    Armed = false; var db = (SelfHostedDbContext)eventData.Context;
                    Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"KnowledgeItems\" SET \"PortableRecoveryState\" = 'dependency-bound', \"CreatedByUserId\" = {db.PortableActorId} WHERE \"Id\" = {Target}", ct));
                    Assert.True(await db.KnowledgeItems.AnyAsync(k => k.Id == Target, ct)); Fired = true;
                }
                else await InjectAsync(ct);
            }
            return result;
        }
        public async Task InjectAsync(CancellationToken ct)
        {
            if (!Armed) return; Armed = false;
            await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct);
            var statement = mode switch { "grant" or "storage-grant" => "UPDATE \"UserVaultAccess\" SET \"CanWrite\" = FALSE WHERE \"Id\" = @id", "user" => "UPDATE \"Users\" SET \"IsActive\" = FALSE WHERE \"Id\" = @id", "owner" => "UPDATE \"KnowledgeItems\" SET \"IsDeleted\" = TRUE WHERE \"Id\" = @id", "membership" => "UPDATE \"UserTenantMemberships\" SET \"IsActive\" = FALSE WHERE \"Id\" = @id", "role" => "UPDATE \"UserTenantMemberships\" SET \"Role\" = 0 WHERE \"Id\" = @id", "storage-revision" => "UPDATE \"FileRecords\" SET \"UpdatedAt\" = NOW() WHERE \"Id\" = @id", _ => "UPDATE \"Tenants\" SET \"IsActive\" = FALSE WHERE \"Id\" = @id" };
            await using var command = new NpgsqlCommand(statement, connection); command.Parameters.AddWithValue("id", Target); Assert.Equal(1, await command.ExecuteNonQueryAsync(ct)); Fired = true;
        }
    }
    private sealed class AfterUpload(IFileStorageProvider inner, ProtectedFileFault fault) : IFileStorageProvider
    {
        public Guid? UploadedObject { get; private set; }
        public async Task<string> UploadAsync(Guid tenant, Guid id, Stream input, string type, CancellationToken ct = default) { var result = await inner.UploadAsync(tenant,id,input,type,ct); if (fault.Armed) { UploadedObject = id; if (fault.AfterStorage) await fault.InjectAsync(ct); } return result; }
        public Task<(Stream stream,string contentType,string fileName)> DownloadAsync(Guid tenant,Guid id,CancellationToken ct=default)=>inner.DownloadAsync(tenant,id,ct);
        public Task<bool> DeleteAsync(Guid tenant,Guid id,CancellationToken ct=default)=>inner.DeleteAsync(tenant,id,ct);
        public Task<bool> ExistsAsync(Guid tenant,Guid id,CancellationToken ct=default)=>inner.ExistsAsync(tenant,id,ct);
        public Task<string> GenerateDownloadUrlAsync(Guid tenant,Guid id,string name,TimeSpan? expiry=null,CancellationToken ct=default)=>inner.GenerateDownloadUrlAsync(tenant,id,name,expiry,ct);
        public Task<bool> IsAvailableAsync(CancellationToken ct=default)=>inner.IsAvailableAsync(ct);
    }
}
