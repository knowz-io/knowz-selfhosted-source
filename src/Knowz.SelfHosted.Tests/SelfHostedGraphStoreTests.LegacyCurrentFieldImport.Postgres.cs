using System.Text.Json;
using Knowz.Core.Configuration;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;
public sealed partial class SelfHostedGraphStoreTests
{
    [PostgresTheory]
    [InlineData(ImportConflictStrategy.Skip, false)]
    [InlineData(ImportConflictStrategy.Merge, false)]
    [InlineData(ImportConflictStrategy.Overwrite, false)]
    [InlineData(ImportConflictStrategy.Skip, true)]
    [InlineData(ImportConflictStrategy.Merge, true)]
    [InlineData(ImportConflictStrategy.Overwrite, true)]
    public async Task LegacyCurrentFieldOmissionPostgresRetainsOriginalRecoveryAfterRestart(ImportConflictStrategy strategy, bool retries)
    {
        var configured = Environment.GetEnvironmentVariable("KNOWZ_TEST_POSTGRES"); Assert.False(string.IsNullOrWhiteSpace(configured));
        var connection = new NpgsqlConnectionStringBuilder(configured); Assert.True(connection.Host is "localhost" or "127.0.0.1" && connection.Port == 18460);
        connection.Database = "postgres"; connection.Pooling = false;
        await using var admin = new NpgsqlConnection(connection.ConnectionString); await admin.OpenAsync();
        var name = "kc_legacy_field_" + Guid.NewGuid().ToString("N"); await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"",admin)) await create.ExecuteNonQueryAsync();
        connection.Database = name; var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(Guid.NewGuid());
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>().UseNpgsql(connection.ConnectionString, o => { if (retries) o.EnableRetryOnFailure(); }).Options;
        var directory = Path.Combine(Path.GetTempPath(),name); var storage = new LocalFileStorageProvider(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Storage:Local:RootPath"] = directory }).Build(),NullLogger<LocalFileStorageProvider>.Instance);
        try
        {
            var source = await LegacyOmissionFixtureAsync(); Guid itemId; Guid vaultId; string beforeRefs; string beforeHistory;
            await using (var db = new SelfHostedDbContext(options,tenant))
            {
                await db.Database.MigrateAsync(); db.Tenants.Add(new() { Id = tenant.TenantId,Name = "Owned legacy field fixture",Slug = name }); await db.SaveChangesAsync();
                var graph = Graph(db);
                var imported = await db.Database.CreateExecutionStrategy().ExecuteAsync(() => graph.ImportAsync(source,null,"trusted:legacy-field-native",ImportConflictStrategy.Skip,null,default));
                Assert.True(imported.Success,imported.Error);
                itemId = (await db.KnowledgeItems.SingleAsync()).Id; vaultId = (await db.KnowledgeVaults.SingleAsync()).VaultId;
                beforeRefs = JsonSerializer.Serialize(await db.ShPayloadKeyReferences.AsNoTracking().OrderBy(r=>r.Id).ToListAsync());
                beforeHistory = JsonSerializer.Serialize(await db.KnowledgeVersions.AsNoTracking().ToListAsync());
            }
            await using (var db = new SelfHostedDbContext(options,tenant))
            {
                // Direct public legacy service, deliberately without an archive adapter transaction.
                var package = LegacyOmissionPackage(itemId,vaultId);
                var result = await new PortableImportService(db,tenant,storage,NullLogger<PortableImportService>.Instance).ImportAsync(package,vaultId,strategy);
                if (strategy == ImportConflictStrategy.Skip) { Assert.True(result.Success,result.Error); Assert.Equal(1,result.KnowledgeItems.Skipped); }
                else { Assert.False(result.Success); Assert.Contains("protected",result.Error,StringComparison.OrdinalIgnoreCase); Assert.False(await db.Topics.AnyAsync(t=>t.Id==package.Data.Topics.Single().Id)); }
            }
            await using (var db = new SelfHostedDbContext(options,tenant))
            {
                var retained = await db.KnowledgeItems.SingleAsync(k=>k.Id==itemId); var original = source.Data.KnowledgeItems.Single();
                Assert.Equal(original.EncryptedFieldsJson,retained.EncryptedFieldsJson); Assert.Equal(original.OriginalItemId,retained.OriginalItemId);
                Assert.Equal(original.Content,retained.Content); Assert.Equal(original.Title,retained.Title); Assert.Null(retained.Summary);
                Assert.Equal(beforeRefs,JsonSerializer.Serialize(await db.ShPayloadKeyReferences.AsNoTracking().OrderBy(r=>r.Id).ToListAsync()));
                Assert.Equal(beforeHistory,JsonSerializer.Serialize(await db.KnowledgeVersions.AsNoTracking().ToListAsync()));
                var exported = await new SelfHostedArchiveExportService(db,tenant,Graph(db),new EphemeralDataProtectionProvider()).ExportAsync(new() { Type=ExportScopeType.Vault,VaultIds=[vaultId] },false,default);
                await using (exported.Stream)
                {
                    using var opened = await KnowzArchiveReader.OpenAsync(exported.Stream,new() { ExpectedSignerFingerprint=exported.Receipt.SignerFingerprint,LeaveOpen=true });
                    var graph = PortableGraphPackages.Combine(opened.Packages); Assert.Equal("knowledge:title",DecryptCurrentField(graph.Data.KnowledgeItems.Single().EncryptedFieldsJson!));
                    Assert.Equal(source.PlainVersions!.Single().SnapshotJson,graph.PlainVersions!.Single().SnapshotJson); Assert.Equal(3,graph.PayloadKeyReferences!.Count);
                }
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)",admin); await drop.ExecuteNonQueryAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory,true);
        }
        SelfHostedGraphStore Graph(SelfHostedDbContext db) => new(db,tenant,new(db,tenant,storage,NullLogger<PortableImportService>.Instance),new(db,tenant,storage,Options.Create(new SelfHostedOptions()),NullLogger<PortableExportService>.Instance),storage);
    }
}
