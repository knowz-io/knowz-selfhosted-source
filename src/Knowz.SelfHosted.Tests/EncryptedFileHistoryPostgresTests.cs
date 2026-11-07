using System.Text.Json;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Knowz.Core.Configuration;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed class EncryptedFileHistoryPostgresTests
{
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RelationalReplacementKeepsHistoryAcrossRestartAndRejectsConcurrentSecondHead(bool race)
    {
        var connection = Environment.GetEnvironmentVariable("KNOWZ_TEST_POSTGRES"); Assert.False(string.IsNullOrEmpty(connection));
        var database = "kc_file_history_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection) { Database = "postgres", Pooling = false }.ConnectionString); await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(Guid.NewGuid());
        var lostCommit = new FileCommitResponseLost();
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>().UseNpgsql(new NpgsqlConnectionStringBuilder(connection) { Database = database, Pooling = false }.ConnectionString, pg => pg.EnableRetryOnFailure()).AddInterceptors(lostCommit).Options;
        var directory = Path.Combine(Path.GetTempPath(), database);
        var storage = new LocalFileStorageProvider(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Local:RootPath"] = directory }).Build(), NullLogger<LocalFileStorageProvider>.Instance);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web); json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var source = JsonSerializer.Deserialize<PortableExportPackage>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portable-graph-v1.json")), json)!;
        var expectedBytes = Convert.FromBase64String(source.SealedFiles!.Single().BinaryContentBase64!);
        try
        {
            SealedFileWrite original; string revision; Guid originalId; Guid vault;
            await using (var db = new SelfHostedDbContext(options, tenant))
            {
                await db.Database.MigrateAsync(); var graph = Graph(db);
                Assert.True((await db.Database.CreateExecutionStrategy().ExecuteAsync(() => graph.ImportAsync(source, null, "trusted:pg-file-history", ImportConflictStrategy.Skip, null, default))).Success);
                var package = await graph.ExportAsync(new() { Type = ExportScopeType.Tenant }, false, default); vault = package.Data.Vaults.Single().Id;
                var lifecycle = new SelfHostedSealedStore(db, tenant, NullLogger<SelfHostedSealedStore>.Instance); var catalog = (await lifecycle.GetCatalogAsync(vault, default))!; var key = catalog.Keys.Single();
                Assert.True((await lifecycle.SelectPrimaryAsync(vault, key.ContextId, key.Fingerprint, catalog.Revision, default, key.Generation)).Ok);
                var record = package.SealedFiles!.Single(); record.BinaryEntryPath = null; originalId = record.Id;
                original = new() { VaultId = vault, Record = record, PayloadKeyReferences = package.PayloadKeyReferences!.Where(r => r.OwnerKind == "file").ToList() }; revision = SelfHostedSealedStore.PayloadRevision(record);
            }
            var arrivals = 0; var bothUploaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<SealedFileResult> Replace()
            {
                await using var db = new SelfHostedDbContext(options, tenant); var lifecycle = new SelfHostedSealedStore(db, tenant, NullLogger<SelfHostedSealedStore>.Instance);
                var write = JsonSerializer.Deserialize<SealedFileWrite>(JsonSerializer.Serialize(original, json), json)!;
                return await new SelfHostedSealedFiles(db, tenant, lifecycle, storage).UpdateAsync(Guid.NewGuid(), write, revision, new MemoryStream(expectedBytes), default, async ct =>
                {
                    if (race) { if (Interlocked.Increment(ref arrivals) == 2) bothUploaded.TrySetResult(); await bothUploaded.Task.WaitAsync(TimeSpan.FromSeconds(20), ct); }
                    return true;
                });
            }
            lostCommit.Armed = !race;
            var results = race ? await Task.WhenAll(Replace(), Replace()) : [await Replace()];
            var accepted = Assert.Single(results, r => r.Ok); Assert.Equal(!race, lostCommit.Injected); if (race) Assert.Single(results, r => !r.Ok && r.Status == 409);
            await using var restarted = new SelfHostedDbContext(options, tenant); var restored = await Graph(restarted).ExportAsync(new() { Type = ExportScopeType.Vault, VaultIds = [vault] }, false, default);
            Assert.Null(EncryptedGraphProfile.Validate(restored)); Assert.Equal(2, restored.SealedFiles!.Count);
            var current = Assert.Single(restored.SealedFiles, f => f.IsCurrentVersion); Assert.Equal(accepted.Write!.Record.Id, current.Id); Assert.Equal(originalId, current.PreviousFileRecordId); Assert.Equal(originalId, current.RootFileRecordId);
            Assert.All(restored.SealedFiles, f => Assert.Equal(source.SealedFiles.Single().OriginalFileId, f.OriginalFileId));
            Assert.All(await restarted.ShSealedAttachments.ToListAsync(), a => Assert.Equal(current.Id, a.FileRecordId));
            foreach (var file in restored.SealedFiles) { await using var stream = await Graph(restarted).OpenFileAsync(file.Id, default); using var bytes = new MemoryStream(); await stream.CopyToAsync(bytes); Assert.Equal(expectedBytes, bytes.ToArray()); }
            await new SelfHostedPortabilityStageReconciler(restarted, storage, NullLogger<SelfHostedPortabilityStageReconciler>.Instance).ReconcileAsync(DateTime.UtcNow.AddHours(2), default);
            Assert.Equal(2, Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length);
            var uploadLifecycle = new SelfHostedSealedStore(restarted, tenant, NullLogger<SelfHostedSealedStore>.Instance);
            var uploadService = new SelfHostedSealedFiles(restarted, tenant, uploadLifecycle, storage); var actorId = Guid.NewGuid();
            var upload = JsonSerializer.Deserialize<SealedFileWrite>(JsonSerializer.Serialize(original, json), json)!;
            upload.Record.Id = Guid.NewGuid(); upload.Record.FileVersion = 1; upload.Record.RootFileRecordId = null; upload.Record.PreviousFileRecordId = null;
            foreach (var reference in upload.PayloadKeyReferences) reference.OwnerId = upload.Record.Id;
            upload.Attachments = [new() { Id = Guid.NewGuid(), OriginalAttachmentId = Guid.NewGuid(), FileRecordId = upload.Record.Id, KnowledgeId = restored.SealedItems!.Single().Id }];
            var uploadCatalog = (await uploadLifecycle.GetCatalogAsync(vault, default))!;
            var stage = await uploadService.StageAsync(actorId, upload, uploadCatalog.Revision, "fresh-upload", default); Assert.True(stage.Ok, stage.Error);
            lostCommit.Reset(!race);
            var uploaded = await uploadService.UploadAsync(actorId, upload.Record.Id, new MemoryStream(expectedBytes), stage.Revision!, upload.Record.BinarySha256!, default, _ => Task.FromResult(true));
            Assert.True(uploaded.Ok, uploaded.Error); Assert.Equal(!race, lostCommit.Injected);
            Assert.Equal(3, await restarted.ShSealedFiles.CountAsync());
            await using var newBytes = await Graph(restarted).OpenFileAsync(upload.Record.Id, default); using var copied = new MemoryStream(); await newBytes.CopyToAsync(copied); Assert.Equal(expectedBytes, copied.ToArray());

        }
        finally { await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin); await drop.ExecuteNonQueryAsync(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        SelfHostedGraphStore Graph(SelfHostedDbContext db) => new(db, tenant, new PortableImportService(db, tenant, storage, NullLogger<PortableImportService>.Instance), new PortableExportService(db, tenant, storage, Options.Create(new SelfHostedOptions()), NullLogger<PortableExportService>.Instance), storage);
    }    private sealed class FileCommitResponseLost : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool Injected { get; private set; }
        public void Reset(bool armed) { Armed = armed; Injected = false; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Armed && !Injected) { Injected = true; throw new TimeoutException("Synthetic lost file COMMIT response."); }
            return Task.CompletedTask;
        }
    }

}
