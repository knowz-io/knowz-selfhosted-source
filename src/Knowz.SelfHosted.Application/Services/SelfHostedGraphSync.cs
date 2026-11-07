using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.DTOs;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Application.Services;

public sealed partial class SelfHostedGraphStore
{
    private static readonly HashSet<string> SyncEntityKinds = ["knowledge", "comment", "version", "file", "attachment", "topic", "tag", "person", "location", "event", "relationship"];

    /// <summary>Linked peer sync preserves logical item IDs; archive copies use isolated destination IDs.</summary>
    public async Task<PortableImportResult> ImportSyncAsync(PortableExportPackage package, Guid vaultId, ImportConflictStrategy strategy, CancellationToken ct)
    {
        var error = EncryptedGraphProfile.Validate(package) ?? await SyncOwnershipErrorAsync(package, vaultId, ct);
        if (error != null) return new() { Error = error };
        return await ImportCoreAsync(package, vaultId, "unverified:sync:" + package.SourceTenantId.ToString("D"), strategy, null, ct, preserveEntityIds: true);
    }
    public async Task<PortableImportResult> ImportPeerArchiveAsync(PortableExportPackage package, Guid vaultId, string lineage,
        ImportConflictStrategy strategy, Func<string, CancellationToken, Task<Stream>> openBinary, SelfHostedGraphStage stage, CancellationToken ct, IReadOnlyDictionary<Guid, Guid>? contextMappings = null)
    {
        var error = await SyncOwnershipErrorAsync(package, vaultId, ct);
        if (error != null) return new() { Error = error };
        return await ImportCoreAsync(package, vaultId, lineage, strategy, openBinary, ct, preparedStage: stage, preserveEntityIds: true, preparedContextMappings: contextMappings);
    }
    public async Task<string?> SyncOwnershipErrorAsync(PortableExportPackage package, Guid vaultId, CancellationToken ct)
    {
        const string denied = "A graph sync identity is outside the authorized destination vault; import refused before mutation.";
        if (!await db.Vaults.AnyAsync(v => v.Id == vaultId, ct)) return denied;
        if (SelfHostedInactiveShelf.Owners(package).Any()) return "Inactive recovery records require an owner-scoped archive restore, not peer synchronization.";
        if (package.Tombstones is { Count: > 0 }) return "Encrypted graph snapshots cannot apply deletion tombstones.";
        if (package.Data.InboxItems.Count > 0 || (package.SealedAttachments ?? []).Any(a => a.InboxId.HasValue)) return "Private inbox data requires an actor-scoped archive restore plan.";
        var members = db.KnowledgeItems.IgnoreQueryFilters().Where(k => k.TenantId == tenant.TenantId && !k.IsDeleted && k.PortableRecoveryState == null
            && db.KnowledgeVaults.IgnoreQueryFilters().Any(v => v.KnowledgeId == k.Id && v.TenantId == tenant.TenantId && v.VaultId == vaultId)
            && !db.KnowledgeVaults.IgnoreQueryFilters().Any(v => v.KnowledgeId == k.Id && (v.TenantId != tenant.TenantId || v.VaultId != vaultId))).Select(k => k.Id);
        var items = package.Data.KnowledgeItems.Select(k => k.Id).Concat((package.SealedItems ?? []).Select(k => k.Id)).ToArray();
        if (await db.KnowledgeItems.IgnoreQueryFilters().AnyAsync(k => items.Contains(k.Id) && (k.TenantId != tenant.TenantId || !members.Contains(k.Id)), ct)) return denied;
        var comments = package.Data.Comments.Select(c => c.Id).Concat((package.SealedComments ?? []).Select(c => c.Id)).ToArray();
        if (await db.Comments.IgnoreQueryFilters().AnyAsync(c => comments.Contains(c.Id) && (c.TenantId != tenant.TenantId || !members.Contains(c.KnowledgeId)), ct)
            || await db.ShSealedComments.IgnoreQueryFilters().AnyAsync(c => comments.Contains(c.Id) && (c.TenantId != tenant.TenantId || !members.Contains(c.KnowledgeId)), ct)) return denied;
        var versions = (package.PlainVersions ?? []).Select(v => v.Id).Concat((package.SealedVersions ?? []).Select(v => v.Id)).ToArray();
        if (await db.ShSealedVersions.IgnoreQueryFilters().AnyAsync(v => versions.Contains(v.Id) && (v.TenantId != tenant.TenantId || !members.Contains(v.KnowledgeId)), ct)
            || await db.KnowledgeVersions.IgnoreQueryFilters().AnyAsync(v => versions.Contains(v.Id) && (v.TenantId != tenant.TenantId || !members.Contains(v.KnowledgeId)), ct)) return denied;
        var commentOwners = db.ShSealedComments.IgnoreQueryFilters().Where(c => c.TenantId == tenant.TenantId && members.Contains(c.KnowledgeId)).Select(c => c.Id)
            .Union(db.Comments.IgnoreQueryFilters().Where(c => c.TenantId == tenant.TenantId && !c.IsDeleted && members.Contains(c.KnowledgeId)).Select(c => c.Id));
        var ownedFiles = db.ShSealedAttachments.IgnoreQueryFilters().Where(a => a.TenantId == tenant.TenantId && (a.KnowledgeId.HasValue && members.Contains(a.KnowledgeId.Value) || a.CommentId.HasValue && commentOwners.Contains(a.CommentId.Value))).Select(a => a.FileRecordId);
        var files = package.Data.FileRecords.Select(f => f.Id).Concat((package.SealedFiles ?? []).Select(f => f.Id)).ToArray();
        var sealedRoots = db.ShSealedFiles.IgnoreQueryFilters().Where(f => f.TenantId == tenant.TenantId && f.IsCurrentVersion && (f.VaultId == vaultId || f.KnowledgeId.HasValue && members.Contains(f.KnowledgeId.Value) || ownedFiles.Contains(f.Id))).Select(f => f.RootFileRecordId ?? f.Id);
        var ownedPlainFiles = db.FileAttachments.IgnoreQueryFilters().Where(a => a.TenantId == tenant.TenantId && (a.KnowledgeId.HasValue && members.Contains(a.KnowledgeId.Value) || a.CommentId.HasValue && commentOwners.Contains(a.CommentId.Value))).Select(a => a.FileRecordId).Union(ownedFiles);
        var plainRoots = db.FileRecords.IgnoreQueryFilters().Where(f => f.TenantId == tenant.TenantId && !f.IsDeleted && f.IsCurrentVersion && ownedPlainFiles.Contains(f.Id)).Select(f => f.RootFileRecordId ?? f.Id);
        if (await db.ShSealedFiles.IgnoreQueryFilters().AnyAsync(f => files.Contains(f.Id) && (f.TenantId != tenant.TenantId || f.VaultId.HasValue && f.VaultId != vaultId || !sealedRoots.Contains(f.RootFileRecordId ?? f.Id)), ct)
            || await db.FileRecords.IgnoreQueryFilters().AnyAsync(f => files.Contains(f.Id) && (f.TenantId != tenant.TenantId || f.IsDeleted || !plainRoots.Contains(f.RootFileRecordId ?? f.Id)), ct)) return denied;
        var touchedRoots = (await db.ShSealedFiles.IgnoreQueryFilters().Where(f => files.Contains(f.Id)).Select(f => f.RootFileRecordId ?? f.Id).ToListAsync(ct))
            .Concat(await db.FileRecords.IgnoreQueryFilters().Where(f => files.Contains(f.Id)).Select(f => f.RootFileRecordId ?? f.Id).ToListAsync(ct)).Distinct().ToArray();
        var chainFiles = db.ShSealedFiles.IgnoreQueryFilters().Where(f => touchedRoots.Contains(f.RootFileRecordId ?? f.Id)).Select(f => f.Id)
            .Union(db.FileRecords.IgnoreQueryFilters().Where(f => touchedRoots.Contains(f.RootFileRecordId ?? f.Id)).Select(f => f.Id));
        if (await db.ShSealedFiles.IgnoreQueryFilters().AnyAsync(f => chainFiles.Contains(f.Id) && (f.TenantId != tenant.TenantId || f.VaultId.HasValue && f.VaultId != vaultId || f.KnowledgeId.HasValue && !members.Contains(f.KnowledgeId.Value)), ct)
            || await db.ShSealedAttachments.IgnoreQueryFilters().AnyAsync(a => chainFiles.Contains(a.FileRecordId) && (a.TenantId != tenant.TenantId || a.InboxId.HasValue || a.KnowledgeId.HasValue && !members.Contains(a.KnowledgeId.Value) || a.CommentId.HasValue && !commentOwners.Contains(a.CommentId.Value)), ct)
            || await db.FileAttachments.IgnoreQueryFilters().AnyAsync(a => chainFiles.Contains(a.FileRecordId) && (a.TenantId != tenant.TenantId || a.KnowledgeId.HasValue && !members.Contains(a.KnowledgeId.Value) || a.CommentId.HasValue && !commentOwners.Contains(a.CommentId.Value)), ct)) return denied;
        var attachments = (package.SealedAttachments ?? []).Select(a => a.Id).ToArray();
        if (await db.ShSealedAttachments.IgnoreQueryFilters().AnyAsync(a => attachments.Contains(a.Id) && (a.TenantId != tenant.TenantId || !(a.KnowledgeId.HasValue && members.Contains(a.KnowledgeId.Value) || a.CommentId.HasValue && commentOwners.Contains(a.CommentId.Value))), ct)) return denied;
        if (await Foreign<Topic>(package.Data.Topics.Select(x => x.Id)) || await Foreign<Tag>(package.Data.Tags.Select(x => x.Id))
            || await Foreign<Person>(package.Data.Persons.Select(x => x.Id)) || await Foreign<Location>(package.Data.Locations.Select(x => x.Id)) || await Foreign<Event>(package.Data.Events.Select(x => x.Id))) return denied;
        return null;
        Task<bool> Foreign<T>(IEnumerable<Guid> values) where T : class, ISelfHostedEntity
        { var ids = values.ToArray(); return db.Set<T>().IgnoreQueryFilters().AnyAsync(e => ids.Contains(e.Id) && (e.TenantId != tenant.TenantId || e.IsDeleted), ct); }
    }
}
