using System.Security.Cryptography;
using System.Text.Json;
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
    public async Task TypedRelationshipsSurviveNativeRestartAndSignedReturn(bool sealedOwner)
    {
        var connection = Environment.GetEnvironmentVariable("KNOWZ_TEST_POSTGRES"); Assert.False(string.IsNullOrWhiteSpace(connection));
        var database = "kc_relations_" + Guid.NewGuid().ToString("N");
        var admin = new NpgsqlConnectionStringBuilder(connection) { Database = "postgres", Pooling = false };
        await using var server = new NpgsqlConnection(admin.ConnectionString); await server.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", server)) await create.ExecuteNonQueryAsync();
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>().UseNpgsql(new NpgsqlConnectionStringBuilder(connection) { Database = database, Pooling = false }.ConnectionString).Options;
        var directory = Path.Combine(Path.GetTempPath(), database);
        var storage = new LocalFileStorageProvider(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Storage:Local:RootPath"] = directory }).Build(), NullLogger<LocalFileStorageProvider>.Instance);
        var (source, _, _) = Fixture(); var plain = source.Data.KnowledgeItems.Single(); var encrypted = source.SealedItems!.Single();
        var owner = sealedOwner ? encrypted.Id : plain.Id; var targetOwner = sealedOwner ? plain.Id : encrypted.Id;
        var location = Guid.NewGuid(); var eventId = Guid.NewGuid();
        var links = new[] { new PortableEntityLink { EntityId = location, RelationshipContext = "{\"dateOnly\":\"2026-10-07\",\"note\":\"exact context — 猫\"}", Role = "location-role", Mentions = 5, ConfidenceScore = .83 },
            new PortableEntityLink { EntityId = eventId, RelationshipContext = "Original event context", Role = "event-role", Mentions = 2, ConfidenceScore = .79 } };
        source.Data.Locations.Add(new() { Id = location, Name = "Native location" }); source.Data.Events.Add(new() { Id = eventId, Name = "Native event" });
        var timestamp = new DateTime(DateTime.UtcNow.Ticks / 10 * 10, DateTimeKind.Utc);
        var relationship = new PortableKnowledgeRelationship { Id = Guid.NewGuid(), OriginalRelationshipId = Guid.NewGuid(), SourceKnowledgeId = owner, TargetKnowledgeId = targetOwner,
            RelationshipType = Knowz.Core.Enums.KnowledgeRelationshipType.RelatedTo, Confidence = .89, PortableMetadataJson = "{\"evidence\":\"original nested value\"}", CreatedAt = timestamp.AddDays(-3), UpdatedAt = timestamp.AddDays(-2) };
        if (sealedOwner) { encrypted.LocationIds = [location]; encrypted.EventIds = [eventId]; encrypted.LocationLinks = [links[0]]; encrypted.EventLinks = [links[1]]; encrypted.Relationships = [relationship]; }
        else { plain.LocationIds = [location]; plain.EventIds = [eventId]; plain.LocationLinks = [links[0]]; plain.EventLinks = [links[1]]; plain.Relationships = [relationship]; }
        try
        {
            using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var original = new MemoryStream();
            var sourceReceipt = await KnowzArchiveWriter.WriteAsync(original, [source], null, new() { SigningKey = signer, SourceSnapshot = "native-relations" }); original.Position = 0;
            var first = await ImportAndReturn(original, sourceReceipt, owner, targetOwner, location, eventId, true);
            await using (first.Stream)
            {
                var second = await ImportAndReturn(first.Stream, first.Receipt, first.Owner, first.Target, first.Location, first.Event, false);
                await second.Stream.DisposeAsync();
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", server); await drop.ExecuteNonQueryAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        async Task<(Stream Stream, ArchiveSourceReceipt Receipt, Guid Owner, Guid Target, Guid Location, Guid Event)> ImportAndReturn(
            Stream input, ArchiveSourceReceipt receipt, Guid sourceOwner, Guid sourceTarget, Guid sourceLocation, Guid sourceEvent, bool migrate)
        {
            var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(Guid.NewGuid()); ArchiveImportPlan plan;
            await using (var db = new SelfHostedDbContext(options, tenant))
            {
                if (migrate) await db.Database.MigrateAsync();
                db.Tenants.Add(new() { Id = tenant.TenantId, Name = "Native relations", Slug = "native-relations-" + tenant.TenantId.ToString("N") });
                var user = new User { TenantId = tenant.TenantId, Username = "relations-owner-" + tenant.TenantId.ToString("N"), Role = Knowz.Core.Enums.UserRole.Admin, IsActive = true };
                db.Users.Add(user); await db.SaveChangesAsync();
                var adapter = new SelfHostedArchiveImportAdapter(db, tenant, Graph(db, tenant), new VaultAccessService(db));
                using var coordinator = new ArchiveImportCoordinator(); var actor = new ArchiveActor(tenant.TenantId, user.Id.ToString("D"));
                plan = await coordinator.CreatePlanAsync(input, actor, new() { ExpectedSignerFingerprint = receipt.SignerFingerprint, SourceReceipt = receipt, Strategy = "overwrite", IdempotencyKey = "native-relations" }, adapter);
                var committed = await coordinator.CommitAsync(plan.PlanId, actor, adapter); Assert.Equal("complete", committed.PreservationState);
            }
            Guid M(string kind, Guid id) => Guid.Parse(plan.Mapping[kind + ":" + id.ToString("D")]);
            await using var reopened = new SelfHostedDbContext(options, tenant);
            var output = await new SelfHostedArchiveExportService(reopened, tenant, Graph(reopened, tenant), new EphemeralDataProtectionProvider()).ExportAsync(new() { Type = ExportScopeType.Tenant }, false, default);
            using (var archive = await KnowzArchiveReader.OpenAsync(output.Stream, new() { ExpectedSignerFingerprint = output.Receipt.SignerFingerprint, LeaveOpen = true }))
            {
                var p = PortableGraphPackages.Combine(archive.Packages); var mapped = M("knowledge", sourceOwner);
                var json = sealedOwner ? JsonSerializer.SerializeToElement(p.SealedItems!.Single(k => k.Id == mapped)) : JsonSerializer.SerializeToElement(p.Data.KnowledgeItems.Single(k => k.Id == mapped));
                foreach (var (name, kind, id, expected) in new[] { ("LocationLinks", "location", sourceLocation, links[0]), ("EventLinks", "event", sourceEvent, links[1]) })
                {
                    var link = Assert.Single(json.GetProperty(name).Deserialize<List<PortableEntityLink>>()!); Assert.Equal(M(kind, id), link.EntityId);
                    Assert.Equal(expected.RelationshipContext, link.RelationshipContext); Assert.Equal(expected.Role, link.Role); Assert.Equal(expected.Mentions, link.Mentions); Assert.Equal(expected.ConfidenceScore, link.ConfidenceScore);
                }
                var related = Assert.Single(json.GetProperty("Relationships").Deserialize<List<PortableKnowledgeRelationship>>()!);
                Assert.Equal(M("knowledge", sourceOwner), related.SourceKnowledgeId); Assert.Equal(M("knowledge", sourceTarget), related.TargetKnowledgeId);
                Assert.Equal(relationship.OriginalRelationshipId, related.OriginalRelationshipId); Assert.Equal(relationship.PortableMetadataJson, related.PortableMetadataJson);
                Assert.Equal(relationship.Confidence, related.Confidence); Assert.Equal(relationship.CreatedAt, related.CreatedAt); Assert.Equal(relationship.UpdatedAt, related.UpdatedAt);
                Assert.DoesNotContain("$knowz.portable", json.GetRawText(), StringComparison.Ordinal);
            }
            output.Stream.Position = 0;
            return (output.Stream, output.Receipt, M("knowledge", sourceOwner), M("knowledge", sourceTarget), M("location", sourceLocation), M("event", sourceEvent));
        }
        SelfHostedGraphStore Graph(SelfHostedDbContext db, ITenantProvider tenant) => new(db, tenant,
            new PortableImportService(db, tenant, storage, NullLogger<PortableImportService>.Instance),
            new PortableExportService(db, tenant, storage, Options.Create(new SelfHostedOptions()), NullLogger<PortableExportService>.Instance), storage);
    }
}
