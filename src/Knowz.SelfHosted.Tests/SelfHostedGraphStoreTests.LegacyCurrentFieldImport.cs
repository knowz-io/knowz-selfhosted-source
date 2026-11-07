using System.Text.Json;
using System.Text.Json.Nodes;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed partial class SelfHostedGraphStoreTests
{
    [Theory]
    [InlineData(ImportConflictStrategy.Skip, false)]
    [InlineData(ImportConflictStrategy.Merge, false)]
    [InlineData(ImportConflictStrategy.Overwrite, false)]
    [InlineData(ImportConflictStrategy.Skip, true)]
    [InlineData(ImportConflictStrategy.Merge, true)]
    [InlineData(ImportConflictStrategy.Overwrite, true)]
    public async Task LegacyCurrentFieldOmissionNeverClearsOrExposesProtectedOwner(ImportConflictStrategy strategy, bool selectedVault)
    {
        var source = await LegacyOmissionFixtureAsync();
        Assert.True((await _graph.ImportAsync(source, null, "trusted:legacy-omission", ImportConflictStrategy.Skip, null, default)).Success);
        var before = await _db.KnowledgeItems.AsNoTracking().SingleAsync();
        var vault = (await _db.KnowledgeVaults.SingleAsync()).VaultId;
        var references = JsonSerializer.Serialize(await _db.ShPayloadKeyReferences.AsNoTracking().OrderBy(r => r.Id).ToListAsync());
        var history = JsonSerializer.Serialize(await _db.KnowledgeVersions.AsNoTracking().ToListAsync());
        var package = LegacyOmissionPackage(before.Id, vault); var service = LegacyImporter();
        var result = selectedVault ? await service.ImportAsync(package, vault, strategy) : await service.ImportAsync(package, strategy);
        if (strategy == ImportConflictStrategy.Skip)
        {
            Assert.True(result.Success, result.Error); Assert.Equal(1, result.KnowledgeItems.Skipped);
            Assert.Equal(0, result.KnowledgeItems.Overwritten + result.KnowledgeItems.Merged);
        }
        else
        {
            Assert.False(result.Success); Assert.Contains("protected", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("archive", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(await _db.Topics.AnyAsync(t => t.Id == package.Data.Topics.Single().Id));
            Assert.Equal(0, result.KnowledgeItems.Total);
        }
        var after = await _db.KnowledgeItems.AsNoTracking().SingleAsync();
        Assert.Equal(before.Title, after.Title); Assert.Equal(before.Content, after.Content);
        Assert.Equal(before.Summary, after.Summary); Assert.Equal(before.BriefSummary, after.BriefSummary);
        Assert.Equal(before.EncryptedFieldsJson, after.EncryptedFieldsJson); Assert.Equal(before.OriginalItemId, after.OriginalItemId);
        Assert.Equal(references, JsonSerializer.Serialize(await _db.ShPayloadKeyReferences.AsNoTracking().OrderBy(r => r.Id).ToListAsync()));
        Assert.Equal(history, JsonSerializer.Serialize(await _db.KnowledgeVersions.AsNoTracking().ToListAsync()));
        Assert.Equal("knowledge:title", DecryptCurrentField(after.EncryptedFieldsJson!));
    }

    [Theory]
    [InlineData(ImportConflictStrategy.Skip)]
    [InlineData(ImportConflictStrategy.Merge)]
    [InlineData(ImportConflictStrategy.Overwrite)]
    public async Task LegacyCurrentFieldOmissionCannotRetargetExistingOwnerToUnrelatedVault(ImportConflictStrategy strategy)
    {
        var source = await LegacyOmissionFixtureAsync(); Assert.True((await _graph.ImportAsync(source, null, "trusted:legacy-unrelated", ImportConflictStrategy.Skip, null, default)).Success);
        var owner = await _db.KnowledgeItems.AsNoTracking().SingleAsync(); var originalVault = (await _db.KnowledgeVaults.SingleAsync()).VaultId;
        var target = new Vault { TenantId = _tenant, Name = "unrelated destination" }; _db.Vaults.Add(target); await _db.SaveChangesAsync();
        var package = LegacyOmissionPackage(owner.Id, target.Id);
        var result = await LegacyImporter().ImportAsync(package, target.Id, strategy);
        Assert.False(result.Success); Assert.Contains("unavailable", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(await _db.Topics.AnyAsync(t => t.Id == package.Data.Topics.Single().Id));
        Assert.Equal(originalVault, (await _db.KnowledgeVaults.SingleAsync()).VaultId);
        Assert.Equal(owner.EncryptedFieldsJson, (await _db.KnowledgeItems.AsNoTracking().SingleAsync()).EncryptedFieldsJson);
    }

    [Theory]
    [InlineData(ImportConflictStrategy.Skip)]
    [InlineData(ImportConflictStrategy.Merge)]
    [InlineData(ImportConflictStrategy.Overwrite)]
    public async Task LegacyCurrentFieldOmissionDoesNotBlockUnrelatedOrdinaryImports(ImportConflictStrategy strategy)
    {
        var source = await LegacyOmissionFixtureAsync(); Assert.True((await _graph.ImportAsync(source, null, "trusted:legacy-unrelated-control", ImportConflictStrategy.Skip, null, default)).Success);
        var owner = await _db.KnowledgeItems.AsNoTracking().SingleAsync(); var vault = (await _db.KnowledgeVaults.SingleAsync()).VaultId;
        var incoming = LegacyOmissionPackage(Guid.NewGuid(), vault);
        var result = await LegacyImporter().ImportAsync(incoming, vault, strategy);
        Assert.True(result.Success, result.Error); Assert.Equal(1, result.KnowledgeItems.Created);
        Assert.Equal(owner.EncryptedFieldsJson, (await _db.KnowledgeItems.AsNoTracking().SingleAsync(k => k.Id == owner.Id)).EncryptedFieldsJson);
        Assert.Equal("Incoming ordinary body", (await _db.KnowledgeItems.SingleAsync(k => k.Id == incoming.Data.KnowledgeItems.Single().Id)).Content);
    }

    [Theory]
    [InlineData(ImportConflictStrategy.Skip)]
    [InlineData(ImportConflictStrategy.Merge)]
    [InlineData(ImportConflictStrategy.Overwrite)]
    public async Task LegacyCurrentFieldOmissionForeignIdFailsBeforeAnyWrites(ImportConflictStrategy strategy)
    {
        var foreign = new Knowledge { TenantId = Guid.NewGuid(), Title = "Foreign protected owner", Content = "Foreign body", EncryptedFieldsJson = "retained opaque foreign ciphertext", OriginalItemId = Guid.NewGuid() };
        var target = new Vault { TenantId = _tenant, Name = "local destination" }; _db.KnowledgeItems.Add(foreign); _db.Vaults.Add(target); await _db.SaveChangesAsync(); _db.ChangeTracker.Clear();
        var package = LegacyOmissionPackage(foreign.Id, target.Id);
        var result = await LegacyImporter().ImportAsync(package, target.Id, strategy);
        Assert.False(result.Success); Assert.Contains("unavailable", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(await _db.Topics.AnyAsync(t => t.Id == package.Data.Topics.Single().Id));
        var retained = await _db.KnowledgeItems.IgnoreQueryFilters().AsNoTracking().SingleAsync(k => k.Id == foreign.Id);
        Assert.Equal(foreign.TenantId, retained.TenantId); Assert.Equal(foreign.EncryptedFieldsJson, retained.EncryptedFieldsJson); Assert.Equal(foreign.Content, retained.Content);
    }

    [Theory]
    [InlineData(ImportConflictStrategy.Skip, false)]
    [InlineData(ImportConflictStrategy.Merge, false)]
    [InlineData(ImportConflictStrategy.Overwrite, false)]
    [InlineData(ImportConflictStrategy.Overwrite, true)]
    public async Task LegacyRetainedSidecarCannotExposeProtectedKnowledgeCompanions(ImportConflictStrategy strategy, bool canonical)
    {
        var source = await LegacyOmissionFixtureAsync(); Assert.True((await _graph.ImportAsync(source,null,"trusted:legacy-companion",ImportConflictStrategy.Skip,null,default)).Success);
        var before = await _db.KnowledgeItems.AsNoTracking().SingleAsync(); var vault = (await _db.KnowledgeVaults.SingleAsync()).VaultId;
        var refs = JsonSerializer.Serialize(await _db.ShPayloadKeyReferences.AsNoTracking().OrderBy(r=>r.Id).ToListAsync());
        var package = LegacyOmissionPackage(before.Id,vault); var incoming = package.Data.KnowledgeItems.Single();
        incoming.EncryptedFieldsJson = before.EncryptedFieldsJson; incoming.OriginalItemId = before.OriginalItemId;
        if (canonical) { incoming.Title = before.Title; incoming.Summary = before.Summary; }
        var result = await LegacyImporter().ImportAsync(package,strategy);
        if (canonical || strategy == ImportConflictStrategy.Skip) Assert.True(result.Success,result.Error);
        else { Assert.False(result.Success); Assert.Contains("protected",result.Error,StringComparison.OrdinalIgnoreCase); Assert.False(await _db.Topics.AnyAsync(t=>t.Id==package.Data.Topics.Single().Id)); }
        var current = await _db.KnowledgeItems.AsNoTracking().SingleAsync(); Assert.Equal(before.Title,current.Title); Assert.Equal(before.Summary,current.Summary); Assert.Equal(before.EncryptedFieldsJson,current.EncryptedFieldsJson);
        Assert.Equal(refs,JsonSerializer.Serialize(await _db.ShPayloadKeyReferences.AsNoTracking().OrderBy(r=>r.Id).ToListAsync()));
    }

    private PortableImportService LegacyImporter()
    {
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        return new(_db, tenant, _storage, NullLogger<PortableImportService>.Instance);
    }

    private static PortableExportPackage LegacyOmissionPackage(Guid itemId, Guid vaultId) => new()
    {
        SchemaVersion = Knowz.Core.Schema.CoreSchema.Version, SourceEdition = "selfhosted", SourceTenantId = Guid.NewGuid(),
        Data = new()
        {
            KnowledgeItems = [new() { Id = itemId, Title = "MUST_NOT_REPLACE_PROTECTED_TITLE", Content = "Incoming ordinary body", Summary = "MUST_NOT_FILL_PROTECTED_SUMMARY", BriefSummary = "Incoming brief", VaultIds = [vaultId] }],
            Topics = [new() { Id = Guid.NewGuid(), Name = "sentinel-before-knowledge-write" }]
        }
    };

    private static async Task<PortableExportPackage> LegacyOmissionFixtureAsync()
    {
        var source = await FieldHistoryFixtureAsync(); var item = source.Data.KnowledgeItems.Single();
        var fields = JsonNode.Parse(item.EncryptedFieldsJson!)!; fields["fields"]!["summary"] = fields["fields"]!["title"]!.DeepClone(); item.EncryptedFieldsJson = fields.ToJsonString();
        var reference = JsonSerializer.Deserialize<PortablePayloadKeyReference>(JsonSerializer.Serialize(source.PayloadKeyReferences!.Single(r => r.OwnerKind == "knowledge")))!;
        reference.FieldName = "summary"; source.PayloadKeyReferences!.Add(reference);
        Assert.Null(EncryptedGraphProfile.Validate(source)); return source;
    }
}
