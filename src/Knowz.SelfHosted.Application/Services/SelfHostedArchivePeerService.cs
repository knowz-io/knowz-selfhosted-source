using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Application.Services;

/// <summary>Process-local challenges fail closed after restart. No credential or private key is retained.</summary>
public sealed class SelfHostedPeerChallengeStore(TimeProvider? clock = null, int maximum = 1024)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<Guid, (ArchivePeerChallenge Challenge, Guid Actor, string? Parent)> entries = [];
    public void Add(ArchivePeerChallenge challenge, Guid actor, string? parent = null)
    {
        if (actor == Guid.Empty) throw new ArchiveOperationException(403, "A peer registration requires a user actor.");
        _ = ArchivePeerProof.Canonicalize(challenge);
        lock (gate)
        {
            Expire();
            if (entries.Count >= maximum || entries.Values.Count(x => x.Challenge.DestinationTenantId == challenge.DestinationTenantId) >= Math.Min(maximum, 32)
                || entries.Values.Count(x => x.Actor == actor && x.Challenge.DestinationTenantId == challenge.DestinationTenantId) >= Math.Min(maximum, 8))
                throw new ArchiveOperationException(429, "Too many outstanding peer registration challenges; wait for expiry.");
            if (!entries.TryAdd(challenge.ChallengeId, (challenge, actor, parent))) throw new ArchiveOperationException(409, "Peer challenge already exists.");
        }
    }
    public (ArchivePeerChallenge Challenge, string? Parent) Consume(Guid tenant, Guid actor, string credential, Guid id, ArchivePeerPossessionProof proof)
    {
        lock (gate)
        {
            Expire();
            if (!entries.TryGetValue(id, out var entry) || entry.Actor != actor || entry.Challenge.DestinationTenantId != tenant || entry.Challenge.CredentialIdentitySha256 != credential)
                throw new ArchiveOperationException(410, "Peer challenge is unavailable; request a new challenge.");
            try { ArchivePeerProof.Verify(proof, entry.Challenge, entry.Challenge.SignerFingerprint, time.GetUtcNow()); }
            catch (InvalidDataException) { throw new ArchiveOperationException(400, "Peer possession proof does not match the issued challenge."); }
            entries.Remove(id); // Exactly one successful proof can leave this lock, even if the subsequent database write fails.
            return (entry.Challenge, entry.Parent);
        }
    }
    private void Expire()
    {
        var now = time.GetUtcNow();
        foreach (var id in entries.Where(x => x.Value.Challenge.ExpiresAt <= now).Select(x => x.Key).ToArray()) entries.Remove(id);
    }
}

/// <summary>Receiving side of reviewed, authenticated TOFU registration. A source URL is descriptive and is never fetched here.</summary>
public sealed class SelfHostedArchivePeerService(SelfHostedDbContext db, ITenantProvider tenant, IVaultAccessService access,
    SelfHostedPeerChallengeStore challenges, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    public async Task<ArchivePeerChallenge> ChallengeAsync(Guid actor, string credential, Guid destinationVault,
        ArchivePeerRegistrationRequest request, CancellationToken ct)
    {
        await AuthorizeAsync(actor, credential, destinationVault, true, ct);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
        var challenge = new ArchivePeerChallenge
        {
            ChallengeId = Guid.NewGuid(), Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), IssuedAt = now, ExpiresAt = now.AddMinutes(2),
            DestinationTenantId = tenant.TenantId, DestinationVaultId = destinationVault, SourceTenantId = request.SourceTenantId,
            SourceVaultId = request.SourceVaultId, SourceBaseUrl = ArchivePeerProof.NormalizeBaseUrl(request.SourceBaseUrl),
            SignerFingerprint = request.SignerFingerprint, SignerPublicKey = request.SignerPublicKey, CredentialIdentitySha256 = credential,
            ConnectionId = request.ConnectionId, ConnectionRevision = request.ConnectionRevision, ExistingBindingId = request.ExistingBindingId,
            ExpectedBindingRevision = request.ExpectedBindingRevision
        };
        _ = ArchivePeerProof.Canonicalize(challenge);
        var slot = ArchivePeerProof.BindingSlot(tenant.TenantId, destinationVault, request.SourceTenantId, request.SourceVaultId, challenge.SourceBaseUrl);
        var existing = await db.ShArchivePeerBindings.AsNoTracking().SingleOrDefaultAsync(x => x.BindingSlotSha256 == slot, ct);
        if (existing == null ? request.ExistingBindingId.HasValue : existing.Id != request.ExistingBindingId || existing.BindingRevision != request.ExpectedBindingRevision)
            throw new ArchiveOperationException(409, "Peer identity already exists or changed; explicitly review the current binding before replacement.");
        var parent = await ParentAsync(destinationVault, challenge.SourceTenantId, challenge.SourceVaultId, challenge.SourceBaseUrl, challenge.ConnectionId, challenge.ConnectionRevision, ct);
        challenges.Add(challenge, actor, parent?.Digest);
        return challenge;
    }

    public async Task<ShArchivePeerBinding> AcceptAsync(Guid actor, string credential, Guid challengeId, ArchivePeerPossessionProof proof, CancellationToken ct)
    {
        // Prove current credential possession before consuming a legitimate challenge.
        await AuthorizeAsync(actor, credential, null, true, ct);
        var cached = challenges.Consume(tenant.TenantId, actor, credential, challengeId, proof); var c = cached.Challenge;
        await AuthorizeAsync(actor, credential, c.DestinationVaultId, true, ct);
        var parent = await ParentAsync(c.DestinationVaultId, c.SourceTenantId, c.SourceVaultId, c.SourceBaseUrl, c.ConnectionId, c.ConnectionRevision, ct);
        if (parent?.Digest != cached.Parent) throw new ArchiveOperationException(409, "Peer parent configuration changed during registration.");
        var slot = ArchivePeerProof.BindingSlot(tenant.TenantId, c.DestinationVaultId, c.SourceTenantId, c.SourceVaultId, c.SourceBaseUrl);
        var row = await db.ShArchivePeerBindings.SingleOrDefaultAsync(x => x.BindingSlotSha256 == slot, ct);
        if (row == null ? c.ExistingBindingId.HasValue : row.Id != c.ExistingBindingId || row.BindingRevision != c.ExpectedBindingRevision)
            throw new ArchiveOperationException(409, "Peer binding changed while registration was pending; review a fresh challenge.");
        if (row == null) { row = new() { TenantId = tenant.TenantId, BindingSlotSha256 = slot }; db.ShArchivePeerBindings.Add(row); }
        row.DestinationVaultId = c.DestinationVaultId; row.SourceTenantId = c.SourceTenantId; row.SourceVaultId = c.SourceVaultId;
        row.SourceBaseUrl = c.SourceBaseUrl; row.SignerFingerprint = c.SignerFingerprint; row.SignerPublicKey = c.SignerPublicKey;
        row.CredentialIdentitySha256 = c.CredentialIdentitySha256; row.PrincipalUserId = actor; row.RegisteredByUserId = actor;
        row.IsEnabled = true; row.BindingRevision = Guid.NewGuid().ToString("N"); row.UpdatedAt = time.GetUtcNow().UtcDateTime;
        row.TrustProvenance = "transport-authenticated-tofu";
        // Keep the old public remote receipt for an explicitly reviewed retry, but never
        // treat it as a completed mutual registration after this local identity changes.
        row.LocalSourceBaseUrl = null;
        row.RegistrationChallengeSha256 = ArchivePeerProof.Fingerprint(ArchivePeerProof.Canonicalize(c));
        row.PendingRemoteChallengeJson = null; row.PendingLocalSourceBaseUrl = null;
        row.PlatformConnectionId = c.ConnectionId; row.SyncLinkId = parent?.Link; row.ParentBindingSha256 = parent?.Digest;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); throw new ArchiveOperationException(409, "Peer binding changed; review a new registration challenge."); }
        return row;
    }

    public async Task<ShArchivePeerBinding> ResolveAsync(Guid actor, string credential, Guid bindingId, string? expectedRevision, CancellationToken ct)
    {
        var row = await db.ShArchivePeerBindings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == bindingId && x.IsEnabled, ct)
            ?? throw new ArchiveOperationException(404, "Peer binding is unavailable.");
        if (row.PrincipalUserId != actor || row.CredentialIdentitySha256 != credential || expectedRevision != null && row.BindingRevision != expectedRevision)
            throw new ArchiveOperationException(409, "Peer credential or binding changed; explicitly review registration again.");
        await AuthorizeAsync(actor, credential, row.DestinationVaultId, false, ct);
        var parent = await ParentAsync(row.DestinationVaultId, row.SourceTenantId, row.SourceVaultId, row.SourceBaseUrl, row.PlatformConnectionId, null, ct);
        if (parent?.Digest != row.ParentBindingSha256 || parent?.Link != row.SyncLinkId) throw new ArchiveOperationException(409, "Peer parent configuration changed; explicitly review registration again.");
        return row;
    }
    public Task AuthorizeDiscoveryAsync(Guid actor, string credential, Guid vault, CancellationToken ct) => AuthorizeAsync(actor, credential, vault, true, ct);

    public async Task<ShArchivePeerBinding> RegistrationReceiptAsync(Guid actor, string credential, ArchivePeerChallenge challenge, CancellationToken ct)
    {
        await AuthorizeAsync(actor, credential, null, true, ct);
        var digest = ArchivePeerProof.Fingerprint(ArchivePeerProof.Canonicalize(challenge));
        if (challenge.DestinationTenantId != tenant.TenantId || challenge.CredentialIdentitySha256 != credential)
            throw new ArchiveOperationException(403, "This registration belongs to another actor or credential.");
        if (challenge.ExpiresAt <= time.GetUtcNow() || challenge.IssuedAt > time.GetUtcNow())
            throw new ArchiveOperationException(410, "Registration challenge expired; explicitly review registration again.");
        var row = await db.ShArchivePeerBindings.AsNoTracking().SingleOrDefaultAsync(p => p.RegistrationChallengeSha256 == digest && p.PrincipalUserId == actor && p.IsEnabled, ct)
            ?? throw new ArchiveOperationException(404, "Exact accepted registration is unavailable.");
        await AuthorizeAsync(actor, credential, row.DestinationVaultId, true, ct);
        return await ResolveAsync(actor, credential, row.Id, row.BindingRevision, ct);
    }

    private async Task<(string Digest, Guid Link)?> ParentAsync(Guid destinationVault, Guid sourceTenant, Guid sourceVault, string url, Guid? connectionId, string? expectedRevision, CancellationToken ct)
    {
        if (!connectionId.HasValue) return null;
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == connectionId && c.TenantId == tenant.TenantId, ct)
            ?? throw new ArchiveOperationException(409, "Configured peer connection is unavailable.");
        if (ArchivePeerProof.NormalizeBaseUrl(connection.PlatformApiUrl) != url || connection.RemoteTenantId != sourceTenant || expectedRevision != null && connection.SecurityRevision != expectedRevision)
            throw new ArchiveOperationException(409, "Configured peer connection changed.");
        var links = await db.VaultSyncLinks.AsNoTracking().Where(l => l.PlatformConnectionId == connection.Id && l.LocalVaultId == destinationVault && l.RemoteVaultId == sourceVault && l.RemoteTenantId == sourceTenant && l.SyncEnabled).Take(2).ToListAsync(ct);
        if (links.Count != 1) throw new ArchiveOperationException(409, "An exact active saved vault pair is required.");
        var link = links[0];
        var hash = ArchivePeerProof.Fingerprint(JsonSerializer.SerializeToUtf8Bytes(new { connection.Id, connection.TenantId, connection.PlatformApiUrl, connection.ApiKeyProtected, connection.RemoteTenantId, connection.SecurityRevision,
            LinkId = link.Id, link.LocalVaultId, link.RemoteVaultId, LinkRemoteTenantId = link.RemoteTenantId, link.SyncEnabled, LinkRevision = link.SecurityRevision }));
        return (hash, link.Id);
    }

    private async Task AuthorizeAsync(Guid actor, string credential, Guid? vault, bool manage, CancellationToken ct)
    {
        if (actor == Guid.Empty) throw new ArchiveOperationException(403, "A current user API key is required for peer registration.");
        if (!await db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenant.TenantId && t.IsActive, ct)) throw new ArchiveOperationException(403, "Peer tenant is suspended or unavailable.");
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == actor && u.IsActive, ct);
        if (user?.ApiKey is not { Length: > 0 } key || ArchivePeerProof.Fingerprint(Encoding.UTF8.GetBytes(key)) != credential)
            throw new ArchiveOperationException(403, "Peer credential was revoked or replaced.");
        var membership = await db.UserTenantMemberships.AsNoTracking().SingleOrDefaultAsync(m => m.UserId == actor && m.TenantId == tenant.TenantId, ct);
        if (membership != null ? !membership.IsActive : user.TenantId != tenant.TenantId)
            throw new ArchiveOperationException(403, "Peer tenant membership is unavailable.");
        if (vault.HasValue && (!await db.Vaults.AnyAsync(v => v.Id == vault.Value, ct)
            || !await access.HasVaultAccessAsync(actor, vault.Value, requireWrite: true, requireManage: manage, ct: ct)))
            throw new ArchiveOperationException(403, "Current destination vault management permission is required.");
    }
}
