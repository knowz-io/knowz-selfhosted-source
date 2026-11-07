using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.API.Endpoints;

// Capture authority while owners are live, then recheck it before committing a logical delete.
// Deleted rows remain present for retained ciphertext; they must not erase the authority checks.
internal static class EncryptedDeleteOperation
{
    internal static Task<SealedStoreResult> RunAsync(HttpContext http, SelfHostedDbContext db,
        IVaultAccessService access, string kind, Guid id, Func<CancellationToken, Task<SealedStoreResult>> delete,
        CancellationToken ct) => db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
    {
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var tenant = AuthorizationHelpers.GetCallerTenantId(http);
        if (!tenant.HasValue) return SealedStoreResult.Fail(403, "Delete permission is required.");
        var owners = new HashSet<Guid>(); var fileVaults = new HashSet<Guid>();
        if (kind == "knowledge") owners.Add(id);
        if (kind == "comment")
        {
            var owner = await db.ShSealedComments.AsNoTracking().Where(c => c.Id == id).Select(c => (Guid?)c.KnowledgeId).SingleOrDefaultAsync(ct);
            if (!owner.HasValue) return SealedStoreResult.Fail(404, "Comment unavailable."); owners.Add(owner.Value);
        }
        if (kind is "knowledge" or "comment")
        {
            var knowledge = owners.Single();
            var family = await db.ShSealedComments.AsNoTracking().Where(c => c.KnowledgeId == knowledge).ToListAsync(ct);
            var selectedComments = kind == "knowledge" ? family.Select(c => c.Id).ToHashSet() : new HashSet<Guid> { id };
            while (true)
            {
                var count = selectedComments.Count;
                foreach (var child in family.Where(c => c.ParentCommentId.HasValue && selectedComments.Contains(c.ParentCommentId.Value))) selectedComments.Add(child.Id);
                if (selectedComments.Count == count) break;
            }
            var comments = selectedComments.ToArray();
            var links = await db.ShSealedAttachments.AsNoTracking().Where(a => kind == "knowledge" && a.KnowledgeId == knowledge
                || a.CommentId.HasValue && comments.Contains(a.CommentId.Value)).ToListAsync(ct);
            if (links.Any(a => a.InboxId.HasValue || a.KnowledgeId.HasValue == a.CommentId.HasValue
                || a.KnowledgeId.HasValue && (kind != "knowledge" || a.KnowledgeId != knowledge)
                || a.CommentId.HasValue && !selectedComments.Contains(a.CommentId.Value)))
                return SealedStoreResult.Fail(403, "Every attachment must belong only to the selected owner graph.");
        }
        if (kind is "file" or "attachment")
        {
            var fileId = id;
            if (kind == "attachment")
            {
                var link = await db.ShSealedAttachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct);
                if (link == null) return SealedStoreResult.Fail(404, "Attachment unavailable.");
                if (link.InboxId.HasValue || link.KnowledgeId.HasValue == link.CommentId.HasValue) return SealedStoreResult.Fail(403, "Attachment owner is unavailable.");
                fileId = link.FileRecordId;
                if (link.KnowledgeId.HasValue) owners.Add(link.KnowledgeId.Value);
                else
                {
                    var owner = await db.ShSealedComments.Where(c => c.Id == link.CommentId).Select(c => (Guid?)c.KnowledgeId).SingleOrDefaultAsync(ct);
                    if (!owner.HasValue) return SealedStoreResult.Fail(403, "Attachment owner is unavailable."); owners.Add(owner.Value);
                }
            }
            else
            {
                var root = await db.ShSealedFiles.Where(f => f.Id == fileId).Select(f => (Guid?)(f.RootFileRecordId ?? f.Id)).SingleOrDefaultAsync(ct)
                    ?? await db.FileRecords.Where(f => f.Id == fileId).Select(f => (Guid?)(f.RootFileRecordId ?? f.Id)).SingleOrDefaultAsync(ct);
                if (!root.HasValue) return SealedStoreResult.Fail(404, "File unavailable.");
                var files = await db.ShSealedFiles.AsNoTracking().Where(f => f.Id == root || f.RootFileRecordId == root).ToListAsync(ct);
                foreach (var vault in files.Where(f => f.VaultId.HasValue).Select(f => f.VaultId!.Value)) fileVaults.Add(vault);
                foreach (var owner in files.Where(f => f.KnowledgeId.HasValue).Select(f => f.KnowledgeId!.Value)) owners.Add(owner);
                var ids = files.Select(f => f.Id).Concat(await db.FileRecords.Where(f => f.Id == root || f.RootFileRecordId == root).Select(f => f.Id).ToListAsync(ct)).Distinct().ToArray();
                var links = await db.ShSealedAttachments.AsNoTracking().Where(a => ids.Contains(a.FileRecordId)).ToListAsync(ct);
                var plain = await db.FileAttachments.AsNoTracking().Where(a => ids.Contains(a.FileRecordId)).ToListAsync(ct);
                if (links.Any(a => a.InboxId.HasValue || a.KnowledgeId.HasValue == a.CommentId.HasValue) || plain.Any(a => a.KnowledgeId.HasValue == a.CommentId.HasValue)) return SealedStoreResult.Fail(403, "File owner is unavailable.");
                foreach (var owner in links.Where(a => a.KnowledgeId.HasValue).Select(a => a.KnowledgeId!.Value).Concat(plain.Where(a => a.KnowledgeId.HasValue).Select(a => a.KnowledgeId!.Value))) owners.Add(owner);
                var comments = links.Where(a => a.CommentId.HasValue).Select(a => a.CommentId!.Value).Concat(plain.Where(a => a.CommentId.HasValue).Select(a => a.CommentId!.Value)).Distinct().ToArray();
                var sealedParents = await db.ShSealedComments.Where(c => comments.Contains(c.Id)).Select(c => new { c.Id, c.KnowledgeId }).ToListAsync(ct);
                var ordinaryParents = await db.Comments.Where(c => comments.Contains(c.Id)).Select(c => new { c.Id, c.KnowledgeId }).ToListAsync(ct);
                if (sealedParents.Select(c => c.Id).Concat(ordinaryParents.Select(c => c.Id)).Distinct().Count() != comments.Length) return SealedStoreResult.Fail(403, "File owner is unavailable.");
                foreach (var owner in sealedParents.Select(c => c.KnowledgeId).Concat(ordinaryParents.Select(c => c.KnowledgeId))) owners.Add(owner);
            }
        }
        var ownerIds = owners.ToArray();
        var memberships = await db.KnowledgeVaults.AsNoTracking().Where(v => ownerIds.Contains(v.KnowledgeId)).Select(v => new { v.KnowledgeId, v.VaultId }).ToListAsync(ct);
        if (ownerIds.Any(owner => !memberships.Any(m => m.KnowledgeId == owner)) || await db.KnowledgeItems.CountAsync(k => ownerIds.Contains(k.Id), ct) != ownerIds.Length) return SealedStoreResult.Fail(403, "Delete owner is unavailable.");
        var vaultIds = memberships.Select(m => m.VaultId).Concat(fileVaults).Distinct().ToArray();
        if (vaultIds.Length == 0) return SealedStoreResult.Fail(403, "Delete owner is unavailable.");
        async Task<bool> AuthorizedAsync(bool afterDelete = false)
        {
            if (await db.KnowledgeItems.IgnoreQueryFilters().AsNoTracking().CountAsync(k => k.TenantId == tenant.Value && ownerIds.Contains(k.Id)
                && k.IsDeleted == (afterDelete && kind == "knowledge" && k.Id == id), ct) != ownerIds.Length) return false;
            foreach (var vault in vaultIds) if (!await EncryptedGraphEndpoints.AuthorizedAsync(http, vault, db, access, false, ct, requireDelete: true)) return false;
            var fresh = await db.KnowledgeVaults.AsNoTracking().Where(v => ownerIds.Contains(v.KnowledgeId)).Select(v => new { v.KnowledgeId, v.VaultId }).ToListAsync(ct);
            return fresh.Count == memberships.Count && fresh.All(m => memberships.Contains(m));
        }
        if (!await AuthorizedAsync()) return SealedStoreResult.Fail(403, "Delete permission is required.");
        try
        {
            var result = await delete(ct);
            if (!result.Ok || !await AuthorizedAsync(afterDelete: true))
            {
                if (transaction != null) await transaction.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                return result.Ok ? SealedStoreResult.Fail(403, "Delete permission changed; nothing was deleted.") : result;
            }
            if (transaction != null) await transaction.CommitAsync(ct);
            return result;
        }
        catch { if (transaction != null) await transaction.RollbackAsync(CancellationToken.None); db.ChangeTracker.Clear(); throw; }
    });
}
