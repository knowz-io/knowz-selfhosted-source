using System.Text.Json;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed partial class SelfHostedGraphStoreTests
{
    [Fact]
    public async Task ProtectedFieldFileDeletionRetainsExactReferencesAndCommittedPhysicalBytes()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web); json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var source = JsonSerializer.Deserialize<PortableExportPackage>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portable-graph-v1.json")), json)!;
        var file = source.SealedFiles!.Single();
        source.Data.FileRecords.Add(new() { Id = file.Id, OriginalFileId = file.OriginalFileId, FileName = "encrypted-file", ContentType = "application/octet-stream", SizeBytes = file.SizeBytes, BinaryContentBase64 = file.BinaryContentBase64, EncryptedFieldsJson = file.EncryptedFieldsJson, Attachments = [new() { KnowledgeId = source.SealedItems!.Single().Id }] });
        source.SealedFiles.Clear(); source.PayloadKeyReferences!.RemoveAll(r => r.OwnerKind == "file" && r.Role == "binary");
        Assert.Null(EncryptedGraphProfile.Validate(source)); Assert.True((await _graph.ImportAsync(source, null, "trusted:protected-delete-retention", ImportConflictStrategy.Skip, null, default)).Success);
        var stored = await _db.FileRecords.AsNoTracking().SingleAsync();
        var references = await _db.ShPayloadKeyReferences.CountAsync(); var pointers = await _db.ShFileObjectReferences.AsNoTracking().ToListAsync();
        var blob = pointers.SingleOrDefault(p => p.FileRecordId == stored.Id)?.BlobStorageId ?? stored.Id;
        await using var before = (await _storage.DownloadAsync(_tenant, blob, default)).stream; using var captured = new MemoryStream(); await before.CopyToAsync(captured); var bytes = captured.ToArray();
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var service = new SelfHostedSealedFiles(_db, tenant, _lifecycle, _storage);
        var record = (await SelfHostedSealedFiles.GetProtectedFileAsync(_db, stored.Id, default))!;
        Assert.True((await service.DeleteFileAsync(stored.Id, SelfHostedSealedStore.PayloadRevision(record), default)).Ok); _db.ChangeTracker.Clear();
        Assert.True((await _db.FileRecords.IgnoreQueryFilters().SingleAsync()).IsDeleted);
        Assert.Equal(stored.EncryptedFieldsJson, (await _db.FileRecords.IgnoreQueryFilters().SingleAsync()).EncryptedFieldsJson);
        Assert.Equal(references, await _db.ShPayloadKeyReferences.IgnoreQueryFilters().CountAsync());
        Assert.Equal(pointers.Count, await _db.ShFileObjectReferences.CountAsync()); Assert.Empty(await _db.ShSealedFileStages.ToListAsync());
        Assert.True(await _storage.ExistsAsync(_tenant, blob));
        await using var retained = (await _storage.DownloadAsync(_tenant, blob, default)).stream; using var after = new MemoryStream(); await retained.CopyToAsync(after); Assert.Equal(bytes, after.ToArray());
    }
}
