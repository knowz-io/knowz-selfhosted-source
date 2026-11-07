using System.Text.Json;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Application.Services;

public sealed partial class SelfHostedSealedFiles
{
    public async Task<SealedFileResult> UpdateAsync(Guid actor, SealedFileWrite write, string expectedRevision, Stream? binary, CancellationToken ct, Func<CancellationToken, Task<bool>>? authorize = null)
    {
        var row = await db.ShSealedFiles.SingleOrDefaultAsync(f => f.Id == write.Record.Id, ct);
        if (row == null) return Fail(404, "Encrypted file unavailable.");
        var current = Copy<SealedFileRecord>(row);
        if (!row.IsCurrentVersion) return Fail(409, "Historical file versions are immutable; edit the current version.");
        if (SelfHostedSealedStore.PayloadRevision(current) != expectedRevision.Trim('"')) return Fail(409, "Encrypted file changed; reload before editing.");
        if (write.Record.OriginalFileId != row.OriginalFileId || write.Record.KnowledgeId != row.KnowledgeId || write.Record.VaultId != row.VaultId || write.Attachments.Count > 0) return Fail(400, "Immutable file identity and owners cannot change.");
        if (write.Record.FileVersion != row.FileVersion || write.Record.RootFileRecordId != row.RootFileRecordId || write.Record.PreviousFileRecordId != row.PreviousFileRecordId || !write.Record.IsCurrentVersion) return Fail(400, "File version lineage is server controlled.");
        if (write.PayloadKeyReferences.Any(r => r.OwnerKind != "file" || r.OwnerId != row.Id)) return Fail(400, "File edit contains references to another owner.");
        if (binary == null && Body(current) != Body(write.Record)) return Fail(400, "Changing file encryption or bytes requires an atomic binary replacement.");
        var catalog = await lifecycle.GetCatalogAsync(write.VaultId, ct); if (catalog == null) return Fail(404, "Vault unavailable.");
        var error = await ValidateAsync(write, catalog, ct, true); if (error != null) return Fail(400, error);
        ShSealedFileStage? stage = null; var oldBlob = row.BlobStorageId; var nextId = binary != null ? Guid.NewGuid() : row.Id;
        try
        {
            if (binary != null)
            {
                stage = new() { Id = Guid.NewGuid(), TenantId = tenant.TenantId, ActorId = actor, FileId = nextId, VaultId = write.VaultId, IdempotencyKey = "replace:" + Guid.NewGuid().ToString("N"), Revision = expectedRevision,
                    MetadataJson = JsonSerializer.Serialize(write, Json), Status = "uploading", BlobStorageId = Guid.NewGuid(), ExpiresAt = DateTime.UtcNow.AddMinutes(30) };
                db.ShSealedFileStages.Add(stage); await db.SaveChangesAsync(ct);
                await using var verified = new VerifyingCiphertext(binary, write.Record.SizeBytes, write.Record.BinarySha256!);
                await using var spool = await ProtectedSpoolStream.CreateAsync(verified, ct: ct); verified.RequireComplete();
                await storage.UploadAsync(tenant.TenantId, stage.BlobStorageId.Value, spool, "application/octet-stream", ct);
            }
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
                if (authorize != null && !await authorize(ct)) throw new UnauthorizedAccessException("File authorization was revoked.");
                var persistedStage = stage == null ? null : await db.ShSealedFileStages.SingleAsync(s => s.Id == stage.Id, ct);
                if (persistedStage?.Status == "committed")
                {
                    var committed = await db.ShSealedFiles.AsNoTracking().SingleAsync(f => f.Id == nextId, ct);
                    return Result(committed);
                }
                var fresh = await db.ShSealedFiles.SingleOrDefaultAsync(f => f.Id == write.Record.Id, ct);
                if (fresh == null || !fresh.IsCurrentVersion || SelfHostedSealedStore.PayloadRevision(Copy<SealedFileRecord>(fresh)) != expectedRevision.Trim('"'))
                    throw new DbUpdateConcurrencyException("File changed during replacement.");
                var freshCatalog = await lifecycle.GetCatalogAsync(write.VaultId, ct);
                if (freshCatalog == null || await ValidateAsync(write, freshCatalog, ct, true) != null) throw new InvalidDataException("File graph changed during editing.");
                var updated = Copy<ShSealedFile>(write.Record); updated.TenantId = tenant.TenantId; updated.BlobStorageId = persistedStage?.BlobStorageId ?? oldBlob; updated.BinaryContentBase64 = null;
                updated.CreatedAt = fresh.CreatedAt; updated.UpdatedAt = DateTime.UtcNow;
                if (persistedStage != null)
                {
                    updated.Id = nextId; updated.FileVersion = checked(fresh.FileVersion + 1);
                    updated.RootFileRecordId = fresh.RootFileRecordId ?? fresh.Id; updated.PreviousFileRecordId = fresh.Id; updated.IsCurrentVersion = true;
                    fresh.IsCurrentVersion = false; fresh.UpdatedAt = updated.UpdatedAt; db.ShSealedFiles.Add(updated);
                    foreach (var attachment in await db.ShSealedAttachments.Where(a => a.FileRecordId == fresh.Id).ToListAsync(ct))
                    { attachment.FileRecordId = nextId; attachment.UpdatedAt = updated.UpdatedAt; }
                }
                else
                {
                    db.Entry(fresh).CurrentValues.SetValues(updated);
                    db.ShPayloadKeyReferences.RemoveRange(await db.ShPayloadKeyReferences.Where(r => r.OwnerKind == "file" && r.OwnerId == fresh.Id).ToListAsync(ct));
                }
                foreach (var reference in write.PayloadKeyReferences) { var item = Copy<ShPayloadKeyReference>(reference); item.Id = Guid.NewGuid(); item.OwnerId = nextId; item.TenantId = tenant.TenantId; db.ShPayloadKeyReferences.Add(item); }
                if (persistedStage != null) { persistedStage.Status = "committed"; persistedStage.UpdatedAt = DateTime.UtcNow; }
                await db.SaveChangesAsync(ct); if (transaction != null) await transaction.CommitAsync(ct);
                return Result(updated);
            });
            SealedFileResult Result(ShSealedFile updated)
            {
                var response = Copy<SealedFileWrite>(write); response.Record = Copy<SealedFileRecord>(updated);
                response.PayloadKeyReferences = response.PayloadKeyReferences.Select(r => { r.OwnerId = nextId; return r; }).ToList();
                return new(true, 200, Revision: SelfHostedSealedStore.PayloadRevision(response.Record), Write: response);
            }
        }
        catch (Exception ex)
        {
            db.ChangeTracker.Clear();
            if (stage != null && await db.ShSealedFileStages.AnyAsync(s => s.Id == stage.Id && s.Status == "committed", CancellationToken.None))
                return Fail(409, "Replacement committed; reload the current file before continuing.");
            var cleaned = stage?.BlobStorageId == null;
            if (stage?.BlobStorageId is Guid blob) try { await storage.DeleteAsync(tenant.TenantId, blob, CancellationToken.None); cleaned = true; } catch { /* Durable stage retains cleanup ownership. */ }
            if (stage != null) { var failed = await db.ShSealedFileStages.SingleAsync(s => s.Id == stage.Id, CancellationToken.None); failed.Status = "cancelled"; if (cleaned) failed.BlobStorageId = null; failed.ExpiresAt = DateTime.UtcNow; await db.SaveChangesAsync(CancellationToken.None); }
            if (ex is OperationCanceledException) throw;
            return Fail(ex is DbUpdateConcurrencyException ? 409 : 400, "File replacement was not committed; prior bytes remain available.");
        }
        static string Body(SealedFileRecord value) => JsonSerializer.Serialize(new { value.OriginalFileId, value.SizeBytes, value.OriginalSizeBytes, value.BinarySha256, value.EnvelopeVersion, value.EncryptedDek, value.EncryptedDekNonce, value.EphemeralPublicKey, value.ContentNonce, value.CryptoBindingId });
    }
}
