using Knowz.Core.Portability;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Application.Services;

public static class SelfHostedInlineSyncPolicy
{
    public const string Message = "Protected vault graphs require a registered complete archive transfer (archive-peer-required). Use the saved connection archive-peer registration and transfer actions.";
    public static bool HasProtectedGraph(PortableExportPackage p) => p.SchemaVersion is EncryptedGraphProfile.SchemaVersion or EncryptedPortabilityProfile.SchemaVersion
        || p.CryptoContexts is { Count: > 0 } || p.CryptoKeyCatalog is { Count: > 0 } || p.PayloadKeyReferences is { Count: > 0 }
        || p.SealedItems is { Count: > 0 } || p.SealedComments is { Count: > 0 } || p.SealedFiles is { Count: > 0 } || p.SealedVersions is { Count: > 0 }
        || p.SealedAttachments is { Count: > 0 } || p.DependencyBoundItems is { Count: > 0 } || p.SuspendedReleasePolicies is { Count: > 0 }
        || p.Data.FileRecords.Any(f => f.EncryptedFieldsJson != null);
    public static async Task<bool> IsProtectedVaultAsync(SelfHostedDbContext db, Guid tenant, Guid vault, CancellationToken ct)
    {
        var items = db.KnowledgeVaults.IgnoreQueryFilters().Where(v => v.TenantId == tenant && v.VaultId == vault).Select(v => v.KnowledgeId);
        return await db.ShVaultCryptoContextLinks.IgnoreQueryFilters().AnyAsync(l => l.TenantId == tenant && l.DestinationVaultId == vault, ct)
            || await db.ShSealedKnowledge.IgnoreQueryFilters().AnyAsync(k => k.TenantId == tenant && items.Contains(k.KnowledgeId), ct)
            || await db.KnowledgeItems.IgnoreQueryFilters().AnyAsync(k => k.TenantId == tenant && items.Contains(k.Id) && k.PortableRecoveryState != null, ct)
            || await db.ShSealedFiles.IgnoreQueryFilters().AnyAsync(f => f.TenantId == tenant && (f.VaultId == vault || f.KnowledgeId.HasValue && items.Contains(f.KnowledgeId.Value)), ct);
    }
}
