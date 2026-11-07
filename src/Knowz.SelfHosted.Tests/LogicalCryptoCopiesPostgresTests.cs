using System.Security.Cryptography;
using System.Text.Json;
using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
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
    public async Task LogicalCopiesRetainOriginalCryptoSubjectsAcrossConnectedArchiveParts(bool multipart)
    {
        var connection = Environment.GetEnvironmentVariable("KNOWZ_TEST_POSTGRES"); Assert.False(string.IsNullOrEmpty(connection));
        var database = "kc_copies_" + Guid.NewGuid().ToString("N");
        await using var server = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection) { Database = "postgres", Pooling = false }.ConnectionString);
        await server.OpenAsync(); await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", server)) await create.ExecuteNonQueryAsync();
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>().UseNpgsql(new NpgsqlConnectionStringBuilder(connection) { Database = database, Pooling = false }.ConnectionString, pg => pg.EnableRetryOnFailure()).Options;
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var (source, _, _) = Fixture();
        var item = Copy(source.SealedItems![0]); item.Id = Guid.NewGuid(); source.SealedItems.Add(item);
        var comment = Copy(source.SealedComments![0]); comment.Id = Guid.NewGuid(); comment.KnowledgeId = item.Id; source.SealedComments.Add(comment);
        var file = Copy(source.SealedFiles![0]); file.Id = Guid.NewGuid(); source.SealedFiles.Add(file);
        source.SealedAttachments!.Add(new() { Id = Guid.NewGuid(), OriginalAttachmentId = Guid.NewGuid(), FileRecordId = file.Id, KnowledgeId = item.Id });
        foreach (var reference in source.PayloadKeyReferences!.ToArray())
        { var copied = Copy(reference); copied.OwnerId = reference.OwnerKind switch { "knowledge" => item.Id, "comment" => comment.Id, _ => file.Id }; source.PayloadKeyReferences.Add(copied); }
        Assert.Null(EncryptedGraphProfile.Validate(source));
        var first = Copy(source); var second = Copy(source);
        first.SealedComments = []; first.SealedFiles = []; first.SealedAttachments = []; first.PayloadKeyReferences!.RemoveAll(r => r.OwnerKind != "knowledge");
        second.SealedItems = []; second.Data.KnowledgeItems = []; second.PayloadKeyReferences!.RemoveAll(r => r.OwnerKind == "knowledge");
        try
        {
            await using (var db = new SelfHostedDbContext(options, tenant))
            {
                await db.Database.MigrateAsync(); var user = new User { TenantId = _tenant, Username = "logical-copy-owner", IsActive = true, Role = UserRole.Admin };
                db.Tenants.Add(new() { Id = _tenant, Name = "Logical copies", Slug = database }); db.Users.Add(user); await db.SaveChangesAsync();
                var graph = Graph(db); var adapter = new SelfHostedArchiveImportAdapter(db, tenant, graph, new VaultAccessService(db)); using var coordinator = new ArchiveImportCoordinator(); using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                using var input = new MemoryStream(); var receipt = await KnowzArchiveWriter.WriteAsync(input, multipart ? [first, second] : [source], null, new() { SigningKey = signer, SourceSnapshot = "logical-copies" }); input.Position = 0;
                var actor = new ArchiveActor(_tenant, user.Id.ToString("D")); var plan = await coordinator.CreatePlanAsync(input, actor, new() { ExpectedSignerFingerprint = receipt.SignerFingerprint, SourceReceipt = receipt, IdempotencyKey = "logical-copies" }, adapter);
                var committed = await coordinator.CommitAsync(plan.PlanId, actor, adapter); Assert.Equal(committed.RequiredPayloadCount, committed.PreservedPayloadCount);
                Assert.Equal(2, await db.ShSealedKnowledge.CountAsync()); Assert.Equal(2, await db.ShSealedComments.CountAsync()); Assert.Equal(2, await db.ShSealedFiles.CountAsync());
            }
            await using (var db = new SelfHostedDbContext(options, tenant))
            {
                var graph = Graph(db); var exported = await graph.ExportAsync(new() { Type = ExportScopeType.Tenant }, false, default);
                var service = new SelfHostedArchiveExportService(db, tenant, graph, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
                var downloaded = await service.ExportAsync(new() { Type = ExportScopeType.Tenant }, false, default);
                using var verified = await KnowzArchiveReader.OpenAsync(downloaded.Stream, new() { ExpectedSignerFingerprint = downloaded.Receipt.SignerFingerprint });
                Assert.Equal(2, PortableGraphPackages.Combine(verified.Packages).SealedItems!.Count);
                Assert.Null(EncryptedGraphProfile.Validate(exported)); Assert.Equal(2, exported.SealedItems!.Count); Assert.Equal(2, exported.SealedComments!.Count); Assert.Equal(2, exported.SealedFiles!.Count);
                Assert.Single(exported.SealedItems.Select(i => i.OriginalItemId).Distinct()); Assert.Single(exported.SealedComments.Select(c => c.OriginalCommentId).Distinct()); Assert.Single(exported.SealedFiles.Select(f => f.OriginalFileId).Distinct());
                Assert.All(exported.SealedItems, i => Assert.Equal(source.SealedItems[0].Ciphertext, i.Ciphertext));
                foreach (var restored in exported.SealedFiles) { await using var stream = await graph.OpenFileAsync(restored.Id, default); using var copied = new MemoryStream(); await stream.CopyToAsync(copied); Assert.Equal(Convert.FromBase64String(file.BinaryContentBase64!), copied.ToArray()); }
            }
        }
        finally { await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", server); await drop.ExecuteNonQueryAsync(); }
        SelfHostedGraphStore Graph(SelfHostedDbContext db) => new(db, tenant, new(db, tenant, _storage, NullLogger<PortableImportService>.Instance), new(db, tenant, _storage, Options.Create(new SelfHostedOptions()), NullLogger<PortableExportService>.Instance), _storage);
        static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    }
}
