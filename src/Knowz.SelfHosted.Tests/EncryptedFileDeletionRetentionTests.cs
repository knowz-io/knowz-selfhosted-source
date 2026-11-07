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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EncryptedFileOrAttachmentDeletionRetainsOriginalBytesAndExactReferences(bool attachmentOnly)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web); json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var source = JsonSerializer.Deserialize<PortableExportPackage>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portable-graph-v1.json")), json)!;
        Assert.True((await _graph.ImportAsync(source, null, "trusted:file-delete-retention", ImportConflictStrategy.Skip, null, default)).Success);
        var file = await _db.ShSealedFiles.AsNoTracking().SingleAsync(); var attachment = await _db.ShSealedAttachments.AsNoTracking().FirstAsync();
        var referenceCount = await _db.ShPayloadKeyReferences.IgnoreQueryFilters().CountAsync();
        await using var before = await _graph.OpenFileAsync(file.Id, default); using var clear = new MemoryStream(); await before.CopyToAsync(clear); var ciphertext = clear.ToArray();
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var files = new SelfHostedSealedFiles(_db, tenant, _lifecycle, _storage);
        var deleted = attachmentOnly
            ? await files.DeleteAttachmentAsync(attachment.Id, SelfHostedSealedStore.PayloadRevision(JsonSerializer.Deserialize<SealedAttachmentRecord>(JsonSerializer.Serialize(attachment))!), default)
            : await files.DeleteFileAsync(file.Id, SelfHostedSealedStore.PayloadRevision(JsonSerializer.Deserialize<SealedFileRecord>(JsonSerializer.Serialize(file))!), default);
        Assert.True(deleted.Ok, deleted.Error); _db.ChangeTracker.Clear();
        var retained = await _db.ShSealedFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == file.Id);
        Assert.Equal(file.BinarySha256, retained.BinarySha256);
        Assert.Equal(referenceCount, await _db.ShPayloadKeyReferences.IgnoreQueryFilters().CountAsync());
        Assert.True((await _db.ShSealedAttachments.IgnoreQueryFilters().SingleAsync(a => a.Id == attachment.Id)).IsDeleted);
        if (attachmentOnly) Assert.False(retained.IsDeleted); else Assert.True(retained.IsDeleted);
        Assert.True(retained.BlobStorageId.HasValue);
        await using var kept = (await _storage.DownloadAsync(_tenant, retained.BlobStorageId.Value, default)).stream;
        using var after = new MemoryStream(); await kept.CopyToAsync(after); Assert.Equal(ciphertext, after.ToArray());
    }
}
