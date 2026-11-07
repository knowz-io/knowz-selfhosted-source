using System.Net;
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

public sealed class EncryptedDeleteRevocationPostgresTests
{
    [PostgresTheory]
    [InlineData("knowledge", "grant")]
    [InlineData("comment", "grant")]
    [InlineData("file", "grant")]
    [InlineData("attachment", "grant")]
    [InlineData("knowledge", "user")]
    [InlineData("comment", "user")]
    [InlineData("file", "user")]
    [InlineData("attachment", "user")]
    [InlineData("knowledge", "tenant")]
    [InlineData("comment", "tenant")]
    [InlineData("file", "tenant")]
    [InlineData("attachment", "tenant")]
    [InlineData("comment", "owner")]
    [InlineData("file", "owner")]
    [InlineData("attachment", "owner")]
    [InlineData("protected-file", "grant")]
    [InlineData("protected-file", "user")]
    [InlineData("protected-file", "tenant")]
    [InlineData("protected-file", "owner")]
    public async Task HttpDeleteRollsBackIfAuthorityIsRevokedAfterSaveBeforeCommit(string kind, string revoke)
    {
        var configured = Environment.GetEnvironmentVariable("KNOWZ_TEST_POSTGRES"); Assert.False(string.IsNullOrWhiteSpace(configured));
        var connection = new NpgsqlConnectionStringBuilder(configured); Assert.True(connection.Host is "localhost" or "127.0.0.1" && connection.Port == 18460);
        connection.Database = "postgres"; connection.Pooling = false;
        await using var admin = new NpgsqlConnection(connection.ConnectionString); await admin.OpenAsync();
        var database = "kc_delete_revoke_" + Guid.NewGuid().ToString("N");
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
        connection.Database = database;
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(Guid.NewGuid());
        var interceptor = new DeleteRevocationInterceptor(connection.ConnectionString, revoke);
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>().UseNpgsql(connection.ConnectionString).AddInterceptors(interceptor).Options;
        var directory = Path.Combine(Path.GetTempPath(), database);
        var storage = new LocalFileStorageProvider(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Storage:Local:RootPath"] = directory }).Build(), NullLogger<LocalFileStorageProvider>.Instance);
        try
        {
            await using var db = new SelfHostedDbContext(options, tenant); await db.Database.MigrateAsync();
            db.Tenants.Add(new Tenant { Id = tenant.TenantId, Name = "Delete revocation fixture", Slug = database }); await db.SaveChangesAsync();
            var graph = new SelfHostedGraphStore(db, tenant, new(db, tenant, storage, NullLogger<PortableImportService>.Instance), new(db, tenant, storage, Options.Create(new SelfHostedOptions()), NullLogger<PortableExportService>.Instance), storage);
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web); json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            var source = JsonSerializer.Deserialize<PortableExportPackage>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portable-graph-v1.json")), json)!;
            if (kind == "protected-file")
            {
                var original = source.SealedFiles!.Single();
                source.Data.FileRecords.Add(new() { Id = original.Id, OriginalFileId = original.OriginalFileId, FileName = "encrypted-file", ContentType = "application/octet-stream", SizeBytes = original.SizeBytes, BinaryContentBase64 = original.BinaryContentBase64, EncryptedFieldsJson = original.EncryptedFieldsJson, Attachments = [new() { KnowledgeId = source.SealedItems!.Single().Id }] });
                source.SealedFiles.Clear(); source.PayloadKeyReferences!.RemoveAll(r => r.OwnerKind == "file" && r.Role == "binary"); Assert.Null(EncryptedGraphProfile.Validate(source));
            }
            Assert.True((await graph.ImportAsync(source, null, "trusted:delete-revocation", ImportConflictStrategy.Skip, null, default)).Success);
            var lifecycle = new SelfHostedSealedStore(db, tenant, NullLogger<SelfHostedSealedStore>.Instance);
            var user = new User { TenantId = tenant.TenantId, Username = "revocation-fixture", IsActive = true }; db.Users.Add(user);
            var vault = (await db.Vaults.SingleAsync()).Id;
            db.UserPermissions.Add(new UserPermissions { TenantId = tenant.TenantId, UserId = user.Id, HasAllVaultsAccess = false });
            var grant = new UserVaultAccess { TenantId = tenant.TenantId, UserId = user.Id, VaultId = vault, CanRead = true, CanWrite = true, CanDelete = true }; db.UserVaultAccess.Add(grant); await db.SaveChangesAsync();
            var item = await db.ShSealedKnowledge.AsNoTracking().SingleAsync(); var comment = await db.ShSealedComments.AsNoTracking().FirstAsync(); var protectedRecord = kind == "protected-file" ? await db.FileRecords.AsNoTracking().SingleAsync() : null;
            var file = protectedRecord == null ? await db.ShSealedFiles.AsNoTracking().SingleAsync() : new ShSealedFile { Id = protectedRecord.Id, BlobStorageId = await db.ShFileObjectReferences.Where(f => f.FileRecordId == protectedRecord.Id).Select(f => (Guid?)f.BlobStorageId).SingleOrDefaultAsync() ?? protectedRecord.Id }; var attachment = await db.ShSealedAttachments.AsNoTracking().FirstAsync();
            var references = await db.ShPayloadKeyReferences.CountAsync();
            var id = kind switch { "knowledge" => item.KnowledgeId, "comment" => comment.Id, "file" or "protected-file" => file.Id, _ => attachment.Id };
            var revision = kind switch { "knowledge" => (await lifecycle.GetAsync(id, default))!.Revision, "comment" => SelfHostedSealedStore.PayloadRevision((await lifecycle.GetCommentAsync(id, default))!.Envelope), "protected-file" => SelfHostedSealedStore.PayloadRevision((await SelfHostedSealedFiles.GetProtectedFileAsync(db, file.Id, default))!), "file" => SelfHostedSealedStore.PayloadRevision(JsonSerializer.Deserialize<SealedFileRecord>(JsonSerializer.Serialize(file))!), _ => SelfHostedSealedStore.PayloadRevision(JsonSerializer.Deserialize<SealedAttachmentRecord>(JsonSerializer.Serialize(attachment))!) };
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Services.AddSingleton(db); builder.Services.AddSingleton(lifecycle); builder.Services.AddSingleton(graph); builder.Services.AddSingleton(new SelfHostedSealedFiles(db, tenant, lifecycle, storage)); builder.Services.AddSingleton<IVaultAccessService>(new VaultAccessService(db));
            await using var app = builder.Build(); app.Use(async (http, next) => { http.User = new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, user.Id.ToString()), new("tenantId", tenant.TenantId.ToString()), new(ClaimTypes.Role, "User")], "synthetic")); await next(); }); app.MapEncryptedVaultEndpoints(); await app.StartAsync(); using var client = app.GetTestClient();
            interceptor.Target = revoke == "grant" ? grant.Id : revoke == "user" ? user.Id : revoke == "owner" ? item.KnowledgeId : tenant.TenantId; interceptor.Armed = true;
            var path = kind switch { "knowledge" => "items", "comment" => "comments", "file" or "protected-file" => "files", _ => "attachments" };
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/encrypted/{path}/{id}?vaultId={vault}"); request.Headers.TryAddWithoutValidation("If-Match", revision);
            using var response = await client.SendAsync(request); Assert.True(interceptor.Fired); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await using var fresh = new SelfHostedDbContext(new DbContextOptionsBuilder<SelfHostedDbContext>().UseNpgsql(connection.ConnectionString).Options, tenant);
            Assert.False((await fresh.ShSealedKnowledge.IgnoreQueryFilters().SingleAsync()).IsDeleted); Assert.Equal(revoke == "owner", (await fresh.KnowledgeItems.IgnoreQueryFilters().SingleAsync(k => k.Id == item.KnowledgeId)).IsDeleted);
            Assert.False((await fresh.ShSealedComments.IgnoreQueryFilters().SingleAsync(c => c.Id == comment.Id)).IsDeleted); if (protectedRecord == null) Assert.False((await fresh.ShSealedFiles.IgnoreQueryFilters().SingleAsync()).IsDeleted);
            else Assert.False((await fresh.FileRecords.IgnoreQueryFilters().SingleAsync()).IsDeleted); Assert.False((await fresh.ShSealedAttachments.IgnoreQueryFilters().SingleAsync(a => a.Id == attachment.Id)).IsDeleted);
            Assert.Equal(references, await fresh.ShPayloadKeyReferences.CountAsync()); Assert.Equal(item.Ciphertext, (await fresh.ShSealedKnowledge.IgnoreQueryFilters().SingleAsync()).Ciphertext); Assert.True(await storage.ExistsAsync(tenant.TenantId, file.BlobStorageId!.Value));
            if (revoke == "grant") Assert.False((await fresh.UserVaultAccess.SingleAsync()).CanDelete);
            if (revoke == "user") Assert.False((await fresh.Users.SingleAsync()).IsActive);
            if (revoke == "tenant") Assert.False((await fresh.Tenants.SingleAsync()).IsActive);
        }
        finally { await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin); await drop.ExecuteNonQueryAsync(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class DeleteRevocationInterceptor(string connectionString, string revoke) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Fired { get; private set; }
        public Guid Target { get; set; }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
        {
            if (!Armed) return result; Armed = false;
            await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct);
            var statement = revoke switch { "grant" => "UPDATE \"UserVaultAccess\" SET \"CanDelete\" = FALSE WHERE \"Id\" = @id", "user" => "UPDATE \"Users\" SET \"IsActive\" = FALSE WHERE \"Id\" = @id", "owner" => "UPDATE \"KnowledgeItems\" SET \"IsDeleted\" = TRUE WHERE \"Id\" = @id", _ => "UPDATE \"Tenants\" SET \"IsActive\" = FALSE WHERE \"Id\" = @id" };
            await using var command = new NpgsqlCommand(statement, connection); command.Parameters.AddWithValue("id", Target); Assert.Equal(1, await command.ExecuteNonQueryAsync(ct)); Fired = true; return result;
        }
    }
}
