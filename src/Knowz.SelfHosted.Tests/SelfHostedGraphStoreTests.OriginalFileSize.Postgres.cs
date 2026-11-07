using Knowz.Core.Configuration;
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

public sealed partial class SelfHostedGraphStoreTests
{
    [PostgresTheory]
    [InlineData(false, null)]
    [InlineData(false, 0L)]
    [InlineData(false, 42L)]
    [InlineData(true, null)]
    [InlineData(true, 0L)]
    [InlineData(true, 42L)]
    public async Task OriginalFileSizeFreshUpgradeAndRestartSignedReturn(bool upgrade, long? originalSize)
    {
        var connection = Environment.GetEnvironmentVariable("KNOWZ_TEST_POSTGRES"); Assert.False(string.IsNullOrWhiteSpace(connection));
        var name = "kc_size_" + Guid.NewGuid().ToString("N"); var oldFile = Guid.NewGuid(); var oldTenant = Guid.NewGuid();
        var admin = new NpgsqlConnectionStringBuilder(connection) { Database = "postgres", Pooling = false };
        await using var server = new NpgsqlConnection(admin.ConnectionString); await server.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", server)) await create.ExecuteNonQueryAsync();
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>().UseNpgsql(new NpgsqlConnectionStringBuilder(connection) { Database = name, Pooling = false }.ConnectionString).Options;
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(Guid.NewGuid()); var directory = Path.Combine(Path.GetTempPath(), name);
        var storage = new LocalFileStorageProvider(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Storage:Local:RootPath"] = directory }).Build(), NullLogger<LocalFileStorageProvider>.Instance);
        var (source, _, _) = Fixture(); source.SealedFiles!.Single().OriginalSizeBytes = originalSize;
        try
        {
            await using (var db = new SelfHostedDbContext(options, tenant))
            {
                if (upgrade)
                {
                    await db.GetService<IMigrator>().MigrateAsync("20261007020001_AddCurrentProtectedKnowledgeFields");
                    var timestamp = DateTime.UtcNow;
                    await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"ShSealedFiles\" (\"Id\",\"TenantId\",\"SizeBytes\",\"CryptoBindingId\",\"CreatedAt\",\"UpdatedAt\",\"OriginalFileId\",\"IsDeleted\") VALUES ({oldFile},{oldTenant},{16L},{Guid.NewGuid()},{timestamp},{timestamp},{oldFile},true)");
                }
                await db.Database.MigrateAsync(); await db.Database.MigrateAsync();
                db.Tenants.Add(new() { Id = tenant.TenantId, Name = "Original size", Slug = name }); await db.SaveChangesAsync();
                Assert.True((await Graph(db).ImportAsync(source, null, "trusted:native-original-size", ImportConflictStrategy.Overwrite, null, default)).Success);
            }
            await using (var reopened = new SelfHostedDbContext(options, tenant))
            {
                if (upgrade)
                {
                    var legacy = await reopened.ShSealedFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == oldFile && f.TenantId == oldTenant);
                    Assert.Null(legacy.OriginalSizeBytes); Assert.Equal(16, legacy.SizeBytes); Assert.True(legacy.IsDeleted);
                }
                var exported = await new SelfHostedArchiveExportService(reopened, tenant, Graph(reopened), new EphemeralDataProtectionProvider()).ExportAsync(new() { Type = ExportScopeType.Tenant }, false, default);
                await using (exported.Stream)
                {
                    using var archive = await KnowzArchiveReader.OpenAsync(exported.Stream, new() { ExpectedSignerFingerprint = exported.Receipt.SignerFingerprint });
                    Assert.Equal(originalSize, Assert.Single(PortableGraphPackages.Combine(archive.Packages).SealedFiles!).OriginalSizeBytes);
                }
                Assert.Empty(await reopened.Database.GetPendingMigrationsAsync());
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", server); await drop.ExecuteNonQueryAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        SelfHostedGraphStore Graph(SelfHostedDbContext db) => new(db, tenant, new PortableImportService(db, tenant, storage, NullLogger<PortableImportService>.Instance),
            new PortableExportService(db, tenant, storage, Options.Create(new SelfHostedOptions()), NullLogger<PortableExportService>.Instance), storage);
    }
}
