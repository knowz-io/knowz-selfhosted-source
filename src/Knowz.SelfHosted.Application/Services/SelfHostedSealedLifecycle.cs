using System.Security.Cryptography;
using System.Text.Json;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Application.Services;

public sealed record SealedCatalogResponse(List<ExportedCryptoContext> Contexts, List<ExportedCryptoKeyIdentity> Keys,
    List<ExportedVaultCryptoContextLink> Links, string Revision)
{
    public List<ShAcceptedCustodyKey> AcceptedCustodyKeys { get; init; } = [];
    public bool IsEligible(ExportedCryptoKeyIdentity key) => key.SourceLifecycle is "active" or "Active"
        && (key.Custody == "customer-held-public-only" || AcceptedCustodyKeys.Any(a => a.ContextId == key.ContextId && a.Generation == key.Generation && a.Fingerprint == key.Fingerprint));
}
public sealed record ShAcceptedCustodyKey(Guid ContextId, int Generation, string Fingerprint);

public partial class SelfHostedSealedStore
{
    private static T Copy<T>(object value) => PortablePublicProjection.Project<T>(value) is T projected ? projected : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Camel), Camel)!;
    private static string Digest(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Camel))).ToLowerInvariant();
    private static string ItemRevision(ShSealedKnowledge row) => Digest(new { row.KnowledgeId, envelope = ToItem(row), row.UpdatedAt });

    public async Task<SealedCatalogResponse?> GetCatalogAsync(Guid vaultId, CancellationToken ct)
    {
        if (!await _db.Vaults.AnyAsync(v => v.Id == vaultId, ct)) return null;
        var links = await _db.ShVaultCryptoContextLinks.Where(l => l.DestinationVaultId == vaultId).AsNoTracking().OrderBy(l => l.Id).ToListAsync(ct);
        var ids = links.Select(l => l.PortableCryptoContextId).ToList();
        var contexts = (await _db.ShPortableCryptoContexts.Where(c => ids.Contains(c.Id)).AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct))
            .Select(c => { var dto = Copy<ExportedCryptoContext>(c); dto.ContextId = c.Id; return dto; }).ToList();
        var storedContexts = await _db.ShPortableCryptoContexts.Where(c => ids.Contains(c.Id)).AsNoTracking().ToListAsync(ct);
        var storedKeys = await _db.ShCryptoKeyIdentities.Where(k => ids.Contains(k.PortableCryptoContextId)).AsNoTracking().OrderBy(k => k.Id).ToListAsync(ct);
        var keys = storedKeys
            .Select(k => { var dto = Copy<ExportedCryptoKeyIdentity>(k); dto.ContextId = k.PortableCryptoContextId; return dto; }).ToList();
        var exportedLinks = links.Select(l => new ExportedVaultCryptoContextLink { VaultId = vaultId, ContextId = l.PortableCryptoContextId, IsPrimaryWriteContext = l.IsPrimaryWriteContext, PrimaryWriteFingerprint = l.PrimaryWriteFingerprint, PrimaryWriteGeneration = l.PrimaryWriteGeneration }).ToList();
        var accepted = new List<ShAcceptedCustodyKey>();
        foreach (var key in storedKeys)
        {
            var context = storedContexts.SingleOrDefault(c => c.Id == key.PortableCryptoContextId);
            if (context != null && await SelfHostedRecoveredCustodyService.IsAcceptedAsync(_db, _tenant.TenantId, context, key, ct)) accepted.Add(new(context.Id, key.Generation, key.Fingerprint));
        }
        return new(contexts, keys, exportedLinks, Digest(new { contexts, keys, links = exportedLinks, accepted })) { AcceptedCustodyKeys = accepted };
    }

    public async Task<SealedStoreResult> SelectPrimaryAsync(Guid vaultId, Guid contextId, string fingerprint, string expectedRevision, CancellationToken ct, int? generation = null)
    {
        var catalog = await GetCatalogAsync(vaultId, ct);
        if (catalog == null) return SealedStoreResult.Fail(404, "Vault unavailable.");
        if (catalog.Revision != expectedRevision.Trim('"')) return SealedStoreResult.Fail(409, "Key catalog changed; reload before selecting a key.");
        var candidates = await _db.ShCryptoKeyIdentities.Where(k => k.PortableCryptoContextId == contextId && k.Fingerprint == fingerprint).ToListAsync(ct);
        if (!generation.HasValue && candidates.Count != 1) return SealedStoreResult.Fail(400, "Select an exact key generation.");
        var key = candidates.SingleOrDefault(k => !generation.HasValue || k.Generation == generation);
        var selected = await _db.ShVaultCryptoContextLinks.SingleOrDefaultAsync(l => l.DestinationVaultId == vaultId && l.PortableCryptoContextId == contextId, ct);
        var context = await _db.ShPortableCryptoContexts.AsNoTracking().SingleOrDefaultAsync(c => c.Id == contextId, ct);
        if (key == null || selected == null || key.SourceLifecycle is not ("active" or "Active")
            || key.Custody != "customer-held-public-only" && (context == null || !await SelfHostedRecoveredCustodyService.IsAcceptedAsync(_db, _tenant.TenantId, context, key, ct))
            || context?.BindingKind != "vault" || context.AlgorithmProfile != "x25519-vault")
            return SealedStoreResult.Fail(400, "Only a linked original active key generation can be selected for new writes.");
        var links = await _db.ShVaultCryptoContextLinks.Where(l => l.DestinationVaultId == vaultId).ToListAsync(ct);
        foreach (var link in links) { link.IsPrimaryWriteContext = link.Id == selected.Id; link.PrimaryWriteFingerprint = link.Id == selected.Id ? fingerprint : null; link.PrimaryWriteGeneration = link.Id == selected.Id ? key.Generation : null; }
        key.Lifecycle = "active";
        await _db.SaveChangesAsync(ct);
        return SealedStoreResult.Success(contextId, 200, 1);
    }

    public async Task<List<object>> GetVersionsAsync(Guid knowledgeId, CancellationToken ct)
    {
        if (!await _db.KnowledgeItems.AnyAsync(k => k.Id == knowledgeId, ct)) return [];
        var sealedVersions = (await _db.ShSealedVersions.AsNoTracking().Where(v => v.KnowledgeId == knowledgeId).OrderByDescending(v => v.VersionNumber).ToListAsync(ct)).Select(Copy<SealedKnowledgeVersionRecord>);
        var plainRows = await _db.KnowledgeVersions.AsNoTracking().Where(v => v.KnowledgeId == knowledgeId).OrderByDescending(v => v.VersionNumber).ToListAsync(ct);
        var plainIds = plainRows.Select(v => v.Id).ToList();
        if (await _db.ShPayloadKeyReferences.AnyAsync(r => r.OwnerKind == "version" && plainIds.Contains(r.OwnerId) && r.Role == "body", ct))
            throw new InvalidDataException("Encrypted body history cannot be read as ordinary history.");
        var plainVersions = plainRows.Select(ToPlainVersion);
        return sealedVersions.Cast<object>().Concat(plainVersions).ToList();
    }

    public async Task<SealedStoreResult> RestoreVersionAsync(Guid knowledgeId, Guid versionId, string expectedRevision, CancellationToken ct)
    {
        if (await SelfHostedInactiveShelf.IsInactiveAsync(_db, knowledgeId, ct)) return SealedStoreResult.Fail(409, SelfHostedInactiveShelf.ReadOnlyMessage);
        var row = await _db.ShSealedKnowledge.SingleOrDefaultAsync(k => k.KnowledgeId == knowledgeId, ct);
        if (row == null) return await RestorePlainVersionAsync(knowledgeId, versionId, expectedRevision, ct);
        var version = await _db.ShSealedVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId && v.KnowledgeId == knowledgeId, ct);
        if (row == null || version == null) return SealedStoreResult.Fail(404, "Encrypted item version unavailable.");
        if (ItemRevision(row) != expectedRevision.Trim('"')) return SealedStoreResult.Fail(409, "Encrypted item changed; reload before restoring history.");
        SealedKnowledgeItem restored;
        try { restored = JsonSerializer.Deserialize<SealedKnowledgeItem>(version.EnvelopeMetadata, Camel)!; }
        catch (JsonException) { return SealedStoreResult.Fail(400, "Historical envelope is not recoverable."); }
        restored.Id = row.KnowledgeId; restored.OriginalItemId = row.OriginalItemId; restored.Ciphertext = version.Ciphertext;
        var error = ValidateItem(restored);
        if (error != null) return SealedStoreResult.Fail(400, error);
        var references = (await _db.ShPayloadKeyReferences.Where(r => r.OwnerKind == "version" && r.OwnerId == versionId).AsNoTracking().ToListAsync(ct))
            .Select(r => { var dto = Copy<PortablePayloadKeyReference>(r); dto.OwnerKind = "knowledge"; dto.OwnerId = knowledgeId; dto.VersionId = null; return dto; }).ToList();
        if (row.LineageScope.Length > 0 && references.Count == 0) return SealedStoreResult.Fail(400, "Historical key references are missing.");
        await SnapshotAsync(row, ct);
        ApplyEnvelope(row, restored);
        await ReplaceReferencesAsync("knowledge", knowledgeId, references, ct);
        await _db.SaveChangesAsync(ct);
        return SealedStoreResult.Success(knowledgeId, 200, 1);
    }

    private async Task<string?> ValidateWriteReferencesAsync(ShSealedKnowledge row, SealedItemWrite write, CancellationToken ct)
    {
        var vaults = await _db.KnowledgeVaults.Where(v => v.KnowledgeId == row.KnowledgeId).Select(v => v.VaultId).ToListAsync(ct);
        var links = await _db.ShVaultCryptoContextLinks.Where(l => vaults.Contains(l.DestinationVaultId)).ToListAsync(ct);
        var contextIds = links.Select(l => l.PortableCryptoContextId).ToList();
        var refs = write.PayloadKeyReferences ?? (await _db.ShPayloadKeyReferences.Where(r => r.OwnerKind == "knowledge" && r.OwnerId == row.KnowledgeId).AsNoTracking().ToListAsync(ct)).Select(Copy<PortablePayloadKeyReference>).ToList();
        var body = refs.SingleOrDefault(r => r.OwnerKind == "knowledge" && r.OwnerId == row.KnowledgeId && r.Role == "body");
        if (body == null || !links.Any(l => l.IsPrimaryWriteContext && l.PortableCryptoContextId == body.ContextId && l.PrimaryWriteFingerprint == body.Fingerprint && (!body.Generation.HasValue || l.PrimaryWriteGeneration == body.Generation))) return "Explicitly select the original active context before encrypted editing.";
        var contexts = (await _db.ShPortableCryptoContexts.Where(c => contextIds.Contains(c.Id)).AsNoTracking().ToListAsync(ct)).Select(c => { var dto = Copy<ExportedCryptoContext>(c); dto.ContextId = c.Id; return dto; }).ToList();
        var keys = (await _db.ShCryptoKeyIdentities.Where(k => contextIds.Contains(k.PortableCryptoContextId)).AsNoTracking().ToListAsync(ct)).Select(k => { var dto = Copy<ExportedCryptoKeyIdentity>(k); dto.ContextId = k.PortableCryptoContextId; return dto; }).ToList();
        // The HTTP mutation has already authorized the live owner. This helper validates only an
        // existing record's crypto edit; ApplyEnvelope never changes its catalog relationships.
        return EncryptedGraphProfile.Validate(new PortableExportPackage { SchemaVersion = EncryptedGraphProfile.SchemaVersion, RequiredCapabilities = [EncryptedGraphProfile.Capability], CryptoContexts = contexts, CryptoKeyCatalog = keys, SealedItems = [write.Item], PayloadKeyReferences = refs }, allowDetachedReferences: true);
    }

    private async Task ReplaceReferencesAsync(string kind, Guid owner, List<PortablePayloadKeyReference> refs, CancellationToken ct)
    {
        var old = await _db.ShPayloadKeyReferences.Where(r => r.OwnerKind == kind && r.OwnerId == owner).ToListAsync(ct);
        _db.ShPayloadKeyReferences.RemoveRange(old);
        foreach (var reference in refs)
        {
            var row = Copy<ShPayloadKeyReference>(reference); row.Id = Guid.NewGuid(); row.TenantId = _tenant.TenantId; row.OwnerKind = kind; row.OwnerId = owner;
            _db.ShPayloadKeyReferences.Add(row);
        }
    }
}
