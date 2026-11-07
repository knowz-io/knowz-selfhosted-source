using System.Text.Json;
using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Application.DTOs;
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
public sealed partial class SelfHostedGraphStoreTests
{
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentFieldsFreshAndUpgradeMigrationsSurviveRestartAndSignedReturn(bool upgrade)
    {
        var connection = Environment.GetEnvironmentVariable("KNOWZ_TEST_POSTGRES"); Assert.False(string.IsNullOrEmpty(connection));
        var name = "kc_current_" + Guid.NewGuid().ToString("N"); var admin = new NpgsqlConnectionStringBuilder(connection) { Database = "postgres", Pooling = false };
        await using var server = new NpgsqlConnection(admin.ConnectionString); await server.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", server)) await create.ExecuteNonQueryAsync();
        var target = new NpgsqlConnectionStringBuilder(connection) { Database = name, Pooling = false }; var directory = Path.Combine(Path.GetTempPath(), name);
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(Guid.NewGuid());
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>().UseNpgsql(target.ConnectionString).Options;
        var storage = new LocalFileStorageProvider(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Storage:Local:RootPath"] = directory }).Build(), NullLogger<LocalFileStorageProvider>.Instance);
        var source = await CurrentFieldFixtureAsync(); Guid restoredId; Guid destination;
        try
        {
            await using (var db = new SelfHostedDbContext(options, tenant))
            {
                if (upgrade) { await db.GetService<IMigrator>().MigrateAsync("20261006234846_AddSealedGraphLogicalDeletion"); db.Tenants.Add(new() { Id = tenant.TenantId, Name = "current-upgrade", Slug = name }); db.Vaults.Add(new() { TenantId = tenant.TenantId, Name = "pre-upgrade owner" }); await db.SaveChangesAsync(); }
                await db.Database.MigrateAsync();
                if (!upgrade) { db.Tenants.Add(new() { Id = tenant.TenantId, Name = "current-fresh", Slug = name }); await db.SaveChangesAsync(); }
                var graph = Graph(db); var result = await graph.ImportAsync(source, null, "trusted:current-pg", ImportConflictStrategy.Skip, null, default); Assert.True(result.Success, result.Error);
                var row = await db.KnowledgeItems.SingleAsync(k => k.Content == source.Data.KnowledgeItems.Single().Content);
                Assert.Equal(source.Data.KnowledgeItems.Single().EncryptedFieldsJson, row.EncryptedFieldsJson); restoredId = row.Id;
                destination = (await db.KnowledgeVaults.SingleAsync(v => v.KnowledgeId == row.Id)).VaultId;
            }
            await using (var db = new SelfHostedDbContext(options, tenant))
            {
                var row = await db.KnowledgeItems.SingleAsync(k => k.Id == restoredId); Assert.Equal(source.Data.KnowledgeItems.Single().EncryptedFieldsJson, row.EncryptedFieldsJson); Assert.Equal("knowledge:title", DecryptCurrentField(row.EncryptedFieldsJson!));
                var archiveService = new SelfHostedArchiveExportService(db, tenant, Graph(db), new EphemeralDataProtectionProvider());
                var output = await archiveService.ExportAsync(new() { Type = ExportScopeType.Vault, VaultIds = [destination] }, false, default);
                await using (output.Stream) { using var archive = await KnowzArchiveReader.OpenAsync(output.Stream, new() { ExpectedSignerFingerprint = output.Receipt.SignerFingerprint, LeaveOpen = true });
                    var package = PortableGraphPackages.Combine(archive.Packages); var current = package.Data.KnowledgeItems.Single(k => k.Id == restoredId);
                    Assert.Equal(row.EncryptedFieldsJson, current.EncryptedFieldsJson); Assert.Equal(source.Data.KnowledgeItems.Single().Id, current.OriginalItemId);
                    var reference = Assert.Single(package.PayloadKeyReferences!, r => r.OwnerKind == "knowledge" && r.OwnerId == restoredId); Assert.Equal(source.PayloadKeyReferences!.Last().OriginalBindingId, reference.OriginalBindingId); Assert.Equal(source.PayloadKeyReferences.Last().Fingerprint, reference.Fingerprint);
                }
                if (upgrade) Assert.True(await db.Vaults.AnyAsync(v => v.Name == "pre-upgrade owner"));
            }
        }
        finally { await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", server); await drop.ExecuteNonQueryAsync(); if (Directory.Exists(directory)) Directory.Delete(directory,true); }
        SelfHostedGraphStore Graph(SelfHostedDbContext db) => new(db, tenant, new PortableImportService(db,tenant,storage,NullLogger<PortableImportService>.Instance), new PortableExportService(db,tenant,storage,Options.Create(new SelfHostedOptions()),NullLogger<PortableExportService>.Instance),storage);
    }
}
