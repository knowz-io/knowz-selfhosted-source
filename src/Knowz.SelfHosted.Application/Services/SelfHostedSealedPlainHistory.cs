using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Portability;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Application.Services;

public partial class SelfHostedSealedStore
{
    public async Task<KnowledgeVersion> StagePlainSnapshotAsync(Guid id, Guid? userId, string? description, CancellationToken ct)
    {
        var item = await GetPlainItemAsync(id, ct) ?? throw new InvalidOperationException("Knowledge item unavailable.");
        var references = (await _db.ShPayloadKeyReferences.AsNoTracking().Where(r => r.OwnerKind == "knowledge" && r.OwnerId == id).ToListAsync(ct)).Select(Copy<PortablePayloadKeyReference>).ToList();
        if (references.Any(r => r.Role != "field")) throw new InvalidOperationException("Encrypted body snapshots require the encrypted lifecycle API.");
        var error = await ValidatePlainHistoryReferencesAsync(item, references, ct);
        if (error != null) throw new InvalidOperationException("Protected history requires exact existing key references: " + error);
        var number = await _db.KnowledgeVersions.Where(v => v.KnowledgeId == id).Select(v => (int?)v.VersionNumber).MaxAsync(ct) ?? 0;
        var snapshot = new PortablePlainVersionRecord { Id = Guid.NewGuid(), KnowledgeId = id, OriginalKnowledgeId = item.OriginalItemId ?? id,
            VersionNumber = checked(number + 1), Title = item.Title, Content = item.Content, Summary = item.Summary,
            SnapshotJson = JsonSerializer.Serialize(new { encryptedFieldsJson = item.EncryptedFieldsJson }, Camel), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var row = new KnowledgeVersion { Id = snapshot.Id, TenantId = _tenant.TenantId, KnowledgeId = id, VersionNumber = checked((int)snapshot.VersionNumber),
            Title = snapshot.Title, Content = snapshot.Content, CreatedAt = snapshot.CreatedAt, UpdatedAt = snapshot.UpdatedAt,
            CreatedByUserId = userId, ChangeDescription = description, ContentType = item.Type.ToString(), PlatformData = JsonSerializer.Serialize(snapshot, Camel) };
        _db.KnowledgeVersions.Add(row);
        foreach (var reference in references) { reference.OwnerKind = "version"; reference.OwnerId = snapshot.Id; reference.VersionId = snapshot.Id; }
        await ReplaceReferencesAsync("version", snapshot.Id, references, ct);
        return row;
    }

    public static PortablePlainVersionRecord ToPlainVersion(KnowledgeVersion row)
    {
        var dto = row.PlatformData == null ? new PortablePlainVersionRecord { OriginalKnowledgeId = row.KnowledgeId }
            : JsonSerializer.Deserialize<PortablePlainVersionRecord>(row.PlatformData, Camel) ?? throw new InvalidDataException("Historical snapshot is missing.");
        if (dto.StorageStrategy != "full" || dto.DeltaContent != null) throw new InvalidDataException("Historical snapshot must be materialized before restoration.");
        dto.Id = row.Id; dto.KnowledgeId = row.KnowledgeId; dto.VersionNumber = row.VersionNumber;
        dto.Title = row.Title; dto.Content = row.Content; dto.CreatedAt = row.CreatedAt; dto.UpdatedAt = row.UpdatedAt;
        var fields = PlainVersionSnapshot.ReadEncryptedFields(dto.SnapshotJson);
        var projection = new PortableKnowledge { Title = dto.Title, Content = dto.Content, Summary = dto.Summary, EncryptedFieldsJson = fields };
        ProtectedKnowledgeFields.RedactCompanions(projection);
        dto.Title = projection.Title; dto.Content = projection.Content; dto.Summary = projection.Summary;
        if (fields != null)
        {
            using var parsed = StrictJson.Parse(fields, KnowzArchiveLimits.MaxPackageBytes);
            if (parsed.RootElement.GetProperty("fields").TryGetProperty("tags", out _)) dto.Tags = null;
        }
        return dto;
    }

    public async Task<PortableKnowledge?> GetPlainItemAsync(Guid id, CancellationToken ct)
    {
        var row = await _db.KnowledgeItems.AsNoTracking().SingleOrDefaultAsync(k => k.Id == id, ct);
        if (row == null) return null;
        var item = new PortableKnowledge { Id = row.Id, OriginalItemId = row.OriginalItemId, EncryptedFieldsJson = row.EncryptedFieldsJson,
            Title = row.Title, Content = row.Content, Summary = row.Summary, BriefSummary = row.BriefSummary, Type = row.Type,
            CreatedAt = row.CreatedAt, UpdatedAt = row.UpdatedAt,
            VaultIds = await _db.KnowledgeVaults.Where(v => v.KnowledgeId == id).OrderBy(v => v.VaultId).Select(v => v.VaultId).ToListAsync(ct) };
        ProtectedKnowledgeFields.RedactCompanions(item); return item;
    }

    private Task<SealedStoreResult> RestorePlainVersionAsync(Guid id, Guid versionId, string expectedRevision, CancellationToken ct)
        => _db.Database.CurrentTransaction != null ? RestorePlainVersionCoreAsync(id, versionId, expectedRevision, ct)
            : _db.Database.CreateExecutionStrategy().ExecuteAsync(() => RestorePlainVersionCoreAsync(id, versionId, expectedRevision, ct));

    private async Task<SealedStoreResult> RestorePlainVersionCoreAsync(Guid id, Guid versionId, string expectedRevision, CancellationToken ct)
    {
        await using var transaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            var row = await _db.KnowledgeItems.AsNoTracking().SingleOrDefaultAsync(k => k.Id == id, ct);
            var stored = await _db.KnowledgeVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId && v.KnowledgeId == id, ct);
            if (row == null || stored == null) return SealedStoreResult.Fail(404, "Item version unavailable.");
            var current = await GetPlainItemAsync(id, ct);
            if (PayloadRevision(current!) != expectedRevision.Trim('"')) return SealedStoreResult.Fail(409, "Item changed; reload before restoring history.");
            var original = row.OriginalItemId ?? row.Id;
            var target = ToPlainVersion(stored);
            if (target.OriginalKnowledgeId != original) return SealedStoreResult.Fail(409, "Historical original item identity does not match.");
            var references = (await _db.ShPayloadKeyReferences.AsNoTracking().Where(r => r.OwnerKind == "version" && r.OwnerId == versionId).ToListAsync(ct)).Select(Copy<PortablePayloadKeyReference>).ToList();
            var currentRefs = (await _db.ShPayloadKeyReferences.AsNoTracking().Where(r => r.OwnerKind == "knowledge" && r.OwnerId == id).ToListAsync(ct)).Select(Copy<PortablePayloadKeyReference>).ToList();
            if (references.Concat(currentRefs).Any(r => r.Role != "field")) return SealedStoreResult.Fail(409, "Encrypted body history cannot be restored as ordinary content.");
            var fields = PlainVersionSnapshot.ReadEncryptedFields(target.SnapshotJson);
            var restored = new PortableKnowledge { Id = id, OriginalItemId = original, EncryptedFieldsJson = fields,
                Title = target.Title, Content = target.Content, Summary = target.Summary, VaultIds = current!.VaultIds };
            ProtectedKnowledgeFields.RedactCompanions(restored);
            foreach (var reference in references) { reference.OwnerKind = "knowledge"; reference.OwnerId = id; reference.VersionId = null; }
            var error = await ValidatePlainHistoryReferencesAsync(current, currentRefs, ct) ?? await ValidatePlainHistoryReferencesAsync(restored, references, ct);
            if (error != null) return SealedStoreResult.Fail(409, error);
            var number = await _db.KnowledgeVersions.Where(v => v.KnowledgeId == id).Select(v => (int?)v.VersionNumber).MaxAsync(ct) ?? 0;
            var snapshot = new PortablePlainVersionRecord { Id = Guid.NewGuid(), KnowledgeId = id, OriginalKnowledgeId = original, VersionNumber = checked(number + 1),
                Title = current.Title, Content = current.Content, Summary = current.Summary, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                SnapshotJson = JsonSerializer.Serialize(new { encryptedFieldsJson = current.EncryptedFieldsJson }, Camel) };
            var now = DateTime.UtcNow;
            if (_db.Database.IsRelational())
            {
                var count = await _db.KnowledgeItems.Where(k => k.Id == id && k.UpdatedAt == row.UpdatedAt && k.Content == row.Content
                    && k.Title == row.Title && k.Summary == row.Summary && k.BriefSummary == row.BriefSummary
                    && k.EncryptedFieldsJson == row.EncryptedFieldsJson && k.OriginalItemId == row.OriginalItemId && k.PortableRecoveryState == null)
                    .ExecuteUpdateAsync(set => set.SetProperty(k => k.Title, restored.Title).SetProperty(k => k.Content, restored.Content)
                        .SetProperty(k => k.Summary, restored.Summary).SetProperty(k => k.BriefSummary, restored.BriefSummary)
                        .SetProperty(k => k.EncryptedFieldsJson, fields).SetProperty(k => k.OriginalItemId, original)
                        .SetProperty(k => k.UpdatedAt, now).SetProperty(k => k.IsIndexed, false).SetProperty(k => k.IndexedAt, (DateTime?)null), ct);
                if (count != 1) return SealedStoreResult.Fail(409, "Item changed; reload before restoring history.");
                foreach (var tracked in _db.ChangeTracker.Entries<Knowledge>().Where(e => e.Entity.Id == id).ToList()) tracked.State = EntityState.Detached;
            }
            else
            {
                var tracked = await _db.KnowledgeItems.SingleAsync(k => k.Id == id, ct);
                tracked.Title = restored.Title; tracked.Content = restored.Content; tracked.Summary = restored.Summary; tracked.BriefSummary = restored.BriefSummary;
                tracked.EncryptedFieldsJson = fields; tracked.OriginalItemId = original; tracked.UpdatedAt = now; tracked.IsIndexed = false; tracked.IndexedAt = null;
            }
            _db.KnowledgeVersions.Add(new() { Id = snapshot.Id, TenantId = row.TenantId, KnowledgeId = id, VersionNumber = checked((int)snapshot.VersionNumber),
                Title = snapshot.Title, Content = snapshot.Content, CreatedAt = snapshot.CreatedAt, UpdatedAt = snapshot.UpdatedAt, PlatformData = JsonSerializer.Serialize(snapshot, Camel) });
            foreach (var reference in currentRefs) { reference.OwnerKind = "version"; reference.OwnerId = snapshot.Id; reference.VersionId = snapshot.Id; }
            await ReplaceReferencesAsync("version", snapshot.Id, currentRefs, ct);
            await ReplaceReferencesAsync("knowledge", id, references, ct);
            await _db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);
            return SealedStoreResult.Success(id, 200, 1);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or KnowzArchiveException)
        {
            if (transaction != null) await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear(); return SealedStoreResult.Fail(409, "Historical snapshot or exact key references are not recoverable.");
        }
    }

    private async Task<string?> ValidatePlainHistoryReferencesAsync(PortableKnowledge item, List<PortablePayloadKeyReference> references, CancellationToken ct)
    {
        var links = await _db.ShVaultCryptoContextLinks.Where(l => item.VaultIds.Contains(l.DestinationVaultId)).AsNoTracking().ToListAsync(ct);
        var ids = links.Select(l => l.PortableCryptoContextId).ToList();
        var contexts = (await _db.ShPortableCryptoContexts.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(ct)).Select(c => { var dto = Copy<ExportedCryptoContext>(c); dto.ContextId = c.Id; return dto; }).ToList();
        var keys = (await _db.ShCryptoKeyIdentities.AsNoTracking().Where(k => ids.Contains(k.PortableCryptoContextId)).ToListAsync(ct)).Select(k => { var dto = Copy<ExportedCryptoKeyIdentity>(k); dto.ContextId = k.PortableCryptoContextId; return dto; }).ToList();
        return EncryptedGraphProfile.Validate(new() { SchemaVersion = EncryptedGraphProfile.SchemaVersion, RequiredCapabilities = [EncryptedGraphProfile.Capability],
            Data = new() { KnowledgeItems = [item] }, CryptoContexts = contexts, CryptoKeyCatalog = keys, PayloadKeyReferences = references }, allowDetachedReferences: true);
    }
}
