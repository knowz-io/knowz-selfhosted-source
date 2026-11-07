using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Application.DTOs;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;
public sealed partial class SelfHostedGraphStoreTests
{
    [Theory]
    [InlineData("unchanged-no-primary", true)]
    [InlineData("unchanged-retired", true)]
    [InlineData("unchanged-active-primary", true)]
    [InlineData("changed-active-primary", true)]
    [InlineData("same-cipher-generation-substitution", false)]
    [InlineData("partial-omission", false)]
    [InlineData("changed-no-primary", false)]
    [InlineData("changed-retired", false)]
    [InlineData("unknown-prior", false)]
    [InlineData("omitted-fields", false)]
    [InlineData("revoked-owner", false)]
    public async Task OrdinaryReplacementPreservesOnlyExactlyAttributedUnchangedProtectedFields(string scenario, bool succeeds)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web); json.Converters.Add(new JsonStringEnumConverter());
        var source = JsonSerializer.Deserialize<PortableExportPackage>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portable-graph-v1.json")), json)!;
        var original = source.SealedFiles!.Single();
        source.Data.FileRecords.Add(new() { Id = original.Id, OriginalFileId = original.OriginalFileId, FileName = "encrypted-file", ContentType = "application/octet-stream",
            SizeBytes = 3, BinaryContentBase64 = "AQID", EncryptedFieldsJson = original.EncryptedFieldsJson, Attachments = [new() { KnowledgeId = source.SealedItems!.Single().Id }] });
        if (scenario == "partial-omission")
        {
            var fields = JsonNode.Parse(source.Data.FileRecords.Single().EncryptedFieldsJson!)!;
            fields["fields"]!["description"] = fields["fields"]!["fileName"]!.DeepClone();
            source.Data.FileRecords.Single().EncryptedFieldsJson = fields.ToJsonString();
            var reference = JsonSerializer.Deserialize<PortablePayloadKeyReference>(JsonSerializer.Serialize(source.PayloadKeyReferences!.Single(r => r.OwnerKind == "file" && r.Role == "field"), json), json)!;
            reference.FieldName = "description"; source.PayloadKeyReferences!.Add(reference);
        }
        source.SealedFiles.Clear(); source.PayloadKeyReferences!.RemoveAll(r => r.OwnerKind == "file" && r.Role == "binary");
        var imported = await _graph.ImportAsync(source, null, "trusted:protected-continuity", ImportConflictStrategy.Skip, null, default); Assert.True(imported.Success, imported.Error);
        var vault = (await _db.Vaults.SingleAsync()).Id; var file = await _db.FileRecords.SingleAsync();
        var projection = (await SelfHostedSealedFiles.GetProtectedFileAsync(_db, file.Id, default))!; var revision = SelfHostedSealedStore.PayloadRevision(projection);
        var exactFields = projection.EncryptedFieldsJson; var oldRefs = (await _db.ShPayloadKeyReferences.AsNoTracking().Where(r => r.OwnerKind == "file").ToListAsync()).Select(PortablePublicProjection.Reference).ToList();
        var refs = JsonSerializer.Deserialize<List<PortablePayloadKeyReference>>(JsonSerializer.Serialize(oldRefs, json), json)!;
        var catalog = (await _lifecycle.GetCatalogAsync(vault, default))!; var key = Assert.Single(catalog.Keys);
        if (scenario is "unchanged-active-primary" or "changed-active-primary" or "revoked-owner" or "partial-omission") Assert.True((await _lifecycle.SelectPrimaryAsync(vault, key.ContextId, key.Fingerprint, catalog.Revision, default, key.Generation)).Ok);
        if (scenario.Contains("retired")) { var stored = await _db.ShCryptoKeyIdentities.SingleAsync(); stored.SourceLifecycle = "retired"; stored.Lifecycle = "decrypt-only"; await _db.SaveChangesAsync(); }
        if (scenario == "unknown-prior") { _db.ShPayloadKeyReferences.RemoveRange(await _db.ShPayloadKeyReferences.Where(r => r.OwnerKind == "file").ToListAsync()); await _db.SaveChangesAsync(); }
        if (scenario.StartsWith("changed")) projection.EncryptedFieldsJson = EncryptProtectedFileName("replacement protected filename", key.PublicKey);
        if (scenario == "same-cipher-generation-substitution")
        {
            var stored = await _db.ShCryptoKeyIdentities.SingleAsync();
            _db.ShCryptoKeyIdentities.Add(new() { Id = Guid.NewGuid(), TenantId = _tenant, PortableCryptoContextId = stored.PortableCryptoContextId, Fingerprint = stored.Fingerprint, PublicKey = stored.PublicKey, KeyType = stored.KeyType, Generation = stored.Generation + 1, Lifecycle = stored.Lifecycle, SourceLifecycle = stored.SourceLifecycle, Custody = stored.Custody });
            await _db.SaveChangesAsync(); catalog = (await _lifecycle.GetCatalogAsync(vault, default))!;
            Assert.True((await _lifecycle.SelectPrimaryAsync(vault, key.ContextId, key.Fingerprint, catalog.Revision, default, key.Generation + 1)).Ok);
            refs.Single().Generation++;
        }
        if (scenario == "partial-omission") { var fields = JsonNode.Parse(projection.EncryptedFieldsJson!)!; fields["fields"]!.AsObject().Remove("description"); projection.EncryptedFieldsJson = fields.ToJsonString(); refs.RemoveAll(r => r.FieldName == "description"); }
        if (scenario == "omitted-fields") { projection.EncryptedFieldsJson = null; refs.Clear(); }
        var replacement = new byte[] { 8, 7, 6, 5 }; projection.SizeBytes = replacement.Length; projection.BinarySha256 = ArchivePeerProof.Fingerprint(replacement);
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant); var service = new SelfHostedSealedFiles(_db, tenant, _lifecycle, _storage);
        var result = await service.UpdateProtectedFileAsync(Guid.NewGuid(), new() { VaultId = vault, Record = projection, PayloadKeyReferences = refs }, revision, new MemoryStream(replacement), default, _ => Task.FromResult(scenario != "revoked-owner"));
        Assert.Equal(succeeds, result.Ok);
        if (succeeds)
        {
            var next = (await SelfHostedSealedFiles.GetProtectedFileAsync(_db, result.Id!.Value, default))!; Assert.Equal(projection.EncryptedFieldsJson, next.EncryptedFieldsJson); Assert.Equal(file.Id, next.PreviousFileRecordId); Assert.Equal(2, next.FileVersion);
            var copied = (await _db.ShPayloadKeyReferences.AsNoTracking().Where(r => r.OwnerKind == "file" && r.OwnerId == next.Id).ToListAsync()).Select(PortablePublicProjection.Reference).ToList();
            foreach (var r in copied) r.OwnerId = file.Id; Assert.Equal(JsonSerializer.Serialize(oldRefs,json), JsonSerializer.Serialize(copied,json));
            await using var bytes = await _graph.OpenFileAsync(next.Id, default); using var output = new MemoryStream(); await bytes.CopyToAsync(output); Assert.Equal(replacement, output.ToArray());
        }
        else { Assert.Equal(scenario == "revoked-owner" ? 403 : 400, result.StatusCode); Assert.Single(await _db.FileRecords.ToListAsync()); }
        Assert.Equal(exactFields, (await SelfHostedSealedFiles.GetProtectedFileAsync(_db, file.Id, default))!.EncryptedFieldsJson);
        await using var retained = await _graph.OpenFileAsync(file.Id, default); using var old = new MemoryStream(); await retained.CopyToAsync(old); Assert.Equal(new byte[] { 1, 2, 3 }, old.ToArray());
    }
    internal static string EncryptProtectedFileName(string text, string publicKey)
    {
        var fields = JsonNode.Parse(EncryptCurrentTitle(text, publicKey))!;
        Assert.Equal(text, DecryptCurrentField(fields.ToJsonString()));
        var value = fields["fields"]!["title"]!.DeepClone(); fields["fields"]!.AsObject().Remove("title"); fields["fields"]!["fileName"] = value;
        return fields.ToJsonString();
    }
    internal static string DecryptProtectedFileName(string fieldsJson)
    {
        var fields = JsonNode.Parse(fieldsJson)!; var value = fields["fields"]!["fileName"]!.DeepClone();
        fields["fields"]!.AsObject().Remove("fileName"); fields["fields"]!["title"] = value;
        return DecryptCurrentField(fields.ToJsonString());
    }

}
