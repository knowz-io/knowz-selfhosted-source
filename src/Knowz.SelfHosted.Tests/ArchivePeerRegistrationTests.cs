using System.Security.Cryptography;
using System.Text;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed class ArchivePeerRegistrationTests : IDisposable
{
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly SelfHostedDbContext db;
    private readonly ITenantProvider tenant = Substitute.For<ITenantProvider>();
    private readonly Clock clock = new();
    private readonly User actor;
    private readonly Vault vault;
    public ArchivePeerRegistrationTests()
    {
        tenant.TenantId.Returns(tenantId);
        db = new(new DbContextOptionsBuilder<SelfHostedDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
        actor = new() { Id = Guid.NewGuid(), TenantId = tenantId, Username = "peer-owner", IsActive = true, ApiKey = "ksh_synthetic-only" };
        vault = new() { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Peer destination" };
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Peer tenant", Slug = "peer-tenant" }); db.Users.Add(actor); db.Vaults.Add(vault); db.SaveChanges();
    }
    public void Dispose() => db.Dispose();
    private string Credential => ArchivePeerProof.Fingerprint(Encoding.UTF8.GetBytes(actor.ApiKey!));
    private SelfHostedArchivePeerService Service(SelfHostedPeerChallengeStore cache) => new(db, tenant, new VaultAccessService(db), cache, clock);

    [Fact]
    public async Task RegistrationIsOneUseActorBoundAndRequiresExplicitReviewedRotation()
    {
        var cache = new SelfHostedPeerChallengeStore(clock); var service = Service(cache);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var changedSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = Request(signer); var challenge = await service.ChallengeAsync(actor.Id, Credential, vault.Id, request, default);
        var proof = ArchivePeerProof.Sign(challenge, signer);
        await Assert.ThrowsAsync<ArchiveOperationException>(() => service.AcceptAsync(actor.Id, new string('b', 64), challenge.ChallengeId, proof, default));
        var binding = await service.AcceptAsync(actor.Id, Credential, challenge.ChallengeId, proof, default);
        Assert.Equal("transport-authenticated-tofu", binding.TrustProvenance); Assert.Equal(request.SignerFingerprint, binding.SignerFingerprint);
        var receipts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => service.RegistrationReceiptAsync(actor.Id, Credential, challenge, default)));
        Assert.All(receipts, r => Assert.Equal(binding.BindingRevision, r.BindingRevision));
        await Assert.ThrowsAsync<ArchiveOperationException>(() => service.RegistrationReceiptAsync(actor.Id, Credential, challenge with { Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }, default));
        await Assert.ThrowsAsync<ArchiveOperationException>(() => service.RegistrationReceiptAsync(actor.Id, new string('b', 64), challenge, default));
        await Assert.ThrowsAsync<ArchiveOperationException>(() => service.AcceptAsync(actor.Id, Credential, challenge.ChallengeId, proof, default));
        Assert.Single(await db.ShArchivePeerBindings.ToListAsync());
        var rotated = request with { SignerPublicKey = Convert.ToBase64String(changedSigner.ExportSubjectPublicKeyInfo()), SignerFingerprint = ArchivePeerProof.Fingerprint(changedSigner.ExportSubjectPublicKeyInfo()) };
        await Assert.ThrowsAsync<ArchiveOperationException>(() => service.ChallengeAsync(actor.Id, Credential, vault.Id, rotated, default));
        var replacement = await service.ChallengeAsync(actor.Id, Credential, vault.Id, rotated with { ExistingBindingId = binding.Id, ExpectedBindingRevision = binding.BindingRevision }, default);
        var oldRevision = binding.BindingRevision;
        var updated = await service.AcceptAsync(actor.Id, Credential, replacement.ChallengeId, ArchivePeerProof.Sign(replacement, changedSigner), default);
        Assert.Equal(binding.Id, updated.Id); Assert.NotEqual(oldRevision, updated.BindingRevision);
        await Assert.ThrowsAsync<ArchiveOperationException>(() => service.RegistrationReceiptAsync(actor.Id, Credential, challenge, default));
        clock.Now += TimeSpan.FromMinutes(3);
        await Assert.ThrowsAsync<ArchiveOperationException>(() => service.RegistrationReceiptAsync(actor.Id, Credential, replacement, default));
        await Assert.ThrowsAsync<ArchiveOperationException>(() => service.ResolveAsync(actor.Id, Credential, binding.Id, oldRevision, default));
        actor.IsActive = false; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArchiveOperationException>(() => service.ResolveAsync(actor.Id, Credential, binding.Id, updated.BindingRevision, default));
    }

    [Fact]
    public async Task ExpiredAndConcurrentRegistrationCannotBeReplayedAndCapacityIsBounded()
    {
        var cache = new SelfHostedPeerChallengeStore(clock, maximum: 1); var service = Service(cache);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256); var request = Request(signer);
        var expired = await service.ChallengeAsync(actor.Id, Credential, vault.Id, request, default);
        await Assert.ThrowsAsync<ArchiveOperationException>(() => service.ChallengeAsync(actor.Id, Credential, vault.Id, request, default));
        clock.Now += TimeSpan.FromMinutes(3);
        await Assert.ThrowsAsync<ArchiveOperationException>(() => service.AcceptAsync(actor.Id, Credential, expired.ChallengeId, ArchivePeerProof.Sign(expired, signer), default));
        var active = await service.ChallengeAsync(actor.Id, Credential, vault.Id, request, default);
        var proof = ArchivePeerProof.Sign(active, signer);
        var wins = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        { try { cache.Consume(tenantId, actor.Id, Credential, active.ChallengeId, proof); return true; } catch (ArchiveOperationException) { return false; } })));
        Assert.Single(wins, x => x);
        var afterRestart = new SelfHostedPeerChallengeStore(clock);
        Assert.Throws<ArchiveOperationException>(() => afterRestart.Consume(tenantId, actor.Id, Credential, active.ChallengeId, proof));
        Assert.Empty(await db.ShArchivePeerBindings.ToListAsync());
    }
    [Fact]
    public async Task SuspendedTenantCannotRegisterPeerDespiteValidUserCredential()
    {
        (await db.Tenants.SingleAsync()).IsActive = false; await db.SaveChangesAsync();
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await Assert.ThrowsAsync<ArchiveOperationException>(() => Service(new(clock)).ChallengeAsync(actor.Id, Credential, vault.Id, Request(signer), default));
    }
    [Fact]
    public async Task PeerParentRequiresExactSavedConnectionAndRejectsConfigurationChangeAndReversion()
    {
        var connection = new PlatformConnection { TenantId = tenantId, PlatformApiUrl = "https://source.example", ApiKeyProtected = "synthetic-encrypted-reference", RemoteTenantId = Guid.NewGuid() };
        db.PlatformConnections.Add(connection); var link = new VaultSyncLink { LocalVaultId = vault.Id, RemoteVaultId = Guid.NewGuid(), RemoteTenantId = connection.RemoteTenantId!.Value, PlatformConnectionId = connection.Id, SyncEnabled = true }; db.VaultSyncLinks.Add(link); await db.SaveChangesAsync();
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256); var service = Service(new(clock));
        var request = Request(signer) with { SourceTenantId = link.RemoteTenantId, SourceVaultId = link.RemoteVaultId, ConnectionId = connection.Id, ConnectionRevision = connection.SecurityRevision };
        var challenge = await service.ChallengeAsync(actor.Id, Credential, vault.Id, request, default);
        var binding = await service.AcceptAsync(actor.Id, Credential, challenge.ChallengeId, ArchivePeerProof.Sign(challenge, signer), default);
        Assert.NotNull(binding.ParentBindingSha256); Assert.Equal(connection.Id, binding.PlatformConnectionId); Assert.Equal(link.Id, binding.SyncLinkId);
        link.LastPullCursor = DateTime.UtcNow; await db.SaveChangesAsync();
        Assert.Equal(binding.Id, (await service.ResolveAsync(actor.Id, Credential, binding.Id, binding.BindingRevision, default)).Id);
        var originalRemote = link.RemoteVaultId; link.RemoteVaultId = Guid.NewGuid(); await db.SaveChangesAsync(); link.RemoteVaultId = originalRemote; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArchiveOperationException>(() => service.ResolveAsync(actor.Id, Credential, binding.Id, binding.BindingRevision, default));
    }
    private static ArchivePeerRegistrationRequest Request(ECDsa signer) => new()
    {
        SourceTenantId = Guid.NewGuid(), SourceVaultId = Guid.NewGuid(), SourceBaseUrl = "https://source.example",
        SignerPublicKey = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()), SignerFingerprint = ArchivePeerProof.Fingerprint(signer.ExportSubjectPublicKeyInfo())
    };
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
}
