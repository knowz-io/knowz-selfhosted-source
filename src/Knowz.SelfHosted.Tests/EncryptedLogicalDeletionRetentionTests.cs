using System.Text.Json;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Tests;

public sealed partial class SelfHostedGraphStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogicalEncryptedDeletionHidesRecordsWithoutDestroyingRetainedEnvelopesOrReparentingReplies(bool deleteItem)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web); json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var package = JsonSerializer.Deserialize<PortableExportPackage>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portable-graph-v1.json")), json)!;
        var result = await _graph.ImportAsync(package, null, "trusted:logical-retention", ImportConflictStrategy.Skip, null, default);
        Assert.True(result.Success, result.Error);
        var item = await _db.ShSealedKnowledge.AsNoTracking().SingleAsync(); var comment = await _db.ShSealedComments.AsNoTracking().FirstAsync();
        var childId = Guid.NewGuid();
        // A legacy retained reply is intentionally opaque to deletion: neither its bytes nor ancestry can be rewritten.
        _db.ShSealedComments.Add(new ShSealedComment { Id = childId, TenantId = _tenant, KnowledgeId = item.KnowledgeId,
            OriginalCommentId = Guid.NewGuid(), OriginalKnowledgeId = item.OriginalItemId, ParentCommentId = comment.Id,
            OriginalParentCommentId = comment.OriginalCommentId, Ciphertext = "retained opaque legacy reply", CryptoBindingId = comment.CryptoBindingId });
        await _db.SaveChangesAsync();
        var itemCipher = item.Ciphertext; var commentCipher = comment.Ciphertext;
        var versionCount = await _db.ShSealedVersions.CountAsync(); Assert.True(versionCount > 0);
        var referenceCount = await _db.ShPayloadKeyReferences.CountAsync();
        var files = await _db.ShSealedFiles.AsNoTracking().ToListAsync(); Assert.NotEmpty(files);
        var bytesBefore = new Dictionary<Guid, byte[]>();
        foreach (var file in files) { await using var stream = await _graph.OpenFileAsync(file.Id, default); using var copy = new MemoryStream(); await stream.CopyToAsync(copy); bytesBefore[file.Id] = copy.ToArray(); }
        var deleted = deleteItem
            ? await _lifecycle.DeleteAsync(item.KnowledgeId, default, (await _lifecycle.GetAsync(item.KnowledgeId, default))!.Revision)
            : await _lifecycle.DeleteCommentAsync(comment.Id, default, SelfHostedSealedStore.PayloadRevision((await _lifecycle.GetCommentAsync(comment.Id, default))!.Envelope));
        Assert.True(deleted.Ok, deleted.Error); _db.ChangeTracker.Clear();
        Assert.Equal(itemCipher, (await _db.ShSealedKnowledge.IgnoreQueryFilters().SingleAsync()).Ciphertext);
        Assert.Equal(commentCipher, (await _db.ShSealedComments.IgnoreQueryFilters().SingleAsync(c => c.Id == comment.Id)).Ciphertext);
        Assert.True((await _db.ShSealedComments.IgnoreQueryFilters().SingleAsync(c => c.Id == comment.Id)).IsDeleted);
        var retainedChild = await _db.ShSealedComments.IgnoreQueryFilters().SingleAsync(c => c.Id == childId);
        Assert.Equal(comment.Id, retainedChild.ParentCommentId); Assert.Equal(comment.OriginalCommentId, retainedChild.OriginalParentCommentId);
        Assert.Equal("retained opaque legacy reply", retainedChild.Ciphertext);
        Assert.Equal(versionCount, await _db.ShSealedVersions.IgnoreQueryFilters().CountAsync());
        Assert.Equal(referenceCount, await _db.ShPayloadKeyReferences.IgnoreQueryFilters().CountAsync());
        Assert.Null(await _lifecycle.GetCommentAsync(comment.Id, default)); Assert.Null(await _lifecycle.GetCommentAsync(childId, default));
        if (deleteItem) Assert.Null(await _lifecycle.GetAsync(item.KnowledgeId, default));
        else Assert.NotNull(await _lifecycle.GetAsync(item.KnowledgeId, default));
        foreach (var file in files)
        {
            var retainedFile = await _db.ShSealedFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == file.Id);
            Assert.Equal(file.BinarySha256, retainedFile.BinarySha256);
            if (retainedFile.BlobStorageId.HasValue)
            { await using var stream = (await _storage.DownloadAsync(_tenant, retainedFile.BlobStorageId.Value, default)).stream; using var copy = new MemoryStream(); await stream.CopyToAsync(copy); Assert.Equal(bytesBefore[file.Id], copy.ToArray()); }
        }
    }
}
