using System.Text.Json;
using System.Text.Json.Serialization;
using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed class EncryptedGraphPostgresTests
{
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealPostgres_MigrationsAtomicImportAndRestartPreserveCiphertextAndIdentity(bool upgrade)
    {
        var connection = Environment.GetEnvironmentVariable("KNOWZ_TEST_POSTGRES");
        Assert.False(string.IsNullOrEmpty(connection));
        var name = "kc_graph_" + Guid.NewGuid().ToString("N");
        var admin = new NpgsqlConnectionStringBuilder(connection) { Database = "postgres", Pooling = false };
        await using var server = new NpgsqlConnection(admin.ConnectionString); await server.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", server)) await create.ExecuteNonQueryAsync();
        var target = new NpgsqlConnectionStringBuilder(connection) { Database = name, Pooling = false };
        var root = Path.Combine(Path.GetTempPath(), name); Directory.CreateDirectory(root);
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(Guid.NewGuid());
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>().UseNpgsql(target.ConnectionString).Options;
        var storage = new LocalFileStorageProvider(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Local:RootPath"] = Path.Combine(root, "files") }).Build(), NullLogger<LocalFileStorageProvider>.Instance);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web); json.Converters.Add(new JsonStringEnumConverter());
        var fixture = JsonSerializer.Deserialize<PortableExportPackage>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portable-graph-v1.json")), json)!;
        var plainFile = Guid.NewGuid(); var plainBytes = System.Text.Encoding.UTF8.GetBytes("Plain persisted bytes with encrypted filename");
        fixture.Data.FileRecords.Add(new() { Id = plainFile, OriginalFileId = fixture.SealedFiles!.Single().OriginalFileId, FileName = "encrypted-file", ContentType = "application/octet-stream", SizeBytes = plainBytes.Length, BinaryContentBase64 = Convert.ToBase64String(plainBytes), EncryptedFieldsJson = fixture.SealedFiles.Single().EncryptedFieldsJson,
            Attachments = [new() { KnowledgeId = fixture.Data.KnowledgeItems.Single().Id }] });
        var fieldRef = JsonSerializer.Deserialize<PortablePayloadKeyReference>(JsonSerializer.Serialize(fixture.PayloadKeyReferences!.Single(r => r.OwnerKind == "file" && r.Role == "field")))!;
        fieldRef.OwnerId = plainFile; fixture.PayloadKeyReferences.Add(fieldRef);
        Guid restoredFile; string signer; string ciphertext; Guid abandonedPlan; ArchiveActor? archiveActor = null; List<Guid> stagedObjects = [];
        try
        {
            await using (var db = new SelfHostedDbContext(options, tenant))
            {
                if (upgrade)
                {
                    await db.GetService<IMigrator>().MigrateAsync("20260927000000_AddShSealedComments");
                    // Existing ordinary data must survive the upgrade as well as new encrypted rows.
                    db.Vaults.Add(new Vault { Id = Guid.NewGuid(), TenantId = tenant.TenantId, Name = "before-upgrade" }); await db.SaveChangesAsync();
                }
                await db.Database.MigrateAsync();
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
                await db.Database.MigrateAsync();
                var graph = CreateGraph(db);
                var result = await graph.ImportAsync(fixture, null, "trusted:postgres-fixture", ImportConflictStrategy.Skip, null, CancellationToken.None);
                Assert.True(result.Success, result.Error);
                var exported = await graph.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None);
                Assert.Null(EncryptedGraphProfile.Validate(exported));
                restoredFile = exported.SealedFiles!.Single().Id; ciphertext = exported.SealedItems!.Single().Ciphertext;
                var service = new SelfHostedArchiveExportService(db, tenant, graph, DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys"))));
                signer = (await service.IdentityAsync(CancellationToken.None)).SignerFingerprint;
                using var archive = (await service.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None)).Stream;
                using var verified = await KnowzArchiveReader.OpenAsync(archive, new() { ExpectedSignerFingerprint = signer });
                Assert.Equal("trusted", verified.SourceTrust);
                db.Tenants.Add(new Tenant { Id = tenant.TenantId, Name = "Portability fixture", Slug = "portability-fixture" }); await db.SaveChangesAsync();
                var actor = new User { Id = Guid.NewGuid(), TenantId = tenant.TenantId, Username = "restart-admin", Role = Knowz.Core.Enums.UserRole.Admin, IsActive = true }; db.Users.Add(actor); await db.SaveChangesAsync();
                archiveActor = new(tenant.TenantId, actor.Id.ToString());
                using var coordinator = new ArchiveImportCoordinator();
                using var pendingInput = (await service.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None)).Stream;
                var pending = await coordinator.CreatePlanAsync(pendingInput, archiveActor, new() { IdempotencyKey = "before-restart", ExpectedSignerFingerprint = signer }, new SelfHostedArchiveImportAdapter(db, tenant, graph, new VaultAccessService(db)));
                abandonedPlan = pending.PlanId;
                stagedObjects = JsonSerializer.Deserialize<List<Guid>>((await db.ShArchiveImportJournals.SingleAsync(j => j.PlanId == abandonedPlan)).StagedObjectIdsJson)!;
                Assert.NotEmpty(stagedObjects);

            }
            await using (var restarted = new SelfHostedDbContext(options, tenant))
            {
                var graph = CreateGraph(restarted);
                var exported = await graph.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None);
                Assert.Equal(ciphertext, exported.SealedItems!.Single().Ciphertext);
                var plain = Assert.Single(exported.Data.FileRecords); Assert.Equal(fixture.Data.FileRecords.Single().EncryptedFieldsJson, plain.EncryptedFieldsJson); Assert.Equal(fixture.Data.FileRecords.Single().OriginalFileId, plain.OriginalFileId);
                using var plainStream = await graph.OpenFileAsync(plain.Id, CancellationToken.None); using var plainBuffer = new MemoryStream(); await plainStream.CopyToAsync(plainBuffer); Assert.Equal(plainBytes, plainBuffer.ToArray());
                using var file = await graph.OpenFileAsync(restoredFile, CancellationToken.None); using var bytes = new MemoryStream(); await file.CopyToAsync(bytes);
                Assert.Equal(Convert.FromBase64String(fixture.SealedFiles!.Single().BinaryContentBase64!), bytes.ToArray());
                var service = new SelfHostedArchiveExportService(restarted, tenant, graph, DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys"))));
                Assert.Equal(signer, (await service.IdentityAsync(CancellationToken.None)).SignerFingerprint);
                Assert.True((await graph.ImportAsync(fixture, null, "trusted:postgres-fixture", ImportConflictStrategy.Skip, null, CancellationToken.None)).Success);
                Assert.Single(await restarted.ShSealedKnowledge.ToListAsync());
                Assert.Single(await restarted.ShSealedVersions.ToListAsync());
                if (upgrade) Assert.True(await restarted.Vaults.AnyAsync(v => v.Name == "before-upgrade"));
                using var coordinator = new ArchiveImportCoordinator(); var adapter = new SelfHostedArchiveImportAdapter(restarted, tenant, graph, new VaultAccessService(restarted));
                var lost = await Assert.ThrowsAsync<ArchiveOperationException>(() => coordinator.CommitAsync(abandonedPlan, archiveActor!, adapter)); Assert.Equal(404, lost.StatusCode);
                await new SelfHostedPortabilityStageReconciler(restarted, storage, NullLogger<SelfHostedPortabilityStageReconciler>.Instance).ReconcileAsync(DateTime.UtcNow.AddHours(2), CancellationToken.None);
                foreach (var staged in stagedObjects) await Assert.ThrowsAsync<FileNotFoundException>(() => storage.DownloadAsync(tenant.TenantId, staged, CancellationToken.None));
                using var live = await graph.OpenFileAsync(restoredFile, CancellationToken.None); Assert.True(live.CanRead);
                Assert.Equal("cancelled", (await restarted.ShArchiveImportJournals.SingleAsync(j => j.PlanId == abandonedPlan)).Status);
                using var reupload = (await service.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None)).Stream;
                var replacement = await coordinator.CreatePlanAsync(reupload, archiveActor!, new() { IdempotencyKey = "after-restart", ExpectedSignerFingerprint = signer }, adapter);
                await coordinator.CancelAsync(replacement.PlanId, archiveActor!, adapter);
                var lifecycle = new SelfHostedSealedStore(restarted, tenant, NullLogger<SelfHostedSealedStore>.Instance);
                var vault = exported.Data.Vaults.Single(v => v.Name != "before-upgrade").Id;
                var catalog = (await lifecycle.GetCatalogAsync(vault, CancellationToken.None))!;
                Assert.True((await lifecycle.SelectPrimaryAsync(vault, catalog.Contexts.Single().ContextId, catalog.Keys.Single().Fingerprint, catalog.Revision, CancellationToken.None, catalog.Keys.Single().Generation)).Ok);
                var files = new SelfHostedSealedFiles(restarted, tenant, lifecycle, storage);
                var fileRecord = exported.SealedFiles!.Single(); fileRecord.BinaryEntryPath = null;
                var original = JsonSerializer.Deserialize<SealedFileRecord>(JsonSerializer.Serialize(await restarted.ShSealedFiles.AsNoTracking().SingleAsync()))!;
                var write = new SealedFileWrite { VaultId = vault, Record = fileRecord, PayloadKeyReferences = exported.PayloadKeyReferences!.Where(r => r.OwnerKind == "file" && r.OwnerId == fileRecord.Id).ToList() };
                var updated = await files.UpdateAsync(Guid.Parse(archiveActor!.ActorId), write, SelfHostedSealedStore.PayloadRevision(original), null, CancellationToken.None); Assert.True(updated.Ok, updated.Error);
                restarted.ChangeTracker.Clear();
                var persisted = JsonSerializer.Deserialize<SealedFileRecord>(JsonSerializer.Serialize(await restarted.ShSealedFiles.AsNoTracking().SingleAsync()))!;
                Assert.Equal(SelfHostedSealedStore.PayloadRevision(persisted), updated.Revision);
                Assert.True((await files.UpdateAsync(Guid.Parse(archiveActor.ActorId), write, updated.Revision!, null, CancellationToken.None)).Ok);

            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", server); await drop.ExecuteNonQueryAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        SelfHostedGraphStore CreateGraph(SelfHostedDbContext db) => new(db, tenant,
            new PortableImportService(db, tenant, storage, NullLogger<PortableImportService>.Instance),
            new PortableExportService(db, tenant, storage, Options.Create(new SelfHostedOptions()), NullLogger<PortableExportService>.Instance), storage);
    }
}

public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KNOWZ_TEST_POSTGRES"))) Skip = "Requires a dedicated owned PostgreSQL connection via KNOWZ_TEST_POSTGRES."; }
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KNOWZ_TEST_POSTGRES"))) Skip = "Requires a dedicated owned PostgreSQL connection via KNOWZ_TEST_POSTGRES."; }
}

// Opens a fresh unpooled admin connection per case; bursts of new connections from parallel classes
// get dropped during SSL setup by local port proxies, so this collection runs on its own.
[CollectionDefinition("OwnedPostgresSerial", DisableParallelization = true)]
public sealed class OwnedPostgresSerialCollection;
