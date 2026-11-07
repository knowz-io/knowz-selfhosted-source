using Knowz.Core.Portability;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Application.Services;

public sealed class SealedAttachmentWrite
{
    public Guid VaultId { get; set; }
    public SealedAttachmentRecord Record { get; set; } = new();
    public List<PortablePayloadKeyReference> PayloadKeyReferences { get; set; } = [];
}

public sealed partial class SelfHostedSealedFiles
{
    public async Task<SealedStoreResult> UpdateAttachmentAsync(SealedAttachmentWrite write, string expectedRevision, CancellationToken ct)
    {
        var row = await db.ShSealedAttachments.SingleOrDefaultAsync(a => a.Id == write.Record.Id, ct);
        if (row == null) return SealedStoreResult.Fail(404, "Attachment unavailable.");
        if (await SelfHostedInactiveShelf.IsOwnerInactiveAsync(db, row.KnowledgeId, row.CommentId, ct)) return SealedStoreResult.Fail(409, SelfHostedInactiveShelf.ReadOnlyMessage);
        var record = write.Record;
        if (SelfHostedSealedStore.PayloadRevision(Copy<SealedAttachmentRecord>(row)) != expectedRevision.Trim('"')) return SealedStoreResult.Fail(409, "Attachment changed; reload before editing.");
        if (row.OriginalAttachmentId != record.OriginalAttachmentId || row.FileRecordId != record.FileRecordId || row.KnowledgeId != record.KnowledgeId || row.CommentId != record.CommentId || row.InboxId != record.InboxId) return SealedStoreResult.Fail(400, "Immutable attachment identity and owners cannot change.");
        if (write.PayloadKeyReferences.Any(r => r.OwnerKind != "attachment" || r.OwnerId != row.Id)) return SealedStoreResult.Fail(400, "Attachment references another payload owner.");
        var catalog = await lifecycle.GetCatalogAsync(write.VaultId, ct); if (catalog == null) return SealedStoreResult.Fail(404, "Vault unavailable.");
        var package = SelfHostedSealedStore.MutationPackage(catalog, write.PayloadKeyReferences); package.SealedAttachments = [record]; package.Data.FileRecords = [new() { Id = row.FileRecordId }];
        if (row.KnowledgeId.HasValue) package.Data.KnowledgeItems.Add(new() { Id = row.KnowledgeId.Value });
        if (row.CommentId.HasValue) package.Data.Comments.Add(new() { Id = row.CommentId.Value });
        if (row.InboxId.HasValue) package.Data.InboxItems.Add(new() { Id = row.InboxId.Value });
        var error = EncryptedGraphProfile.Validate(package) ?? SelfHostedSealedStore.ValidatePrimaryReferences(catalog, write.PayloadKeyReferences);
        if (error != null) return SealedStoreResult.Fail(400, error);
        row.EncryptedFieldsJson = record.EncryptedFieldsJson; row.DisplayOrder = record.DisplayOrder; row.IsPrimary = record.IsPrimary;
        row.ExcludeFromAiProcessing = record.ExcludeFromAiProcessing; row.ExcludeFromVersionDiff = record.ExcludeFromVersionDiff; row.UpdatedAt = DateTime.UtcNow;
        db.ShPayloadKeyReferences.RemoveRange(await db.ShPayloadKeyReferences.Where(r => r.OwnerKind == "attachment" && r.OwnerId == row.Id).ToListAsync(ct));
        foreach (var reference in write.PayloadKeyReferences) { var item = Copy<ShPayloadKeyReference>(reference); item.Id = Guid.NewGuid(); item.TenantId = tenant.TenantId; db.ShPayloadKeyReferences.Add(item); }
        await db.SaveChangesAsync(ct); write.Record = Copy<SealedAttachmentRecord>(row);
        return SealedStoreResult.Success(row.Id, 200, 1);
    }
    public async Task<SealedStoreResult> DeleteAttachmentAsync(Guid id, string expectedRevision, CancellationToken ct)
    {
        var row = await db.ShSealedAttachments.SingleOrDefaultAsync(a => a.Id == id, ct); if (row == null) return SealedStoreResult.Fail(404, "Attachment unavailable.");
        if (await SelfHostedInactiveShelf.IsOwnerInactiveAsync(db, row.KnowledgeId, row.CommentId, ct)) return SealedStoreResult.Fail(409, SelfHostedInactiveShelf.ReadOnlyMessage);
        if (SelfHostedSealedStore.PayloadRevision(Copy<SealedAttachmentRecord>(row)) != expectedRevision.Trim('"')) return SealedStoreResult.Fail(409, "Attachment changed; reload before deleting.");
        row.IsDeleted = true; row.IsPrimary = false; row.UpdatedAt = DateTime.UtcNow;
        foreach (var reference in await db.ShPayloadKeyReferences.Where(r => r.OwnerKind == "attachment" && r.OwnerId == id).ToListAsync(ct)) reference.IsDeleted = true;
        await db.SaveChangesAsync(ct); return SealedStoreResult.Success(id, 200, 1);
    }
    public async Task<SealedStoreResult> DeleteFileAsync(Guid id, string expectedRevision, CancellationToken ct)
    {
        if (await SelfHostedInactiveShelf.IsFileInactiveAsync(db, id, ct)) return SealedStoreResult.Fail(409, SelfHostedInactiveShelf.ReadOnlyMessage);
        var row = await db.ShSealedFiles.SingleOrDefaultAsync(f => f.Id == id, ct); if (row == null) return await DeleteProtectedFileAsync(id, expectedRevision, ct);
        if (!row.IsCurrentVersion) return SealedStoreResult.Fail(409, "Historical file versions are immutable; delete through the current version.");
        if (SelfHostedSealedStore.PayloadRevision(Copy<SealedFileRecord>(row)) != expectedRevision.Trim('"')) return SealedStoreResult.Fail(409, "File changed; reload before deleting.");
        var root = row.RootFileRecordId ?? row.Id;
        var versions = await db.ShSealedFiles.Where(f => f.Id == root || f.RootFileRecordId == root).ToListAsync(ct); var fileIds = versions.Select(f => f.Id).ToArray();
        var links = await db.ShSealedAttachments.Where(a => fileIds.Contains(a.FileRecordId)).ToListAsync(ct); var linkIds = links.Select(a => a.Id).ToArray();
        foreach (var link in links) { link.IsDeleted = true; link.IsPrimary = false; link.UpdatedAt = DateTime.UtcNow; }
        foreach (var version in versions) { version.IsDeleted = true; version.UpdatedAt = DateTime.UtcNow; }
        foreach (var reference in await db.ShPayloadKeyReferences.Where(r => r.OwnerKind == "file" && fileIds.Contains(r.OwnerId) || r.OwnerKind == "attachment" && linkIds.Contains(r.OwnerId)).ToListAsync(ct)) reference.IsDeleted = true;
        await db.SaveChangesAsync(ct);
        return SealedStoreResult.Success(id, 200, 1);
    }
}
