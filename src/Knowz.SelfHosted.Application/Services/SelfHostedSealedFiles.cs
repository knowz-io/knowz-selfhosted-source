using System.Security.Cryptography;
using System.Text.Json;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Application.Services;

public sealed class SealedFileWrite
{
    public Guid VaultId { get; set; }
    public SealedFileRecord Record { get; set; } = new();
    public List<PortablePayloadKeyReference> PayloadKeyReferences { get; set; } = [];
    public List<SealedAttachmentRecord> Attachments { get; set; } = [];
}
public sealed record SealedFileResult(bool Ok, int Status, string? Error = null, string? Revision = null, SealedFileWrite? Write = null, bool Staged = false);

public sealed partial class SelfHostedSealedFiles(SelfHostedDbContext db, ITenantProvider tenant, SelfHostedSealedStore lifecycle, IFileStorageProvider storage)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static T Copy<T>(object source) => PortablePublicProjection.Project<T>(source) is T projected ? projected : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(source, Json), Json)!;
    private static SealedFileResult Fail(int status, string error) => new(false, status, error);

    public async Task<SealedFileResult> StageAsync(Guid actor, SealedFileWrite write, string expectedRevision, string idempotencyKey, CancellationToken ct)
    {
        if (actor == Guid.Empty || idempotencyKey.Length is < 1 or > 200 || write.Record == null || write.PayloadKeyReferences == null || write.Attachments == null) return Fail(400, "File upload identity and metadata are required.");
        var metadata = JsonSerializer.Serialize(write, Json);
        if (metadata.Length > 8 * 1024 * 1024 || SealedMaterialGuard.DeclaresPrivateMaterial(metadata)) return Fail(400, "Invalid file metadata or private material.");
        var prior = await db.ShSealedFileStages.SingleOrDefaultAsync(s => s.ActorId == actor && s.IdempotencyKey == idempotencyKey, ct);
        if (prior != null && prior.Status != "committed" && (prior.Status == "cancelled" || prior.ExpiresAt <= DateTime.UtcNow)) return Fail(410, "Upload stage expired; create a new upload intent.");
        if (prior != null) return prior.MetadataJson == metadata ? new(true, 200, Revision: prior.Revision, Write: write, Staged: prior.Status != "committed") : Fail(409, "Idempotency key was used with different file metadata.");
        var catalog = await lifecycle.GetCatalogAsync(write.VaultId, ct);
        if (catalog == null) return Fail(404, "File vault unavailable.");
        if (catalog.Revision != expectedRevision.Trim('"')) return Fail(409, "Vault catalog changed; reload before uploading.");
        if (await db.ShSealedFiles.IgnoreQueryFilters().AnyAsync(f => f.Id == write.Record.Id, ct) || await db.FileRecords.IgnoreQueryFilters().AnyAsync(f => f.Id == write.Record.Id, ct)) return Fail(409, "File identity already exists.");
        var error = await ValidateAsync(write, catalog, ct);
        if (error != null) return Fail(400, error);
        var stage = new ShSealedFileStage { Id = Guid.NewGuid(), TenantId = tenant.TenantId, ActorId = actor, VaultId = write.VaultId, FileId = write.Record.Id,
            IdempotencyKey = idempotencyKey, MetadataJson = metadata, Revision = Guid.NewGuid().ToString("N"), ExpiresAt = DateTime.UtcNow.AddMinutes(30) };
        db.ShSealedFileStages.Add(stage); await db.SaveChangesAsync(ct);
        return new(true, 201, Revision: stage.Revision, Write: write, Staged: true);
    }

    public async Task<SealedFileResult> UploadAsync(Guid actor, Guid fileId, Stream ciphertext, string expectedRevision, string expectedSha256, CancellationToken ct, Func<CancellationToken, Task<bool>>? authorize = null)
    {
        var stage = await db.ShSealedFileStages.SingleOrDefaultAsync(s => s.ActorId == actor && s.FileId == fileId && s.Revision == expectedRevision.Trim('"'), ct);
        if (stage == null) return Fail(404, "Upload stage unavailable.");
        var write = JsonSerializer.Deserialize<SealedFileWrite>(stage.MetadataJson, Json)!;
        if (stage.Status == "committed") return new(true, 200, Revision: SelfHostedSealedStore.PayloadRevision(write.Record), Write: write);
        if (stage.Status != "prepared" || stage.ExpiresAt <= DateTime.UtcNow) return Fail(409, "Upload stage expired or is busy.");
        if (write.Record.BinarySha256 != expectedSha256) return Fail(400, "Ciphertext digest does not match staged metadata.");
        var catalog = await lifecycle.GetCatalogAsync(write.VaultId, ct);
        if (catalog == null) return Fail(404, "Vault unavailable.");
        var error = await ValidateAsync(write, catalog, ct);
        if (error != null) return Fail(400, error);
        stage.Status = "uploading"; stage.UpdatedAt = DateTime.UtcNow; stage.BlobStorageId = Guid.NewGuid();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Fail(409, "Upload already claimed; reload its status."); }
        try
        {
            await using var verified = new VerifyingCiphertext(ciphertext, write.Record.SizeBytes, expectedSha256);
            await using var spool = await ProtectedSpoolStream.CreateAsync(verified, ct: ct);
            verified.RequireComplete();
            await storage.UploadAsync(tenant.TenantId, stage.BlobStorageId.Value, spool, "application/octet-stream", ct);
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
                if (authorize != null && !await authorize(ct)) throw new UnauthorizedAccessException("Destination access was revoked during upload.");
                var freshStage = await db.ShSealedFileStages.SingleAsync(s => s.Id == stage.Id, ct);
                if (freshStage.Status == "committed")
                {
                    var committed = await db.ShSealedFiles.AsNoTracking().SingleAsync(f => f.Id == fileId, ct);
                    var response = Copy<SealedFileWrite>(write); response.Record = Copy<SealedFileRecord>(committed);
                    return new SealedFileResult(true, 200, Revision: SelfHostedSealedStore.PayloadRevision(response.Record), Write: response);
                }
                if (freshStage.Status != "uploading" || freshStage.BlobStorageId != stage.BlobStorageId || freshStage.ExpiresAt <= DateTime.UtcNow) throw new InvalidDataException("Upload stage changed during activation.");
                error = await ValidateAsync(write, (await lifecycle.GetCatalogAsync(write.VaultId, ct))!, ct);
                if (error != null) throw new InvalidDataException(error);
                var row = Copy<ShSealedFile>(write.Record); row.TenantId = tenant.TenantId; row.BlobStorageId = stage.BlobStorageId; row.BinaryContentBase64 = null;
                db.ShSealedFiles.Add(row);
                foreach (var attachment in write.Attachments) { var link = Copy<ShSealedAttachment>(attachment); link.TenantId = tenant.TenantId; db.ShSealedAttachments.Add(link); }
                foreach (var reference in write.PayloadKeyReferences) { var link = Copy<ShPayloadKeyReference>(reference); link.Id = Guid.NewGuid(); link.TenantId = tenant.TenantId; db.ShPayloadKeyReferences.Add(link); }
                freshStage.Status = "committed"; freshStage.UpdatedAt = DateTime.UtcNow; await db.SaveChangesAsync(ct);
                if (transaction != null) await transaction.CommitAsync(ct);
                return new SealedFileResult(true, 200, Revision: SelfHostedSealedStore.PayloadRevision(write.Record), Write: write);
            });
        }
        catch (Exception ex)
        {
            var blob = stage.BlobStorageId;
            db.ChangeTracker.Clear();
            if (await db.ShSealedFileStages.AnyAsync(s => s.Id == stage.Id && s.Status == "committed", CancellationToken.None))
                return Fail(409, "Upload committed; reload the file before continuing.");
            var cleaned = !blob.HasValue;
            if (blob.HasValue) try { await storage.DeleteAsync(tenant.TenantId, blob.Value, CancellationToken.None); cleaned = true; } catch { /* Keep the durable physical identity for the reconciler. */ }
            var retry = await db.ShSealedFileStages.SingleAsync(s => s.Id == stage.Id, CancellationToken.None);
            retry.Status = cleaned ? "prepared" : "cancelled"; retry.BlobStorageId = cleaned ? null : blob;
            if (!cleaned) retry.ExpiresAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            if (ex is OperationCanceledException) throw;
            return Fail(ex is DbUpdateException ? 409 : 400, "Ciphertext upload failed validation or its destination changed.");
        }
    }

    private async Task<string?> ValidateAsync(SealedFileWrite write, SealedCatalogResponse catalog, CancellationToken ct, bool update = false)
    {
        if (write.Record.OriginalSizeBytes is < 0 or > EncryptedGraphProfile.MaxFileBytes)
            return "Original file size must be null or between zero and 64 MiB.";
        if (write.Record.BinaryContentBase64 != null || write.Record.BinaryEntryPath != null) return "Ciphertext bytes must use the separate binary transport.";
        var file = Copy<SealedFileRecord>(write.Record); file.BinaryEntryPath = $"files/{file.Id:D}.bin";
        var package = SelfHostedSealedStore.MutationPackage(catalog, write.PayloadKeyReferences.ToList()); package.SealedFiles = [file]; package.SealedAttachments = write.Attachments;
        package.Data.Vaults.Add(new() { Id = write.VaultId });
        var knowledgeIds = write.Attachments.Where(a => a.KnowledgeId.HasValue).Select(a => a.KnowledgeId!.Value).Concat(file.KnowledgeId.HasValue ? [file.KnowledgeId.Value] : []).Distinct().ToList();
        var existingLinks = update ? await db.ShSealedAttachments.AsNoTracking().Where(a => a.FileRecordId == file.Id).ToListAsync(ct) : [];
        knowledgeIds.AddRange(existingLinks.Where(a => a.KnowledgeId.HasValue).Select(a => a.KnowledgeId!.Value));
        var comments = write.Attachments.Where(a => a.CommentId.HasValue).Select(a => a.CommentId!.Value).Concat(existingLinks.Where(a => a.CommentId.HasValue).Select(a => a.CommentId!.Value)).Distinct().ToList();
        foreach (var id in comments)
        {
            var comment = await db.ShSealedComments.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
            if (comment == null) return "Attachment comment is unavailable.";
            knowledgeIds.Add(comment.KnowledgeId); package.Data.Comments.Add(new() { Id = id, KnowledgeId = comment.KnowledgeId });
        }
        if (file.VaultId.HasValue && file.VaultId != write.VaultId) return "File owning vault does not match the destination vault.";
        if (knowledgeIds.Count == 0 && file.VaultId != write.VaultId || write.Attachments.Any(a => a.InboxId.HasValue)) return "File must have an authorized knowledge, comment or vault owner.";
        foreach (var id in knowledgeIds.Distinct())
        {
            if (await SelfHostedInactiveShelf.IsInactiveAsync(db, id, ct)) return SelfHostedInactiveShelf.ReadOnlyMessage;
            if (!await db.KnowledgeVaults.AnyAsync(v => v.KnowledgeId == id && v.VaultId == write.VaultId, ct)) return "File owner is not in the destination vault.";
            package.Data.KnowledgeItems.Add(new() { Id = id });
        }
        foreach (var attachment in write.Attachments)
            if (attachment.FileRecordId != file.Id || attachment.OriginalAttachmentId == Guid.Empty || await db.ShSealedAttachments.IgnoreQueryFilters().AnyAsync(a => a.Id == attachment.Id, ct)) return "Attachment identity is invalid or already exists.";
        var previousId = file.PreviousFileRecordId; var visited = new HashSet<Guid> { file.Id };
        while (previousId.HasValue)
        {
            if (!visited.Add(previousId.Value)) return "File lineage contains a cycle.";
            var previous = await db.ShSealedFiles.AsNoTracking().SingleOrDefaultAsync(f => f.Id == previousId, ct);
            if (previous == null) return "File lineage is incomplete.";
            var ancestor = Copy<SealedFileRecord>(previous); ancestor.BinaryEntryPath = $"files/{ancestor.Id:D}.bin"; ancestor.BinaryContentBase64 = null;
            package.SealedFiles.Add(ancestor);
            package.PayloadKeyReferences!.AddRange((await db.ShPayloadKeyReferences.AsNoTracking().Where(r => r.OwnerKind == "file" && r.OwnerId == ancestor.Id).ToListAsync(ct)).Select(Copy<PortablePayloadKeyReference>));
            previousId = previous.PreviousFileRecordId;
        }
        return EncryptedGraphProfile.Validate(package) ?? SelfHostedSealedStore.ValidatePrimaryReferences(catalog, write.PayloadKeyReferences);
    }

    private sealed class VerifyingCiphertext(Stream inner, long length, string digest, long maximum = EncryptedGraphProfile.MaxFileBytes) : Stream
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); private long count; private bool complete;
        public void RequireComplete() { if (!complete) throw new InvalidDataException("Incomplete ciphertext."); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (complete) return 0;
            var read = await inner.ReadAsync(buffer, ct); count += read;
            if (count > length || count > maximum) throw new InvalidDataException("Ciphertext exceeds declared limit.");
            if (read > 0) hash.AppendData(buffer.Span[..read]);
            else { if (count != length || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != digest) throw new InvalidDataException("Ciphertext integrity mismatch."); complete = true; }
            return read;
        }
        protected override void Dispose(bool disposing) { if (disposing) hash.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => length; public override long Position { get => count; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
