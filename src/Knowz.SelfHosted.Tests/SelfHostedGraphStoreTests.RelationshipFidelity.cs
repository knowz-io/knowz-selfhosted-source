using System.Security.Cryptography;
using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed partial class SelfHostedGraphStoreTests
{
    [Theory]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    [InlineData(false, "location")]
    [InlineData(true, "location")]
    [InlineData(false, "event")]
    [InlineData(true, "event")]
    [InlineData(false, "relationship")]
    [InlineData(true, "relationship")]
    public async Task SignedGraphRoundTripPreservesTypedRelationshipArrays(bool sealedOwner, string scenario)
    {
        var (source, _, _) = Fixture();
        var plain = source.Data.KnowledgeItems.Single(); var encrypted = source.SealedItems!.Single();
        plain.PersonLinks = []; plain.LocationLinks = []; plain.EventLinks = []; plain.Relationships = [];
        encrypted.PersonLinks = []; encrypted.LocationLinks = []; encrypted.EventLinks = []; encrypted.Relationships = [];
        var owner = sealedOwner ? encrypted.Id : plain.Id;
        var location = Guid.NewGuid(); var eventId = Guid.NewGuid();
        var rich = new PortableEntityLink { EntityId = scenario == "location" ? location : eventId,
            RelationshipContext = "Exact original contextual relation — 猫", Role = "witness", Mentions = 3, ConfidenceScore = .87 };
        var relation = new PortableKnowledgeRelationship { Id = Guid.NewGuid(), OriginalRelationshipId = Guid.NewGuid(),
            SourceKnowledgeId = owner, TargetKnowledgeId = sealedOwner ? plain.Id : encrypted.Id,
            RelationshipType = Knowz.Core.Enums.KnowledgeRelationshipType.RelatedTo, Confidence = .91,
            PortableMetadataJson = "{\"purpose\":\"corroborates original claim\"}", CreatedAt = DateTime.UtcNow.AddDays(-2), UpdatedAt = DateTime.UtcNow.AddDays(-1) };
        if (scenario == "location")
        {
            source.Data.Locations.Add(new() { Id = location, Name = "Original location" });
            if (sealedOwner) { encrypted.LocationIds = [location]; encrypted.LocationLinks = [rich]; }
            else { plain.LocationIds = [location]; plain.LocationLinks = [rich]; }
        }
        if (scenario == "event")
        {
            source.Data.Events.Add(new() { Id = eventId, Name = "Original event" });
            if (sealedOwner) { encrypted.EventIds = [eventId]; encrypted.EventLinks = [rich]; }
            else { plain.EventIds = [eventId]; plain.EventLinks = [rich]; }
        }
        if (scenario == "relationship")
        {
            if (sealedOwner) encrypted.Relationships = [relation]; else plain.Relationships = [relation];
        }
        Assert.Null(EncryptedGraphProfile.Validate(source));
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var bytes = new MemoryStream();
        var receipt = await KnowzArchiveWriter.WriteAsync(bytes, [source], null, new() { SigningKey = signer, SourceSnapshot = "typed-relations" }); bytes.Position = 0;
        var actor = new User { TenantId = _tenant, Username = "relationship-importer", Role = Knowz.Core.Enums.UserRole.Admin, IsActive = true };
        _db.Users.Add(actor); await _db.SaveChangesAsync();
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var adapter = new SelfHostedArchiveImportAdapter(_db, tenant, _graph, new VaultAccessService(_db));
        using var coordinator = new ArchiveImportCoordinator(); var identity = new ArchiveActor(_tenant, actor.Id.ToString("D"));
        var plan = await coordinator.CreatePlanAsync(bytes, identity, new() { ExpectedSignerFingerprint = receipt.SignerFingerprint,
            SourceReceipt = receipt, Strategy = "overwrite", IdempotencyKey = "typed-relations" }, adapter);
        var committed = await coordinator.CommitAsync(plan.PlanId, identity, adapter);
        Assert.Equal("complete", committed.PreservationState);
        var exported = await new SelfHostedArchiveExportService(_db, tenant, _graph, new EphemeralDataProtectionProvider())
            .ExportAsync(new() { Type = ExportScopeType.Tenant }, false, default);
        await using (exported.Stream)
        {
            using var archive = await KnowzArchiveReader.OpenAsync(exported.Stream, new() { ExpectedSignerFingerprint = exported.Receipt.SignerFingerprint });
            var result = PortableGraphPackages.Combine(archive.Packages); Assert.Equal("complete", archive.Manifest.PreservationState);
            var mappedOwner = Guid.Parse(plan.Mapping["knowledge:" + owner.ToString("D")]);
            var row = sealedOwner ? JsonSerializer.SerializeToElement(result.SealedItems!.Single(k => k.Id == mappedOwner))
                : JsonSerializer.SerializeToElement(result.Data.KnowledgeItems.Single(k => k.Id == mappedOwner));
            if (scenario == "empty")
                foreach (var property in new[] { "PersonLinks", "LocationLinks", "EventLinks", "Relationships" })
                    Assert.Equal("[]", row.GetProperty(property).GetRawText());
            else if (scenario is "location" or "event")
            {
                var property = scenario == "location" ? "LocationLinks" : "EventLinks";
                var links = row.GetProperty(property).Deserialize<List<PortableEntityLink>>();
                var link = Assert.Single(links!); Assert.Equal(Guid.Parse(plan.Mapping[scenario + ":" + rich.EntityId.ToString("D")]), link.EntityId);
                Assert.Equal(rich.RelationshipContext, link.RelationshipContext); Assert.Equal(rich.Role, link.Role);
                Assert.Equal(rich.Mentions, link.Mentions); Assert.Equal(rich.ConfidenceScore, link.ConfidenceScore);
            }
            else
            {
                var actual = Assert.Single(row.GetProperty("Relationships").Deserialize<List<PortableKnowledgeRelationship>>()!);
                Assert.Equal(mappedOwner, actual.SourceKnowledgeId); Assert.Equal(Guid.Parse(plan.Mapping["knowledge:" + relation.TargetKnowledgeId!.Value.ToString("D")]), actual.TargetKnowledgeId);
                Assert.Equal(relation.OriginalRelationshipId, actual.OriginalRelationshipId); Assert.Equal(relation.PortableMetadataJson, actual.PortableMetadataJson);
                Assert.Equal(relation.Confidence, actual.Confidence); Assert.Equal(relation.CreatedAt, actual.CreatedAt); Assert.Equal(relation.UpdatedAt, actual.UpdatedAt);
            }
        }
    }

    [Theory]
    [InlineData("detached")]
    [InlineData("foreign")]
    [InlineData("deleted")]
    [InlineData("substituted")]
    public async Task TypedRelationshipMetadataCannotReattachUnavailableLocation(string corruption)
    {
        var location = new Location { Id = Guid.NewGuid(), TenantId = corruption == "foreign" ? Guid.NewGuid() : _tenant,
            Name = "Unavailable private location", IsDeleted = corruption == "deleted" };
        var row = new Knowledge { TenantId = _tenant, Title = "Allowed item", Content = "Body", PlatformData = JsonSerializer.Serialize(new Dictionary<string,object>
            { ["$knowz.portable.locationLinks"] = new[] { new PortableEntityLink { EntityId = location.Id, RelationshipContext = "Archived private relation" } } }) };
        _db.Locations.Add(location); _db.KnowledgeItems.Add(row);
        if (corruption != "detached")
        {
            var actual = corruption == "substituted" ? new Location { TenantId = _tenant, Name = "Different live location" } : location;
            if (actual != location) _db.Locations.Add(actual);
            _db.KnowledgeLocations.Add(new() { KnowledgeId = row.Id, LocationId = actual.Id });
        }
        await _db.SaveChangesAsync();
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var exporter = new PortableExportService(_db, tenant, _storage, Microsoft.Extensions.Options.Options.Create(new Knowz.Core.Configuration.SelfHostedOptions()), Microsoft.Extensions.Logging.Abstractions.NullLogger<PortableExportService>.Instance);
        await Assert.ThrowsAsync<InvalidDataException>(() => exporter.ExportMetadataAsync(new() { Type = ExportScopeType.Tenant }, true, default));
    }

    [Theory]
    [InlineData(ImportConflictStrategy.Skip)]
    [InlineData(ImportConflictStrategy.Merge)]
    [InlineData(ImportConflictStrategy.Overwrite)]
    public async Task TypedRelationshipConflictStrategyPreservesExistingOrReplacesExactly(ImportConflictStrategy strategy)
    {
        var (source, _, _) = Fixture(); var owner = source.SealedItems!.Single(); var location = Guid.NewGuid();
        source.Data.Locations.Add(new() { Id = location, Name = "Original location" });
        owner.LocationIds = [location]; owner.LocationLinks = [new() { EntityId = location, Role = "original", RelationshipContext = "Original context", Mentions = 2 }];
        Assert.True((await _graph.ImportAsync(source, null, "trusted:relation-strategy", ImportConflictStrategy.Skip, null, default)).Success);
        owner.LocationLinks[0].Role = "changed"; owner.LocationLinks[0].RelationshipContext = "Changed context";
        var added = Guid.NewGuid(); source.Data.Locations.Add(new() { Id = added, Name = "New rich-only location" });
        owner.LocationLinks.Add(new() { EntityId = added, Role = "added", RelationshipContext = "Additional context", Mentions = 7 });
        // Rich-only target must produce a real junction, not merely echo a sidecar.
        Assert.True((await _graph.ImportAsync(source, null, "trusted:relation-strategy", strategy, null, default)).Success);
        var exported = await _graph.ExportAsync(new() { Type = ExportScopeType.Tenant }, false, default);
        var links = exported.SealedItems!.Single().LocationLinks!;
        Assert.Equal(strategy == ImportConflictStrategy.Skip ? 1 : 2, links.Count);
        var original = Assert.Single(links, l => l.Role != "added");
        Assert.Equal(strategy == ImportConflictStrategy.Overwrite ? "changed" : "original", original.Role);
        Assert.Equal(strategy == ImportConflictStrategy.Overwrite ? "Changed context" : "Original context", original.RelationshipContext);
        if (strategy != ImportConflictStrategy.Skip)
        {
            var addition = Assert.Single(links, l => l.Role == "added"); Assert.Equal(7, addition.Mentions);
            Assert.True(await _db.KnowledgeLocations.AnyAsync(l => l.LocationId == addition.EntityId));
        }
    }
}
