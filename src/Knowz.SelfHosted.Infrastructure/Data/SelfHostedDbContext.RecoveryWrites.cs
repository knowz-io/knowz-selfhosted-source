using Knowz.Core.Entities;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Infrastructure.Data;
public partial class SelfHostedDbContext
{
    private int recoveryImportDepth;
    /// <summary>Scoped only around the validated archive import transaction. Ordinary services never acquire this lease.</summary>
    public IDisposable BeginInactiveRecoveryImport()
    {
        if (!PortableActorId.HasValue || PortableActorId == Guid.Empty) throw new UnauthorizedAccessException("Inactive recovery requires a local actor.");
        recoveryImportDepth++; return new RecoveryLease(this);
    }
    private sealed class RecoveryLease(SelfHostedDbContext db) : IDisposable
    { private bool disposed; public void Dispose() { if (!disposed) { disposed = true; db.recoveryImportDepth--; } } }
    private async Task GuardInactiveWritesAsync(bool asynchronous, CancellationToken ct)
    {
        if (recoveryImportDepth > 0) return;
        ChangeTracker.DetectChanges();
        var entries = ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
        if (entries.Length == 0) return;
        const string denied = "Imported recovery records are suspended and read-only.";
        if (entries.Any(e => e.Entity is ShInactiveRecoveryRecord || e.Entity is Knowledge &&
            (e.CurrentValues.GetValue<string?>(nameof(Knowledge.PortableRecoveryState)) != null || e.State != EntityState.Added && e.OriginalValues.GetValue<string?>(nameof(Knowledge.PortableRecoveryState)) != null))) throw new InvalidOperationException(denied);
        var owners = new HashSet<Guid>(); var comments = new HashSet<Guid>(); var files = new HashSet<Guid>();
        foreach (var e in entries)
        {
            // Infrastructure may retire stale jobs and delete stale index projections. It may
            // never add/modify chunks, mutate history or grant an inactive record write authority.
            if (e.Entity is EnrichmentOutboxItem or EnrichmentActivityLog
                || e.Entity is ContentChunk && e.State == EntityState.Deleted) continue;
            foreach (var name in new[] { "KnowledgeId", "SourceKnowledgeId", "TargetKnowledgeId" })
                if (e.Metadata.FindProperty(name) != null)
                { if (e.Property(name).CurrentValue is Guid id) owners.Add(id); if (e.Property(name).OriginalValue is Guid old) owners.Add(old); }
            if (e.Entity is Knowledge item) owners.Add(item.Id);
            if (e.Entity is FileRecord f) files.Add(f.Id);
            if (e.Entity is ShSealedFile sf) files.Add(sf.Id);
            foreach (var name in new[] { "CommentId", "FileRecordId" })
                if (e.Metadata.FindProperty(name) != null)
                {
                    var target = name == "CommentId" ? comments : files;
                    if (e.Property(name).CurrentValue is Guid id) target.Add(id);
                    if (e.Property(name).OriginalValue is Guid old) target.Add(old);
                }
        }
        if (owners.Count == 0 && comments.Count == 0 && files.Count == 0) return;
        var fileOwnerIds = FileAttachments.IgnoreQueryFilters().Where(a => a.TenantId == _tenantId && files.Contains(a.FileRecordId) && a.KnowledgeId.HasValue).Select(a => a.KnowledgeId!.Value)
            .Union(ShSealedAttachments.IgnoreQueryFilters().Where(a => a.TenantId == _tenantId && files.Contains(a.FileRecordId) && a.KnowledgeId.HasValue).Select(a => a.KnowledgeId!.Value))
            .Union(ShSealedFiles.IgnoreQueryFilters().Where(f => f.TenantId == _tenantId && files.Contains(f.Id) && f.KnowledgeId.HasValue).Select(f => f.KnowledgeId!.Value));
        var fileComments = FileAttachments.IgnoreQueryFilters().Where(a => a.TenantId == _tenantId && files.Contains(a.FileRecordId) && a.CommentId.HasValue).Select(a => a.CommentId!.Value)
            .Union(ShSealedAttachments.IgnoreQueryFilters().Where(a => a.TenantId == _tenantId && files.Contains(a.FileRecordId) && a.CommentId.HasValue).Select(a => a.CommentId!.Value));
        var commentOwners = Comments.IgnoreQueryFilters().Where(c => c.TenantId == _tenantId && (comments.Contains(c.Id) || fileComments.Contains(c.Id))).Select(c => c.KnowledgeId)
            .Union(ShSealedComments.IgnoreQueryFilters().Where(c => c.TenantId == _tenantId && (comments.Contains(c.Id) || fileComments.Contains(c.Id))).Select(c => c.KnowledgeId));
        var query = KnowledgeItems.IgnoreQueryFilters().Where(k => k.TenantId == _tenantId && k.PortableRecoveryState != null && (owners.Contains(k.Id) || fileOwnerIds.Contains(k.Id) || commentOwners.Contains(k.Id)));
        if (asynchronous ? await query.AnyAsync(ct) : query.Any()) throw new InvalidOperationException(denied);
    }
}
