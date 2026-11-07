using System.Data.Common;
using System.Text;
using System.Text.Json;
using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    public async Task ProtectedPlainReplacementNeverPublishesAfterPostCommitRevocation(bool revoke)
    {
        var connection = Environment.GetEnvironmentVariable("KNOWZ_TEST_POSTGRES"); Assert.False(string.IsNullOrEmpty(connection)); var database = "kc_plain_revoke_" + Guid.NewGuid().ToString("N");
        await using var server = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection) { Database = "postgres", Pooling = false }.ConnectionString); await server.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", server)) await create.ExecuteNonQueryAsync();
        var scopedConnection = new NpgsqlConnectionStringBuilder(connection) { Database = database, Pooling = false }.ConnectionString;
        var userId = Guid.NewGuid(); var lost = new PlainCommitRevocation(scopedConnection, userId, revoke);
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>().UseNpgsql(scopedConnection, pg => pg.EnableRetryOnFailure()).AddInterceptors(lost).Options;
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web); json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var source = JsonSerializer.Deserialize<PortableExportPackage>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portable-graph-v1.json")), json)!;
        var file = source.SealedFiles!.Single(); var bytes = Encoding.UTF8.GetBytes("ordinary retained original"); var replacement = Encoding.UTF8.GetBytes("ordinary replacement; protected metadata unchanged");
        source.Data.FileRecords = [new() { Id = file.Id, OriginalFileId = file.OriginalFileId, FileName = "encrypted-file", EncryptedFieldsJson = file.EncryptedFieldsJson, BinaryContentBase64 = Convert.ToBase64String(bytes), SizeBytes = bytes.Length }]; source.SealedFiles = [];
        source.PayloadKeyReferences!.RemoveAll(r => r.OwnerKind == "file" && r.Role == "binary");
        try
        {
            await using var db = new SelfHostedDbContext(options, tenant); await db.Database.MigrateAsync(); db.Tenants.Add(new() { Id = _tenant, Name = "Plain revocation", Slug = database }); db.Users.Add(new() { Id = userId, TenantId = _tenant, Username = "file-owner", IsActive = true }); await db.SaveChangesAsync();
            var graph = new SelfHostedGraphStore(db, tenant, new(db, tenant, _storage, NullLogger<PortableImportService>.Instance), new(db, tenant, _storage, Options.Create(new SelfHostedOptions()), NullLogger<PortableExportService>.Instance), _storage);
            Assert.True((await db.Database.CreateExecutionStrategy().ExecuteAsync(() => graph.ImportAsync(source, null, "trusted:plain-revoke", ImportConflictStrategy.Skip, null, default))).Success);
            var current = await db.FileRecords.SingleAsync(); var vault = (await db.Vaults.SingleAsync()).Id; var lifecycle = new SelfHostedSealedStore(db, tenant, NullLogger<SelfHostedSealedStore>.Instance);
            var catalog = (await lifecycle.GetCatalogAsync(vault, default))!; var key = catalog.Keys.Single(); Assert.True((await lifecycle.SelectPrimaryAsync(vault, key.ContextId, key.Fingerprint, catalog.Revision, default, key.Generation)).Ok);
            var projection = (await SelfHostedSealedFiles.GetProtectedFileAsync(db, current.Id, default))!; var revision = SelfHostedSealedStore.PayloadRevision(projection); projection.SizeBytes = replacement.Length; projection.BinarySha256 = ArchivePeerProof.Fingerprint(replacement);
            var refs = (await db.ShPayloadKeyReferences.Where(r => r.OwnerKind == "file").AsNoTracking().ToListAsync()).Select(PortablePublicProjection.Reference).ToList(); lost.Armed = true;
            var result = await new SelfHostedSealedFiles(db, tenant, lifecycle, _storage).UpdateProtectedFileAsync(userId, new() { VaultId = vault, Record = projection, PayloadKeyReferences = refs }, revision, new MemoryStream(replacement), default,
                ct => db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.IsActive, ct));
            Assert.True(lost.Injected); Assert.Equal(!revoke, result.Ok); if (revoke) { Assert.Equal(409, result.StatusCode); Assert.Null(result.Id); }
            var rows = await db.FileRecords.AsNoTracking().ToListAsync(); Assert.Equal(2, rows.Count); var latest = Assert.Single(rows, f => f.IsCurrentVersion); Assert.Equal(current.Id, latest.PreviousFileRecordId);
            foreach (var (id, expected) in new[] { (current.Id, bytes), (latest.Id, replacement) }) { await using var input = await graph.OpenFileAsync(id, default); using var output = new MemoryStream(); await input.CopyToAsync(output); Assert.Equal(expected, output.ToArray()); }
            Assert.Equal("committed", (await db.ShSealedFileStages.SingleAsync()).Status);
        }
        finally { await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", server); await drop.ExecuteNonQueryAsync(); }
    }
    private sealed class PlainCommitRevocation(string connection, Guid user, bool revoke) : DbTransactionInterceptor
    {
        public bool Armed { get; set; } public bool Injected { get; private set; }
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (!Armed || Injected) return; Injected = true;
            if (revoke) { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(cancellationToken); await using var cmd = new NpgsqlCommand("UPDATE \"Users\" SET \"IsActive\" = FALSE WHERE \"Id\" = @id", db); cmd.Parameters.AddWithValue("id", user); await cmd.ExecuteNonQueryAsync(cancellationToken); }
            throw new TimeoutException("Synthetic lost file commit acknowledgement.");
        }
    }
}
