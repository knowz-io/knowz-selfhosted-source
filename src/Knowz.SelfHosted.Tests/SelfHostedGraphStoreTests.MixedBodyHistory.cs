using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed partial class SelfHostedGraphStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentEncryptedBodySignedReturnPreservesEarlierOrdinaryFullHistory(bool encryptedFields)
    {
        var source = await CurrentFieldFixtureAsync(); foreach (var version in source.SealedVersions ?? []) version.VersionNumber += 10;
        Assert.True((await _graph.ImportAsync(source,null,"trusted:historical-body-mode",Knowz.SelfHosted.Application.DTOs.ImportConflictStrategy.Skip,null,default)).Success);
        var current = await _db.ShSealedKnowledge.SingleAsync();
        var original = new PortablePlainVersionRecord { Id = Guid.NewGuid(),KnowledgeId = current.KnowledgeId,OriginalKnowledgeId = current.OriginalItemId,
            VersionNumber = 1,Title = encryptedFields ? "Encrypted item" : "Historical ordinary title",Content = "Earlier ordinary body before encryption — 猫",
            SnapshotJson = encryptedFields ? JsonSerializer.Serialize(new { encryptedFieldsJson = current.EncryptedFieldsJson }) : null,
            CreatedAt = DateTime.UtcNow.AddDays(-1),UpdatedAt = DateTime.UtcNow.AddDays(-1) };
        _db.KnowledgeVersions.Add(new KnowledgeVersion { Id = original.Id,TenantId = _tenant,KnowledgeId = original.KnowledgeId,VersionNumber = 1,
            Title = original.Title,Content = original.Content,CreatedAt = original.CreatedAt,UpdatedAt = original.UpdatedAt,PlatformData = JsonSerializer.Serialize(original,new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
        if (encryptedFields)
            foreach (var reference in await _db.ShPayloadKeyReferences.AsNoTracking().Where(r => r.OwnerKind == "knowledge" && r.OwnerId == current.KnowledgeId && r.Role == "field").ToListAsync())
            {
                var copy = JsonSerializer.Deserialize<ShPayloadKeyReference>(JsonSerializer.Serialize(reference))!; copy.Id = Guid.NewGuid(); copy.OwnerKind = "version"; copy.OwnerId = original.Id; copy.VersionId = original.Id;
                copy.OriginalItemId = original.OriginalKnowledgeId;
                _db.ShPayloadKeyReferences.Add(copy);
            }
        await _db.SaveChangesAsync();
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var exported = await new SelfHostedArchiveExportService(_db,tenant,_graph,new EphemeralDataProtectionProvider()).ExportAsync(new() { Type = ExportScopeType.Tenant },false,default);
        await using (exported.Stream)
        {
            using var archive = await KnowzArchiveReader.OpenAsync(exported.Stream,new() { ExpectedSignerFingerprint = exported.Receipt.SignerFingerprint, LeaveOpen = true });
            var package = PortableGraphPackages.Combine(archive.Packages); Assert.Equal("complete",archive.Manifest.PreservationState); Assert.Empty(archive.Manifest.Inventory.Omissions);
            Assert.Contains(package.SealedItems!,k => k.Id == current.KnowledgeId); Assert.DoesNotContain(package.Data.KnowledgeItems,k => k.Id == current.KnowledgeId);
            var history = Assert.Single(package.PlainVersions!,v => v.Id == original.Id); Assert.Equal(original.SnapshotJson,history.SnapshotJson); Assert.Equal(original.Content,history.Content); Assert.Equal(current.OriginalItemId,history.OriginalKnowledgeId);
            if (encryptedFields) { var reference = Assert.Single(package.PayloadKeyReferences!,r => r.OwnerKind == "version" && r.OwnerId == history.Id); Assert.Equal(current.OriginalItemId,reference.OriginalItemId); Assert.Equal("knowledge:title",DecryptCurrentField(PlainVersionSnapshot.ReadEncryptedFields(history.SnapshotJson)!)); }

            using var destination = new SelfHostedGraphStoreTests();
            var actor = new User { TenantId = destination._tenant, Username = "mixed-history-importer", Role = Knowz.Core.Enums.UserRole.Admin, IsActive = true };
            destination._db.Users.Add(actor); await destination._db.SaveChangesAsync();
            var targetTenant = Substitute.For<ITenantProvider>(); targetTenant.TenantId.Returns(destination._tenant);
            var adapter = new SelfHostedArchiveImportAdapter(destination._db, targetTenant, destination._graph, new VaultAccessService(destination._db));
            using var coordinator = new ArchiveImportCoordinator(); var targetActor = new ArchiveActor(destination._tenant, actor.Id.ToString("D"));
            exported.Stream.Position = 0;
            var plan = await coordinator.CreatePlanAsync(exported.Stream, targetActor, new() { ExpectedSignerFingerprint = exported.Receipt.SignerFingerprint,
                SourceReceipt = exported.Receipt, Strategy = "overwrite", IdempotencyKey = "mixed-body-history" }, adapter);
            var committed = await coordinator.CommitAsync(plan.PlanId, targetActor, adapter);
            Assert.Equal("complete", committed.PreservationState); Assert.Equal(committed.RequiredPayloadCount, committed.PreservedPayloadCount);
            var mappedOwner = Guid.Parse(plan.Mapping["knowledge:" + current.KnowledgeId.ToString("D")]);
            var mappedVersion = Guid.Parse(plan.Mapping["version:" + original.Id.ToString("D")]);
            var returned = await new SelfHostedArchiveExportService(destination._db, targetTenant, destination._graph, new EphemeralDataProtectionProvider())
                .ExportAsync(new() { Type = ExportScopeType.Tenant }, false, default);
            await using (returned.Stream)
            {
                using var returnArchive = await KnowzArchiveReader.OpenAsync(returned.Stream, new() { ExpectedSignerFingerprint = returned.Receipt.SignerFingerprint });
                var roundTrip = PortableGraphPackages.Combine(returnArchive.Packages);
                Assert.Equal("complete", returnArchive.Manifest.PreservationState);
                Assert.Contains(roundTrip.SealedItems!, k => k.Id == mappedOwner); Assert.DoesNotContain(roundTrip.Data.KnowledgeItems, k => k.Id == mappedOwner);
                var retained = Assert.Single(roundTrip.PlainVersions!, v => v.Id == mappedVersion);
                Assert.Equal(mappedOwner, retained.KnowledgeId); Assert.Equal(original.OriginalKnowledgeId, retained.OriginalKnowledgeId);
                Assert.Equal(original.Content, retained.Content); Assert.Equal(original.SnapshotJson, retained.SnapshotJson);
                if (encryptedFields)
                {
                    var reference = Assert.Single(roundTrip.PayloadKeyReferences!, r => r.OwnerKind == "version" && r.OwnerId == retained.Id);
                    Assert.Equal(original.OriginalKnowledgeId, reference.OriginalItemId);
                    Assert.Equal("knowledge:title", DecryptCurrentField(PlainVersionSnapshot.ReadEncryptedFields(retained.SnapshotJson)!));
                }
            }
        }
    }
}
