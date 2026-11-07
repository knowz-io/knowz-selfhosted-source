using System.Text;
using System.Text.Json;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Infrastructure.Data.Entities;

namespace Knowz.SelfHosted.Application.Services;

public sealed partial class SelfHostedArchiveImportAdapter
{
    private sealed record PeerBinding(Guid Id, Guid TenantId, Guid VaultId, Guid SourceTenantId, Guid SourceVaultId,
        Guid ActorId, string Credential, string Fingerprint, string Revision, string Digest);
    private PeerBinding? peer;
    public ArchiveActor PeerActor => peer is { } p ? new(p.TenantId, $"sync:vault:{p.VaultId:D}:link:{p.Id:D}:user:{p.ActorId:D}")
        : throw new InvalidOperationException("This adapter has no peer binding.");
    public SelfHostedArchiveImportAdapter ForPeer(ShArchivePeerBinding binding)
    {
        if (peers == null || binding.TenantId != tenant.TenantId || binding.PrincipalUserId is not { } actor || actor == Guid.Empty)
            throw new ArchiveOperationException(403, "A verified peer identity is required.");
        return new(db, tenant, graph, access, peers) { peer = new(binding.Id, binding.TenantId, binding.DestinationVaultId,
            binding.SourceTenantId, binding.SourceVaultId, actor, binding.CredentialIdentitySha256, binding.SignerFingerprint,
            binding.BindingRevision, BindingDigest(binding)) };
    }
    private static string BindingDigest(ShArchivePeerBinding p) => ArchivePeerProof.Fingerprint(JsonSerializer.SerializeToUtf8Bytes(new
    {
        p.Id, p.TenantId, p.DestinationVaultId, p.SourceTenantId, p.SourceVaultId, p.SourceBaseUrl, p.BindingSlotSha256,
        p.SignerFingerprint, p.SignerPublicKey, p.CredentialIdentitySha256, p.PrincipalUserId, p.PlatformConnectionId,
        p.SyncLinkId, p.ParentBindingSha256, p.RemoteBindingId, p.RemoteBindingRevision, p.LocalSourceBaseUrl, p.TrustProvenance, p.IsEnabled, p.BindingRevision
    }));
    private async Task AuthorizePeerAsync(ArchiveActor actor, ArchiveImportOptions options, ArchivePreparation? prepared, CancellationToken ct)
    {
        var p = peer!;
        if (actor != PeerActor || options.TargetVaultId != p.VaultId || options.VaultMappings.Count != 0
            || options.ExpectedSignerFingerprint != p.Fingerprint || options.AllowPartial || options.SourceReceipt == null
            || !options.IdempotencyKey.StartsWith($"sync:{p.Id:D}:", StringComparison.Ordinal)
            || prepared?.State is State state && state.PeerBindingSha256 != p.Digest)
            throw new ArchiveOperationException(403, "The archive plan is not bound to this verified peer.");
        var current = await peers!.ResolveAsync(p.ActorId, p.Credential, p.Id, p.Revision, ct);
        if (BindingDigest(current) != p.Digest) throw new ArchiveOperationException(409, "The peer binding changed; review registration again.");
    }
    private async Task ValidatePeerArchiveAsync(ValidatedKnowzArchive archive, CancellationToken ct)
    {
        if (peer is not { } p) return;
        if (archive.SourceTrust != "trusted" || archive.SignerFingerprint != p.Fingerprint || archive.Manifest.PreservationState != "complete"
            || archive.Packages.Any(package => package.SourceTenantId != p.SourceTenantId || package.Data.Vaults.Any(v => v.Id != p.SourceVaultId))
            || !archive.Packages.SelectMany(package => package.Data.Vaults).Any(v => v.Id == p.SourceVaultId))
            throw new ArchiveOperationException(403, "The archive does not match the registered source tenant, vault, and signer.");
        foreach (var package in new[] { PortableGraphPackages.Combine(archive.Packages) })
        {
            var error = await graph.SyncOwnershipErrorAsync(package, p.VaultId, ct);
            if (error != null) throw new ArchiveOperationException(403, error);
        }
    }
}
