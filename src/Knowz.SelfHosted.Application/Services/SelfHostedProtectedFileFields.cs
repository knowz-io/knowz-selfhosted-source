using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Application.Services;

public sealed class ProtectedFileWrite
{
    public Guid VaultId { get; set; }
    public PortableFileRecord Record { get; set; } = new();
    public List<PortablePayloadKeyReference> PayloadKeyReferences { get; set; } = [];
}

public sealed partial class SelfHostedSealedFiles
{
    public static async Task<PortableFileRecord?> GetProtectedFileAsync(Knowz.SelfHosted.Infrastructure.Data.SelfHostedDbContext db, Guid id, CancellationToken ct)
    {
        var row = await db.FileRecords.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id && f.EncryptedFieldsJson != null, ct);
        if (row == null) return null;
        return new() { Id = row.Id, OriginalFileId = row.OriginalFileId ?? row.Id, EncryptedFieldsJson = row.EncryptedFieldsJson,
            FileVersion = row.FileVersion, RootFileRecordId = row.RootFileRecordId, PreviousFileRecordId = row.PreviousFileRecordId, IsCurrentVersion = row.IsCurrentVersion,
            BinarySha256 = await db.ShFileObjectReferences.Where(f => f.FileRecordId == id).Select(f => f.Sha256).FirstOrDefaultAsync(ct),
            FileName = ProtectedFileMetadata.FileName(row), ContentType = row.ContentType, SizeBytes = row.SizeBytes, CreatedAt = row.CreatedAt, UpdatedAt = row.UpdatedAt,
            Attachments = await db.FileAttachments.Where(a => a.FileRecordId == id).OrderBy(a => a.Id)
                .Select(a => new PortableFileAttachmentLink { KnowledgeId = a.KnowledgeId, CommentId = a.CommentId }).ToListAsync(ct) };
    }

    public Task<SealedStoreResult> UpdateProtectedFieldsAsync(ProtectedFileWrite write, string expectedRevision, CancellationToken ct, Func<CancellationToken, Task<bool>>? authorize = null)
        => UpdateProtectedFileAsync(Guid.Empty, write, expectedRevision, null, ct, authorize);

    public async Task<SealedStoreResult> UpdateProtectedFileAsync(Guid actor, ProtectedFileWrite write, string expectedRevision, Stream? binary, CancellationToken ct, Func<CancellationToken, Task<bool>>? authorize = null)
    {
        var current = await GetProtectedFileAsync(db, write.Record.Id, ct);
        if (current == null) return SealedStoreResult.Fail(404, "Protected file metadata unavailable.");
        if (!current.IsCurrentVersion) return SealedStoreResult.Fail(409, "Historical file versions are immutable; edit the current version.");
        if (await SelfHostedInactiveShelf.IsFileInactiveAsync(db, current.Id, ct)) return SealedStoreResult.Fail(409, SelfHostedInactiveShelf.ReadOnlyMessage);
        if (SelfHostedSealedStore.PayloadRevision(current) != expectedRevision.Trim('"')) return SealedStoreResult.Fail(409, "File metadata changed; reload before editing.");
        var record = write.Record;
        if (record.OriginalFileId != current.OriginalFileId || record.FileName != current.FileName || record.FileVersion != current.FileVersion || record.RootFileRecordId != current.RootFileRecordId || record.PreviousFileRecordId != current.PreviousFileRecordId || !record.IsCurrentVersion ||
            record.BinaryContentBase64 != null || record.BinaryEntryPath != null || record.BinaryFilePath != null || record.BlobUri != null ||
            JsonSerializer.Serialize(record.Attachments) != JsonSerializer.Serialize(current.Attachments)) return SealedStoreResult.Fail(400, "File identity, lineage and owners are immutable.");
        if (binary == null && (record.SizeBytes != current.SizeBytes || record.ContentType != current.ContentType || record.BinarySha256 != current.BinarySha256))
            return SealedStoreResult.Fail(400, "Changing bytes requires an atomic binary replacement.");
        if (binary != null && (actor == Guid.Empty || record.SizeBytes < 0 || record.SizeBytes > KnowzArchiveLimits.MaxOrdinaryFileBytes || !EncryptedGraphProfile.IsSha256(record.BinarySha256)))
            return SealedStoreResult.Fail(400, "A bounded file length, SHA256 and authenticated actor are required.");
        var error = await ValidateProtectedFieldsAsync(write, ct); if (error != null) return SealedStoreResult.Fail(400, error);
        if (authorize != null && !await authorize(ct)) return SealedStoreResult.Fail(403, "File authorization was revoked.");
        ShSealedFileStage? stage = null; var nextId = binary == null ? current.Id : Guid.NewGuid();
        try
        {
            if (binary != null)
            {
                stage = new() { Id = Guid.NewGuid(), TenantId = tenant.TenantId, ActorId = actor, FileId = nextId, VaultId = write.VaultId,
                    IdempotencyKey = "plain-replace:" + Guid.NewGuid().ToString("N"), Revision = expectedRevision, MetadataJson = JsonSerializer.Serialize(write, Json),
                    Status = "uploading", BlobStorageId = Guid.NewGuid(), ExpiresAt = DateTime.UtcNow.AddMinutes(30) };
                db.ShSealedFileStages.Add(stage); await db.SaveChangesAsync(ct);
                await using var verified = new VerifyingCiphertext(binary, record.SizeBytes, record.BinarySha256!, KnowzArchiveLimits.MaxOrdinaryFileBytes);
                await using var spool = await ProtectedSpoolStream.CreateAsync(verified, ct: ct, maxBytes: record.SizeBytes); verified.RequireComplete();
                await storage.UploadAsync(tenant.TenantId, stage.BlobStorageId.Value, spool, record.ContentType ?? "application/octet-stream", ct);
            }
            return await db.Database.CreateExecutionStrategy().ExecuteAsync<SealedStoreResult>(async () =>
            {
                db.ChangeTracker.Clear();
                if (authorize != null && !await authorize(ct)) throw new ArchiveOperationException(403, "File authorization was revoked.");
                await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
                var durable = stage == null ? null : await db.ShSealedFileStages.SingleAsync(s => s.Id == stage.Id, ct);
                if (durable?.Status == "committed") return SealedStoreResult.Success(nextId, 200, 1);
                var fresh = await GetProtectedFileAsync(db, current.Id, ct);
                if (fresh == null || !fresh.IsCurrentVersion || SelfHostedSealedStore.PayloadRevision(fresh) != expectedRevision.Trim('"')) throw new ArchiveOperationException(409, "File changed during replacement; reload before writing.");
                if (await SelfHostedInactiveShelf.IsFileInactiveAsync(db, current.Id, ct)) throw new ArchiveOperationException(409, SelfHostedInactiveShelf.ReadOnlyMessage);
                if (await ValidateProtectedFieldsAsync(write, ct) is string changed) throw new ArchiveOperationException(400, changed);
                var row = await db.FileRecords.SingleAsync(f => f.Id == current.Id, ct); var now = DateTime.UtcNow;
                FileRecord next;
                if (durable != null)
                {
                    next = Copy<FileRecord>(row); next.Id = nextId; next.Attachments = []; next.FileVersion = checked(row.FileVersion + 1); next.RootFileRecordId = row.RootFileRecordId ?? row.Id; next.PreviousFileRecordId = row.Id; next.IsCurrentVersion = true;
                    next.SizeBytes = record.SizeBytes; next.ContentType = record.ContentType; next.BlobUri = null;
                    next.ExtractedText = null; next.TranscriptionText = null; next.LayoutDataJson = null; next.TextExtractedAt = null; next.TextExtractionStatus = 0; next.TextExtractionError = null;
                    next.VisionDescription = null; next.VisionTagsJson = null; next.VisionObjectsJson = null; next.VisionExtractedText = null; next.VisionAnalyzedAt = null; next.AttachmentAIProvider = null;
                    row.IsCurrentVersion = false; row.UpdatedAt = now; db.FileRecords.Add(next);
                    db.ShFileObjectReferences.Add(new() { TenantId = tenant.TenantId, FileRecordId = nextId, BlobStorageId = durable.BlobStorageId!.Value, Sha256 = record.BinarySha256! });
                    foreach (var link in await db.FileAttachments.Where(a => a.FileRecordId == row.Id).ToListAsync(ct)) link.FileRecordId = nextId;
                    foreach (var link in await db.ShSealedAttachments.Where(a => a.FileRecordId == row.Id).ToListAsync(ct)) { link.FileRecordId = nextId; link.UpdatedAt = now; }
                    durable.Status = "committed"; durable.UpdatedAt = now;
                }
                else { next = row; db.ShPayloadKeyReferences.RemoveRange(await db.ShPayloadKeyReferences.Where(r => r.OwnerKind == "file" && r.OwnerId == row.Id).ToListAsync(ct)); }
                next.EncryptedFieldsJson = record.EncryptedFieldsJson; next.UpdatedAt = now;
                foreach (var reference in write.PayloadKeyReferences) { var item = Copy<ShPayloadKeyReference>(reference); item.Id = Guid.NewGuid(); item.OwnerId = nextId; item.TenantId = tenant.TenantId; db.ShPayloadKeyReferences.Add(item); }
                await db.SaveChangesAsync(ct);
                if (await SelfHostedInactiveShelf.IsFileInactiveAsync(db, current.Id, ct)) throw new ArchiveOperationException(409, SelfHostedInactiveShelf.ReadOnlyMessage);
                if (authorize != null && !await authorize(ct)) throw new ArchiveOperationException(403, "File authorization changed; the write was rolled back.");
                if (transaction != null) await transaction.CommitAsync(ct);
                return SealedStoreResult.Success(nextId, 200, 1);
            });
        }
        catch (Exception ex)
        {
            db.ChangeTracker.Clear();
            if (stage != null)
            {
                var failed = await db.ShSealedFileStages.SingleAsync(s => s.Id == stage.Id, CancellationToken.None);
                if (failed.Status == "committed") return SealedStoreResult.Fail(409, "Replacement committed; reload the current file before continuing.");
                if (failed.BlobStorageId.HasValue) try { await storage.DeleteAsync(tenant.TenantId, failed.BlobStorageId.Value, CancellationToken.None); failed.BlobStorageId = null; } catch { }
                failed.Status = "cancelled"; failed.ExpiresAt = DateTime.UtcNow; await db.SaveChangesAsync(CancellationToken.None);
            }
            if (ex is OperationCanceledException) throw;
            return SealedStoreResult.Fail(ex is ArchiveOperationException operation ? operation.StatusCode : ex is DbUpdateConcurrencyException ? 409 : 400,
                ex is ArchiveOperationException known ? known.Message : "File replacement was not committed; all prior bytes remain available.");
        }
    }
    private async Task<string?> ValidateProtectedFieldsAsync(ProtectedFileWrite write, CancellationToken ct)
    {
        var record = write.Record;
        if (write.PayloadKeyReferences.Any(r => r.OwnerKind != "file" || r.OwnerId != record.Id || r.Role != "field")) return "Protected fields refer to another payload owner.";
        var existingReferences = await db.ShPayloadKeyReferences.AsNoTracking().Where(r => r.OwnerKind == "file" && r.OwnerId == record.Id).ToListAsync(ct);
        if (write.PayloadKeyReferences.Any(r => existingReferences.Any(prior => prior.Role == r.Role && prior.FieldName == r.FieldName && prior.OriginalItemId != r.OriginalItemId))) return "The original file payload identity is immutable.";
        var catalog = await lifecycle.GetCatalogAsync(write.VaultId, ct); if (catalog == null) return "Vault unavailable.";
        var package = SelfHostedSealedStore.MutationPackage(catalog, write.PayloadKeyReferences.ToList());
        var validation = Copy<PortableFileRecord>(record); validation.Attachments = []; package.Data.FileRecords = [validation];
        var previous = record.PreviousFileRecordId; var seen = new HashSet<Guid> { record.Id };
        while (previous.HasValue)
        {
            if (!seen.Add(previous.Value)) return "File history is cyclic.";
            var ancestor = await GetProtectedFileAsync(db, previous.Value, ct); if (ancestor == null) return "File history is unavailable.";
            ancestor.Attachments = []; package.Data.FileRecords.Add(ancestor); previous = ancestor.PreviousFileRecordId;
            package.PayloadKeyReferences!.AddRange((await db.ShPayloadKeyReferences.AsNoTracking().Where(r => r.OwnerKind == "file" && r.OwnerId == ancestor.Id).ToListAsync(ct)).Select(Copy<PortablePayloadKeyReference>));
        }
        var error = EncryptedGraphProfile.Validate(package);
        if (error != null) return error;
        var current = await GetProtectedFileAsync(db, record.Id, ct);
        if (current == null) return "Protected file metadata unavailable.";
        return SelfHostedSealedStore.ValidateProtectedFileFieldChanges(current.EncryptedFieldsJson, record.EncryptedFieldsJson,
            existingReferences.Select(PortablePublicProjection.Reference).ToList(), write.PayloadKeyReferences, catalog);
    }
    private async Task<SealedStoreResult> DeleteProtectedFileAsync(Guid id, string expectedRevision, CancellationToken ct)
    {
        var record = await GetProtectedFileAsync(db, id, ct); if (record == null) return SealedStoreResult.Fail(404, "File unavailable.");
        if (!record.IsCurrentVersion) return SealedStoreResult.Fail(409, "Historical file versions are immutable; delete through the current version.");
        if (SelfHostedSealedStore.PayloadRevision(record) != expectedRevision.Trim('"')) return SealedStoreResult.Fail(409, "File changed; reload before deleting.");
        var root = record.RootFileRecordId ?? record.Id;
        var versions = await db.FileRecords.Where(f => f.Id == root || f.RootFileRecordId == root).ToListAsync(ct); var ids = versions.Select(f => f.Id).ToArray();
        var links = await db.ShSealedAttachments.Where(a => ids.Contains(a.FileRecordId)).ToListAsync(ct); var linkIds = links.Select(a => a.Id).ToArray();
        foreach (var link in links) { link.IsDeleted = true; link.IsPrimary = false; link.UpdatedAt = DateTime.UtcNow; }
        // Ordinary links have no tombstone field. Hide them with the logical file while keeping
        // the committed object pointers and encrypted payload identity for retention/recovery.
        foreach (var reference in await db.ShPayloadKeyReferences.Where(r => r.OwnerKind == "file" && ids.Contains(r.OwnerId)
            || r.OwnerKind == "attachment" && linkIds.Contains(r.OwnerId)).ToListAsync(ct)) reference.IsDeleted = true;
        foreach (var row in versions) { row.IsDeleted = true; row.UpdatedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync(ct);
        return SealedStoreResult.Success(id, 200, 1);
    }

}

public sealed partial class SelfHostedSealedStore
{
    internal static string? ValidateProtectedFileFieldChanges(string? previous, string? incoming,
        List<PortablePayloadKeyReference> oldReferences, List<PortablePayloadKeyReference> references, SealedCatalogResponse catalog)
        => string.IsNullOrWhiteSpace(incoming) ? "Existing protected fields cannot be omitted."
            : RequireChangedCurrentKeys(previous, incoming, oldReferences, references, catalog);
}
