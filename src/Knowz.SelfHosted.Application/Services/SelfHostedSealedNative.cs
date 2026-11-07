using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowz.Core.Entities;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Application.Services;

public partial class SelfHostedSealedStore
{
    public static string PayloadRevision(object record) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(record))).ToLowerInvariant();

    private async Task<SealedStoreResult> CreateGraphItemAsync(SealedItemWrite write, string? expectedRevision, CancellationToken ct)
    {
        if (!write.VaultId.HasValue || write.Item == null || write.Item.Id == Guid.Empty) return SealedStoreResult.Fail(400, "Vault and encrypted item identity are required.");
        var catalog = await GetCatalogAsync(write.VaultId.Value, ct);
        if (catalog == null) return SealedStoreResult.Fail(404, "Vault unavailable.");
        if (expectedRevision?.Trim('"') != catalog.Revision) return SealedStoreResult.Fail(409, "Vault key catalog changed; reload before creating an item.");
        if (await _db.KnowledgeItems.IgnoreQueryFilters().AnyAsync(k => k.Id == write.Item.Id, ct)) return SealedStoreResult.Fail(409, "Item identity already exists.");
        var package = MutationPackage(catalog, write.PayloadKeyReferences!); package.SealedItems = [write.Item];
        package.Data.Vaults = [new() { Id = write.VaultId.Value }];
        var error = EncryptedGraphProfile.Validate(package) ?? ValidatePrimaryReferences(catalog, write.PayloadKeyReferences!);
        if (error != null) return SealedStoreResult.Fail(400, error);
        var row = Copy<ShSealedKnowledge>(write.Item); row.Id = Guid.NewGuid(); row.KnowledgeId = write.Item.Id; row.TenantId = _tenant.TenantId; row.LineageScope = "native";
        row.LifecycleStatus = LockedStatus; row.CreatedAt = row.UpdatedAt = DateTime.UtcNow;
        _db.KnowledgeItems.Add(new Knowledge { Id = row.KnowledgeId, TenantId = _tenant.TenantId, Title = LabelOrDefault(write.Label), Content = "", Type = write.Item.Type, CreatedAt = row.CreatedAt, UpdatedAt = row.UpdatedAt });
        _db.KnowledgeVaults.Add(new KnowledgeVault { TenantId = _tenant.TenantId, KnowledgeId = row.KnowledgeId, VaultId = write.VaultId.Value, IsPrimary = true });
        _db.ShSealedKnowledge.Add(row);
        await ReplaceReferencesAsync("knowledge", row.KnowledgeId, write.PayloadKeyReferences!, ct);
        await _db.SaveChangesAsync(ct);
        return SealedStoreResult.Success(row.KnowledgeId, 201, 1);
    }

    private async Task<SealedStoreResult> WriteGraphCommentAsync(Guid knowledgeId, SealedCommentWrite write, bool create, string? expectedRevision, CancellationToken ct)
    {
        if (await SelfHostedInactiveShelf.IsInactiveAsync(_db, knowledgeId, ct)) return SealedStoreResult.Fail(409, SelfHostedInactiveShelf.ReadOnlyMessage);
        var item = await _db.ShSealedKnowledge.SingleOrDefaultAsync(k => k.KnowledgeId == knowledgeId, ct);
        var plain = item == null ? await GetPlainItemAsync(knowledgeId, ct) : null;
        if (item == null && plain == null) return SealedStoreResult.Fail(404, "Parent item unavailable.");
        var originalItemId = item?.OriginalItemId ?? plain!.OriginalItemId ?? plain.Id;
        var vault = write.VaultId ?? await _db.KnowledgeVaults.Where(v => v.KnowledgeId == knowledgeId).Select(v => v.VaultId).FirstOrDefaultAsync(ct);
        if (!await _db.KnowledgeVaults.AnyAsync(v => v.KnowledgeId == knowledgeId && v.VaultId == vault, ct)) return SealedStoreResult.Fail(400, "Comment vault does not own its item.");
        var catalog = await GetCatalogAsync(vault, ct);
        if (catalog == null) return SealedStoreResult.Fail(409, "The selected vault changed; reload before writing.");
        var record = write.Comment;
        if (record == null || record.Id == Guid.Empty || record.OriginalCommentId == Guid.Empty || write.PayloadKeyReferences == null) return SealedStoreResult.Fail(400, "Comment identity and exact payload key references are required.");
        var existing = await _db.ShSealedComments.SingleOrDefaultAsync(c => c.Id == record.Id, ct);
        if (create ? existing != null : existing == null) return SealedStoreResult.Fail(create ? 409 : 404, "Encrypted comment identity is unavailable.");
        if (expectedRevision?.Trim('"') != (create ? catalog!.Revision : PayloadRevision(ToComment(existing!)))) return SealedStoreResult.Fail(409, "Encrypted comment or vault changed; reload before writing.");
        if (existing != null && (existing.KnowledgeId != knowledgeId || existing.OriginalCommentId != record.OriginalCommentId || existing.OriginalKnowledgeId != record.OriginalKnowledgeId)) return SealedStoreResult.Fail(400, "Immutable comment owner cannot change.");
        if (record.KnowledgeId != knowledgeId || record.OriginalKnowledgeId != originalItemId) return SealedStoreResult.Fail(400, "Comment owner binding is invalid.");
        if (record.ParentCommentId.HasValue && !await _db.ShSealedComments.AnyAsync(c => c.Id == record.ParentCommentId && c.KnowledgeId == knowledgeId, ct)) return SealedStoreResult.Fail(400, "Parent comment is unavailable.");
        var package = MutationPackage(catalog!, write.PayloadKeyReferences);
        package.Data.KnowledgeItems = [new() { Id = knowledgeId }];
        if (record.ParentCommentId.HasValue) package.Data.Comments = [new() { Id = record.ParentCommentId.Value, KnowledgeId = knowledgeId }];
        package.SealedComments = [record];
        var error = EncryptedGraphProfile.Validate(package);
        if (error == null)
        {
            var previous = existing == null ? null : ToComment(existing);
            var oldRefs = existing == null ? [] : (await _db.ShPayloadKeyReferences.AsNoTracking().Where(r => r.OwnerKind == "comment" && r.OwnerId == existing.Id).ToListAsync(ct)).Select(Copy<PortablePayloadKeyReference>).ToList();
            error = RequireChangedCommentKeys(previous,record,oldRefs,write.PayloadKeyReferences,catalog);
        }
        if (error != null) return SealedStoreResult.Fail(400, error);
        var row = existing ?? Copy<ShSealedComment>(record);
        if (existing == null) { row.TenantId = _tenant.TenantId; row.LineageScope = "native"; _db.ShSealedComments.Add(row); }
        ApplyCommentEnvelope(row, record); row.ParentCommentId = record.ParentCommentId;
        await ReplaceReferencesAsync("comment", row.Id, write.PayloadKeyReferences, ct);
        await _db.SaveChangesAsync(ct);
        return SealedStoreResult.Success(row.Id, create ? 201 : 200, 1);
    }

    private static string? RequireChangedCommentKeys(SealedCommentRecord? previous, SealedCommentRecord incoming,
        List<PortablePayloadKeyReference> oldRefs, List<PortablePayloadKeyReference> refs, SealedCatalogResponse catalog)
    {
        if (previous == null) return ValidatePrimaryReferences(catalog,refs);
        if (oldRefs.Any(old => !refs.Any(reference => reference.Role == old.Role && reference.FieldName == old.FieldName)))
            return "Existing protected comment payloads cannot be omitted.";
        var oldFields = previous.EncryptedFieldsJson == null ? null : JsonNode.Parse(previous.EncryptedFieldsJson)?["fields"];
        var fields = incoming.EncryptedFieldsJson == null ? null : JsonNode.Parse(incoming.EncryptedFieldsJson)?["fields"];
        var sameBody = previous.Ciphertext == incoming.Ciphertext && previous.EncryptedDek == incoming.EncryptedDek
            && previous.EncryptedDekNonce == incoming.EncryptedDekNonce && previous.EphemeralPublicKey == incoming.EphemeralPublicKey
            && previous.ContentNonce == incoming.ContentNonce && previous.EnvelopeVersion == incoming.EnvelopeVersion
            && previous.CryptoBindingId == incoming.CryptoBindingId && previous.EncryptionKeyRef == incoming.EncryptionKeyRef
            && previous.AlgorithmProfile == incoming.AlgorithmProfile;
        var changed = new List<PortablePayloadKeyReference>();
        foreach (var reference in refs)
        {
            var unchanged = reference.Role == "body" ? sameBody : JsonNode.DeepEquals(oldFields?[reference.FieldName!],fields?[reference.FieldName!]);
            if (!unchanged) changed.Add(reference);
            else if (!oldRefs.Any(old => Digest(old) == Digest(reference)))
                return "Unchanged ciphertext must retain its exact original key attribution.";
        }
        return ValidatePrimaryReferences(catalog,changed);
    }

    internal static PortableExportPackage MutationPackage(SealedCatalogResponse catalog, List<PortablePayloadKeyReference> refs)
        => new() { SchemaVersion = EncryptedGraphProfile.SchemaVersion, RequiredCapabilities = [EncryptedGraphProfile.Capability], CryptoContexts = catalog.Contexts, CryptoKeyCatalog = catalog.Keys, PayloadKeyReferences = refs };
    internal static string? ValidatePrimaryReferences(SealedCatalogResponse catalog, List<PortablePayloadKeyReference> refs)
    {
        foreach (var reference in refs)
        {
            var key = catalog.Keys.SingleOrDefault(k => k.ContextId == reference.ContextId && k.Fingerprint == reference.Fingerprint && (!reference.Generation.HasValue || k.Generation == reference.Generation));
            if (key == null || !catalog.IsEligible(key) || !catalog.Links.Any(l => l.IsPrimaryWriteContext && l.ContextId == reference.ContextId && l.PrimaryWriteFingerprint == reference.Fingerprint && l.PrimaryWriteGeneration == key.Generation)) return "Select the exact active original key generation before writing encrypted payloads.";
        }
        return null;
    }
}
