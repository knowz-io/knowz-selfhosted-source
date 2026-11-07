using System.Security.Cryptography;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Microsoft.AspNetCore.DataProtection;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed partial class SelfHostedGraphStoreTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(42L)]
    public async Task SignedArchiveRetainsNullableOriginalFileSize(long? originalSize)
    {
        var (source, _, _) = Fixture(); source.SealedFiles!.Single().OriginalSizeBytes = originalSize;
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var input = new MemoryStream();
        var receipt = await KnowzArchiveWriter.WriteAsync(input, [source], null, new() { SigningKey = signer, SourceSnapshot = "original-size" }); input.Position = 0;
        using var archive = await KnowzArchiveReader.OpenAsync(input, new() { ExpectedSignerFingerprint = receipt.SignerFingerprint });
        var imported = await _graph.ImportAsync(PortableGraphPackages.Combine(archive.Packages), null, "trusted:original-size", ImportConflictStrategy.Overwrite, archive.OpenBinaryAsync, default);
        Assert.True(imported.Success, imported.Error);
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var output = await new SelfHostedArchiveExportService(_db, tenant, _graph, new EphemeralDataProtectionProvider()).ExportAsync(new() { Type = ExportScopeType.Tenant }, false, default);
        await using (output.Stream)
        {
            using var returned = await KnowzArchiveReader.OpenAsync(output.Stream, new() { ExpectedSignerFingerprint = output.Receipt.SignerFingerprint });
            Assert.Equal(originalSize, Assert.Single(PortableGraphPackages.Combine(returned.Packages).SealedFiles!).OriginalSizeBytes);
        }
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(67108865L)]
    public async Task InvalidOriginalFileSizeFailsBeforeGraphMutation(long originalSize)
    {
        var (source, _, _) = Fixture(); source.SealedFiles!.Single().OriginalSizeBytes = originalSize;
        var imported = await _graph.ImportAsync(source, null, "trusted:invalid-original-size", ImportConflictStrategy.Overwrite, null, default);
        Assert.False(imported.Success); Assert.Empty(_db.ShSealedFiles); Assert.Empty(_db.KnowledgeItems);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileSizeMetadataChangesRequireBinaryReplacementAndPersistExactNewValue(bool replaceBinary)
    {
        var (source, _, _) = Fixture();
        Assert.True((await _graph.ImportAsync(source, null, "trusted:size-edit", ImportConflictStrategy.Skip, null, default)).Success);
        var graph = await _graph.ExportAsync(new() { Type = ExportScopeType.Tenant }, false, default);
        var vault = graph.Data.Vaults.Single().Id; var catalog = (await _lifecycle.GetCatalogAsync(vault, default))!;
        Assert.True((await _lifecycle.SelectPrimaryAsync(vault, catalog.Contexts.Single().ContextId, catalog.Keys.Single().Fingerprint, catalog.Revision, default)).Ok);
        var file = graph.SealedFiles!.Single(); file.BinaryEntryPath = null;
        var revision = SelfHostedSealedStore.PayloadRevision(file); file.OriginalSizeBytes = 42;
        var write = new SealedFileWrite { VaultId = vault, Record = file, PayloadKeyReferences = graph.PayloadKeyReferences!.Where(r => r.OwnerKind == "file").ToList() };
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var files = new SelfHostedSealedFiles(_db, tenant, _lifecycle, _storage);
        using var bytes = replaceBinary ? new MemoryStream(Convert.FromBase64String(source.SealedFiles!.Single().BinaryContentBase64!)) : null;
        var result = await files.UpdateAsync(Guid.NewGuid(), write, revision, bytes, default);
        if (!replaceBinary) { Assert.False(result.Ok); Assert.Equal(400, result.Status); }
        else
        {
            Assert.True(result.Ok, result.Error); Assert.Equal(42, result.Write!.Record.OriginalSizeBytes);
            var exported = await _graph.ExportAsync(new() { Type = ExportScopeType.Tenant }, false, default);
            Assert.Equal(42, Assert.Single(exported.SealedFiles!, f => f.IsCurrentVersion).OriginalSizeBytes);
            Assert.Null(Assert.Single(exported.SealedFiles!, f => !f.IsCurrentVersion).OriginalSizeBytes);
        }
    }

    [Theory]
    [InlineData(0L, true)]
    [InlineData(-1L, false)]
    [InlineData(67108865L, false)]
    public async Task OriginalFileSizeStagedUploadPreservesZeroAndRejectsInvalidBounds(long originalSize, bool valid)
    {
        var (source, _, _) = Fixture();
        Assert.True((await _graph.ImportAsync(source, null, "trusted:size-stage", ImportConflictStrategy.Skip, null, default)).Success);
        var graph = await _graph.ExportAsync(new() { Type = ExportScopeType.Tenant }, false, default);
        var vault = graph.Data.Vaults.Single().Id; var catalog = (await _lifecycle.GetCatalogAsync(vault, default))!;
        Assert.True((await _lifecycle.SelectPrimaryAsync(vault, catalog.Contexts.Single().ContextId, catalog.Keys.Single().Fingerprint, catalog.Revision, default)).Ok);
        catalog = (await _lifecycle.GetCatalogAsync(vault, default))!;
        var file = graph.SealedFiles!.Single(); file.Id = Guid.NewGuid(); file.OriginalFileId = file.Id; file.OriginalSizeBytes = originalSize;
        file.KnowledgeId = graph.SealedItems!.Single().Id; file.BinaryEntryPath = null; file.BinaryContentBase64 = null;
        var references = graph.PayloadKeyReferences!.Where(r => r.OwnerKind == "file").ToList(); foreach (var r in references) { r.OwnerId = file.Id; r.OriginalItemId = file.Id; }
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant); var files = new SelfHostedSealedFiles(_db, tenant, _lifecycle, _storage);
        var actor = Guid.NewGuid(); var write = new SealedFileWrite { VaultId = vault, Record = file, PayloadKeyReferences = references };
        var stage = await files.StageAsync(actor, write, catalog.Revision, "original-size-stage", default);
        if (!valid) { Assert.False(stage.Ok); Assert.Equal(400, stage.Status); Assert.Empty(_db.ShSealedFileStages); return; }
        Assert.True(stage.Ok, stage.Error);
        using var binary = new MemoryStream(Convert.FromBase64String(source.SealedFiles!.Single().BinaryContentBase64!));
        var saved = await files.UploadAsync(actor, file.Id, binary, stage.Revision!, file.BinarySha256!, default); Assert.True(saved.Ok, saved.Error);
        Assert.Equal(originalSize, saved.Write!.Record.OriginalSizeBytes);
        var exported = await _graph.ExportAsync(new() { Type = ExportScopeType.Tenant }, false, default);
        Assert.Equal(originalSize, Assert.Single(exported.SealedFiles!, f => f.Id == file.Id).OriginalSizeBytes);
    }
}
