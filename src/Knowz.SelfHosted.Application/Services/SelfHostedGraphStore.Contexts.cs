using Knowz.Core.Portability;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Application.Services;

public sealed partial class SelfHostedGraphStore
{
    public async Task<IReadOnlyDictionary<Guid, Guid>> ResolvePeerContextMappingsAsync(PortableExportPackage source,
        Guid vaultId, string lineage, CancellationToken ct, bool requirePersisted = false)
    {
        if (!await db.Vaults.AsNoTracking().AnyAsync(v => v.Id == vaultId && v.TenantId == tenant.TenantId && !v.IsDeleted, ct))
            throw new InvalidDataException("The destination vault is unavailable for original context resolution.");
        var scope = ImportScope(source, vaultId, lineage, null);
        var contexts = await (from link in db.ShVaultCryptoContextLinks.AsNoTracking()
            join context in db.ShPortableCryptoContexts.AsNoTracking() on link.PortableCryptoContextId equals context.Id
            where link.TenantId == tenant.TenantId && link.DestinationVaultId == vaultId && context.TenantId == tenant.TenantId
            select context).Distinct().ToListAsync(ct);
        var ids = contexts.Select(c => c.Id).ToArray();
        var keys = await db.ShCryptoKeyIdentities.AsNoTracking().Where(k => k.TenantId == tenant.TenantId && ids.Contains(k.PortableCryptoContextId)).ToListAsync(ct);
        var mappings = new Dictionary<Guid, Guid>();
        foreach (var incoming in source.CryptoContexts ?? [])
        {
            var catalog = (source.CryptoKeyCatalog ?? []).Where(k => k.ContextId == incoming.ContextId).ToList();
            var related = contexts.Where(c => c.OriginalBindingId == incoming.OriginalBindingId).ToList();
            if (requirePersisted && related.Count == 0)
                throw new InvalidDataException("The reviewed original encryption context is no longer linked to the destination vault.");
            if (related.Any(c => c.BindingKind != incoming.BindingKind || c.AlgorithmProfile != incoming.AlgorithmProfile))
                throw new InvalidDataException("The linked original encryption context has a contradictory binding or algorithm profile.");
            var keylessMetadata = catalog.Count == 0 && !(source.PayloadKeyReferences ?? []).Any(r => r.ContextId == incoming.ContextId);
            var candidates = new List<Knowz.SelfHosted.Infrastructure.Data.Entities.ShPortableCryptoContext>();
            foreach (var candidate in related)
            {
                var existing = keys.Where(k => k.PortableCryptoContextId == candidate.Id).ToList();
                if (keylessMetadata)
                {
                    if (existing.Count != 0) throw new InvalidDataException("Keyless context metadata cannot adopt an existing keyed encryption context.");
                    candidates.Add(candidate); continue;
                }
                var overlapping = catalog.Select(k => (Incoming: k, Stored: existing.SingleOrDefault(s => s.Generation == k.Generation)))
                    .Where(pair => pair.Stored != null).ToList();
                if (requirePersisted && overlapping.Count != catalog.Count)
                    throw new InvalidDataException("An imported original key generation is no longer present at the destination.");
                if (overlapping.Count == 0 || overlapping.Any(pair => pair.Stored!.Fingerprint != pair.Incoming.Fingerprint
                    || pair.Stored.PublicKey != pair.Incoming.PublicKey || pair.Stored.KeyType != pair.Incoming.KeyType || pair.Stored.Custody != pair.Incoming.Custody))
                    throw new InvalidDataException("The linked original encryption context has an unanchored or contradictory key generation identity.");
                candidates.Add(candidate);
            }
            if (candidates.Count > 1)
            {
                if (keylessMetadata) throw new InvalidDataException("The destination has ambiguous keyless context metadata for this vault.");
                var anchored = await ResolveImmutableVersionContextAsync(source, incoming.ContextId, vaultId, candidates.Select(c => c.Id).ToHashSet(), ct);
                candidates.RemoveAll(c => c.Id != anchored);
            }
            mappings[incoming.ContextId] = candidates.Count == 1 ? candidates[0].Id : DestinationId(tenant.TenantId, scope, "context", incoming.ContextId);
        }
        return mappings;
    }

    private async Task<Guid> ResolveImmutableVersionContextAsync(PortableExportPackage source, Guid incomingContext,
        Guid vaultId, HashSet<Guid> candidates, CancellationToken ct)
    {
        var ownershipError = await SyncOwnershipErrorAsync(source, vaultId, ct);
        if (ownershipError != null) throw new InvalidDataException(ownershipError);
        var references = (source.PayloadKeyReferences ?? []).Where(r => r.OwnerKind == "version" && r.ContextId == incomingContext).ToList();
        var ids = references.Select(r => r.OwnerId).Distinct().ToArray();
        var declared = (source.SealedVersions ?? []).Select(v => (v.Id, v.KnowledgeId, v.OriginalKnowledgeId))
            .Concat((source.PlainVersions ?? []).Select(v => (v.Id, v.KnowledgeId, v.OriginalKnowledgeId))).ToDictionary(v => v.Id);
        var stored = (await db.ShSealedVersions.AsNoTracking().Where(v => v.TenantId == tenant.TenantId && !v.IsDeleted && ids.Contains(v.Id)).ToListAsync(ct))
            .Select(v => (v.Id, v.KnowledgeId, v.OriginalKnowledgeId)).ToList();
        foreach (var row in await db.KnowledgeVersions.AsNoTracking().Where(v => v.TenantId == tenant.TenantId && !v.IsDeleted && ids.Contains(v.Id)).ToListAsync(ct))
        {
            var version = SelfHostedSealedStore.ToPlainVersion(row);
            stored.Add((row.Id, row.KnowledgeId, version.OriginalKnowledgeId));
        }
        if (stored.GroupBy(v => v.Id).Any(g => g.Count() != 1))
            throw new InvalidDataException("Immutable version identity is ambiguous at the destination.");
        var storedOwners = stored.ToDictionary(v => v.Id);
        var parents = stored.Select(v => v.KnowledgeId).Distinct().ToArray();
        var liveParents = await db.KnowledgeItems.AsNoTracking().Where(k => k.TenantId == tenant.TenantId && !k.IsDeleted
            && k.PortableRecoveryState == null && parents.Contains(k.Id)
            && db.KnowledgeVaults.Any(l => l.TenantId == tenant.TenantId && l.KnowledgeId == k.Id && l.VaultId == vaultId))
            .Select(k => k.Id).ToListAsync(ct);
        var existingReferences = await db.ShPayloadKeyReferences.AsNoTracking().Where(r => r.TenantId == tenant.TenantId && !r.IsDeleted
            && r.OwnerKind == "version" && ids.Contains(r.OwnerId)).ToListAsync(ct);
        var anchors = new HashSet<Guid>();
        foreach (var incoming in references)
        {
            if (!storedOwners.TryGetValue(incoming.OwnerId, out var owner)) continue; // New history cannot establish prior local context identity.
            if (!declared.TryGetValue(owner.Id, out var sourceOwner) || owner.KnowledgeId != sourceOwner.KnowledgeId
                || owner.OriginalKnowledgeId != sourceOwner.OriginalKnowledgeId
                || !liveParents.Contains(owner.KnowledgeId))
                throw new InvalidDataException("Immutable version context ownership is unavailable or contradictory.");
            var matching = existingReferences.Where(r => r.OwnerId == incoming.OwnerId && r.Role == incoming.Role && r.FieldName == incoming.FieldName
                && r.OriginalBindingKind == incoming.OriginalBindingKind && r.OriginalBindingId == incoming.OriginalBindingId && r.OriginalItemId == incoming.OriginalItemId
                && r.Generation == incoming.Generation && r.Fingerprint == incoming.Fingerprint && r.KeyType == incoming.KeyType
                && r.AlgorithmProfile == incoming.AlgorithmProfile && r.EnvelopeVersion == incoming.EnvelopeVersion).ToList();
            if (matching.Count != 1 || !candidates.Contains(matching[0].ContextId))
                throw new InvalidDataException("Immutable version key attribution is unavailable or contradictory.");
            anchors.Add(matching[0].ContextId);
        }
        if (anchors.Count != 1) throw new InvalidDataException("The destination has ambiguous or conflicting immutable version context anchors.");
        return anchors.Single();
    }
}
