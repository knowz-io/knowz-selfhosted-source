using System.Text.Json;
using System.Text.Json.Nodes;
using Knowz.Core.Entities;
using Knowz.Core.Portability;
using Microsoft.EntityFrameworkCore;
namespace Knowz.SelfHosted.Application.Services;
public partial class SelfHostedSealedStore
{
    private Task<SealedStoreResult> UpdateCurrentFieldsAsync(Guid id, SealedItemWrite write, string? revision, CancellationToken ct)
        => _db.Database.CurrentTransaction != null ? UpdateCurrentFieldsCoreAsync(id,write,revision,ct)
            : _db.Database.CreateExecutionStrategy().ExecuteAsync(() => UpdateCurrentFieldsCoreAsync(id,write,revision,ct));

    private async Task<SealedStoreResult> UpdateCurrentFieldsCoreAsync(Guid id, SealedItemWrite write, string? revision, CancellationToken ct)
    {
        await using var transaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            if (await SelfHostedInactiveShelf.IsInactiveAsync(_db,id,ct)) return SealedStoreResult.Fail(409,SelfHostedInactiveShelf.ReadOnlyMessage);
            var row = await _db.KnowledgeItems.AsNoTracking().SingleOrDefaultAsync(k => k.Id == id,ct);
            if (row == null) return SealedStoreResult.Fail(404,"Item unavailable.");
            if (await _db.ShSealedKnowledge.AnyAsync(k => k.KnowledgeId == id,ct)) return SealedStoreResult.Fail(400,"Encrypted body mode cannot change through a field edit.");
            var current = (await GetPlainItemAsync(id,ct))!; var incoming = write.PlainItem!;
            if (revision?.Trim('"') != PayloadRevision(current)) return SealedStoreResult.Fail(409,"Item changed; reload before editing.");
            if (incoming.Id != id || incoming.OriginalItemId != (row.OriginalItemId ?? id)) return SealedStoreResult.Fail(400,"Original item identity cannot change.");
            if (write.VaultId == null || !current.VaultIds.Contains(write.VaultId.Value)) return SealedStoreResult.Fail(400,"The selected vault does not own this item.");
            if (string.IsNullOrWhiteSpace(incoming.EncryptedFieldsJson) || write.PayloadKeyReferences == null || incoming.Content == null)
                return SealedStoreResult.Fail(400,"Ordinary content, protected fields and exact references are required.");
            if (incoming.VaultIds == null || !incoming.VaultIds.ToHashSet().SetEquals(current.VaultIds) || incoming.Type != current.Type)
                return SealedStoreResult.Fail(400,"Field edits cannot change item ownership or type.");
            var oldRefs = (await _db.ShPayloadKeyReferences.AsNoTracking().Where(r => r.OwnerKind == "knowledge" && r.OwnerId == id).ToListAsync(ct)).Select(Copy<PortablePayloadKeyReference>).ToList();
            var error = await ValidatePlainHistoryReferencesAsync(current,oldRefs,ct) ?? await ValidatePlainHistoryReferencesAsync(incoming,write.PayloadKeyReferences,ct);
            if (error != null) return SealedStoreResult.Fail(400,error);
            ProtectedKnowledgeFields.RedactCompanions(incoming);
            if (incoming.Title == null) return SealedStoreResult.Fail(400,"An ordinary title value is required.");
            var catalog = await GetCatalogAsync(write.VaultId.Value,ct);
            if (catalog == null) return SealedStoreResult.Fail(409,"The selected vault changed; reload before editing.");
            error = RequireChangedCurrentKeys(current.EncryptedFieldsJson,incoming.EncryptedFieldsJson,oldRefs,write.PayloadKeyReferences,catalog);
            if (error != null) return SealedStoreResult.Fail(400,error);
            await StagePlainSnapshotAsync(id,null,"Protected field edit",ct);
            var now = DateTime.UtcNow;
            if (_db.Database.IsRelational())
            {
                var count = await _db.KnowledgeItems.Where(k => k.Id == id && k.UpdatedAt == row.UpdatedAt && k.Content == row.Content && k.Title == row.Title
                    && k.Summary == row.Summary && k.BriefSummary == row.BriefSummary && k.EncryptedFieldsJson == row.EncryptedFieldsJson
                    && k.OriginalItemId == row.OriginalItemId && k.PortableRecoveryState == null).ExecuteUpdateAsync(set => set
                        .SetProperty(k => k.Title,incoming.Title).SetProperty(k => k.Content,incoming.Content).SetProperty(k => k.Summary,incoming.Summary)
                        .SetProperty(k => k.BriefSummary,incoming.BriefSummary).SetProperty(k => k.EncryptedFieldsJson,incoming.EncryptedFieldsJson)
                        .SetProperty(k => k.OriginalItemId,incoming.OriginalItemId).SetProperty(k => k.UpdatedAt,now)
                        .SetProperty(k => k.IsIndexed,false).SetProperty(k => k.IndexedAt,(DateTime?)null),ct);
                if (count != 1) { _db.ChangeTracker.Clear(); return SealedStoreResult.Fail(409,"Item changed; reload before editing."); }
                foreach (var tracked in _db.ChangeTracker.Entries<Knowledge>().Where(e => e.Entity.Id == id).ToList()) tracked.State = EntityState.Detached;
            }
            else
            {
                var tracked = await _db.KnowledgeItems.SingleAsync(k => k.Id == id,ct);
                tracked.Title=incoming.Title; tracked.Content=incoming.Content; tracked.Summary=incoming.Summary; tracked.BriefSummary=incoming.BriefSummary;
                tracked.EncryptedFieldsJson=incoming.EncryptedFieldsJson; tracked.OriginalItemId=incoming.OriginalItemId; tracked.UpdatedAt=now; tracked.IsIndexed=false; tracked.IndexedAt=null;
            }
            await ReplaceReferencesAsync("knowledge",id,write.PayloadKeyReferences,ct);
            await _db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);
            return SealedStoreResult.Success(id,200,1);
        }
        catch
        {
            if (transaction != null) await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear(); throw;
        }
    }

    private static string? RequireChangedCurrentKeys(string? previous,string incoming,List<PortablePayloadKeyReference> oldRefs,
        List<PortablePayloadKeyReference> refs,SealedCatalogResponse catalog)
    {
        var oldFields = previous == null ? null : JsonNode.Parse(previous)?["fields"];
        var fields = JsonNode.Parse(incoming)!["fields"]!;
        if (oldFields is JsonObject old && old.Any(field => fields[field.Key] == null)) return "Existing protected fields cannot be omitted.";
        var changed = new List<PortablePayloadKeyReference>();
        foreach (var reference in refs)
        {
            if (!JsonNode.DeepEquals(oldFields?[reference.FieldName!],fields[reference.FieldName!])) changed.Add(reference);
            else if (!oldRefs.Any(old => Digest(old) == Digest(reference)))
                return "Unchanged ciphertext must retain its exact original key attribution.";
        }
        return ValidatePrimaryReferences(catalog,changed);
    }
}
