using System.Text.Json;
using Knowz.Core.Configuration;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed class EncryptedLogicalDeletionPostgresTests
{
    [PostgresTheory]
    [InlineData("knowledge", false)]
    [InlineData("comment", false)]
    [InlineData("file", false)]
    [InlineData("attachment", false)]
    [InlineData("knowledge", true)]
    [InlineData("comment", true)]
    [InlineData("file", true)]
    [InlineData("attachment", true)]
    public async Task LogicalDeletionRetainsEncryptedGraphAndBytesAcrossFreshUpgradeAndRestart(string kind, bool upgrade)
    {
        var configured = Environment.GetEnvironmentVariable("KNOWZ_TEST_POSTGRES"); Assert.False(string.IsNullOrWhiteSpace(configured));
        var builder = new NpgsqlConnectionStringBuilder(configured); Assert.True(builder.Host is "localhost" or "127.0.0.1" && builder.Port == 18460);
        builder.Database = "postgres"; builder.Pooling = false;
        await using var admin = new NpgsqlConnection(builder.ConnectionString); await admin.OpenAsync();
        var database = "kc_encrypted_delete_" + Guid.NewGuid().ToString("N");
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
        builder.Database = database;
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>().UseNpgsql(builder.ConnectionString).Options;
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(Guid.NewGuid());
        var directory = Path.Combine(Path.GetTempPath(), database);
        var storage = new LocalFileStorageProvider(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Storage:Local:RootPath"] = directory }).Build(), NullLogger<LocalFileStorageProvider>.Instance);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web); json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var source = JsonSerializer.Deserialize<PortableExportPackage>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portable-graph-v1.json")), json)!;
        var expectedBytes = Convert.FromBase64String(source.SealedFiles!.Single().BinaryContentBase64!);
        Guid itemId, commentId, fileId, attachmentId, blobId; string body, commentBody, snapshot; int references;
        try
        {
            await using (var db = new SelfHostedDbContext(options, tenant))
            {
                if (upgrade)
                {
                    await db.GetService<IMigrator>().MigrateAsync("20261006171704_AddInactiveRecoveryAndPeerReceiptResume");
                    await using var connection = new NpgsqlConnection(builder.ConnectionString); await connection.OpenAsync();
                    await using var seed = new NpgsqlCommand("INSERT INTO \"Vaults\" (\"Id\",\"TenantId\",\"Name\",\"CreatedAt\",\"UpdatedAt\",\"IsDeleted\",\"IsDefault\") VALUES (@id,@tenant,'Pre-upgrade plain vault',now(),now(),FALSE,FALSE)", connection);
                    seed.Parameters.AddWithValue("id", Guid.NewGuid()); seed.Parameters.AddWithValue("tenant", tenant.TenantId); await seed.ExecuteNonQueryAsync();
                }
                await db.Database.MigrateAsync(); await db.Database.MigrateAsync(); Assert.Empty(await db.Database.GetPendingMigrationsAsync());
                Assert.False(db.Database.HasPendingModelChanges());
                var imported = await Graph(db).ImportAsync(source, null, "trusted:deletion-pg", ImportConflictStrategy.Skip, null, default); Assert.True(imported.Success, imported.Error);
                var item = await db.ShSealedKnowledge.AsNoTracking().SingleAsync(); var comment = await db.ShSealedComments.AsNoTracking().FirstAsync();
                var file = await db.ShSealedFiles.AsNoTracking().SingleAsync(); var attachment = await db.ShSealedAttachments.AsNoTracking().FirstAsync();
                itemId = item.KnowledgeId; commentId = comment.Id; fileId = file.Id; attachmentId = attachment.Id; blobId = file.BlobStorageId!.Value;
                body = item.Ciphertext; commentBody = comment.Ciphertext; snapshot = (await db.ShSealedVersions.SingleAsync()).EnvelopeMetadata;
                references = await db.ShPayloadKeyReferences.CountAsync();
                var lifecycle = new SelfHostedSealedStore(db, tenant, NullLogger<SelfHostedSealedStore>.Instance); var files = new SelfHostedSealedFiles(db, tenant, lifecycle, storage);
                var stale = await Delete("stale"); Assert.False(stale.Ok); Assert.Equal(409, stale.StatusCode);
                db.ChangeTracker.Clear(); var result = await Delete(null); Assert.True(result.Ok, result.Error);
                async Task<SealedStoreResult> Delete(string? staleRevision)
                {
                    if (kind == "knowledge") return await lifecycle.DeleteAsync(itemId, default, staleRevision ?? (await lifecycle.GetAsync(itemId, default))!.Revision);
                    if (kind == "comment") return await lifecycle.DeleteCommentAsync(commentId, default, staleRevision ?? SelfHostedSealedStore.PayloadRevision((await lifecycle.GetCommentAsync(commentId, default))!.Envelope));
                    if (kind == "file") return await files.DeleteFileAsync(fileId, staleRevision ?? SelfHostedSealedStore.PayloadRevision(JsonSerializer.Deserialize<SealedFileRecord>(JsonSerializer.Serialize(file))!), default);
                    return await files.DeleteAttachmentAsync(attachmentId, staleRevision ?? SelfHostedSealedStore.PayloadRevision(JsonSerializer.Deserialize<SealedAttachmentRecord>(JsonSerializer.Serialize(attachment))!), default);
                }
            }
            await using (var restarted = new SelfHostedDbContext(options, tenant))
            {
                Assert.Equal(body, (await restarted.ShSealedKnowledge.IgnoreQueryFilters().SingleAsync()).Ciphertext);
                Assert.Equal(commentBody, (await restarted.ShSealedComments.IgnoreQueryFilters().SingleAsync(c => c.Id == commentId)).Ciphertext);
                Assert.Equal(snapshot, (await restarted.ShSealedVersions.IgnoreQueryFilters().SingleAsync()).EnvelopeMetadata);
                Assert.Equal(references, await restarted.ShPayloadKeyReferences.IgnoreQueryFilters().CountAsync());
                Assert.True(await restarted.ShCryptoKeyIdentities.AnyAsync());
                if (kind == "knowledge") Assert.False(await restarted.ShSealedKnowledge.AnyAsync(s => s.KnowledgeId == itemId));
                if (kind is "knowledge" or "comment") Assert.False(await restarted.ShSealedComments.AnyAsync(c => c.Id == commentId));
                if (kind == "file") Assert.False(await restarted.ShSealedFiles.AnyAsync(f => f.Id == fileId));
                if (kind is "knowledge" or "file" or "attachment") Assert.False(await restarted.ShSealedAttachments.AnyAsync(a => a.Id == attachmentId));
                await using var stored = (await storage.DownloadAsync(tenant.TenantId, blobId, default)).stream; using var bytes = new MemoryStream(); await stored.CopyToAsync(bytes); Assert.Equal(expectedBytes, bytes.ToArray());
                await new SelfHostedPortabilityStageReconciler(restarted, storage, NullLogger<SelfHostedPortabilityStageReconciler>.Instance).ReconcileAsync(DateTime.UtcNow.AddHours(2), default);
                Assert.True(await storage.ExistsAsync(tenant.TenantId, blobId));
                if (upgrade) Assert.True(await restarted.Vaults.AnyAsync(v => v.Name == "Pre-upgrade plain vault"));
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin); await drop.ExecuteNonQueryAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        SelfHostedGraphStore Graph(SelfHostedDbContext db) => new(db, tenant, new(db, tenant, storage, NullLogger<PortableImportService>.Instance), new(db, tenant, storage, Options.Create(new SelfHostedOptions()), NullLogger<PortableExportService>.Instance), storage);
    }
}
