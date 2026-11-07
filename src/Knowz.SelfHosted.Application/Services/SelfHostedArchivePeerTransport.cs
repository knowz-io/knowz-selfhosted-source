using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Application.Services;

/// <summary>Explicit manual archive exchange over an existing, authorized saved connection. Never deletes source records.</summary>
public sealed class SelfHostedArchivePeerTransport(SelfHostedDbContext db, ITenantProvider tenant, SelfHostedArchivePeerService peers,
    SelfHostedArchiveExportService exporter, SelfHostedArchiveImportAdapter adapter, ArchiveImportCoordinator coordinator,
    PlatformSyncCredentialResolver credentials)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Identity(Guid TenantId, Guid VaultId, string SignerFingerprint, string SignerPublicKey, string Capability, string TrustProvenance);
    private sealed record Session(VaultSyncLink Link, PlatformConnection Connection, Guid Actor, string Credential);

    public async Task<object> GetRegistrationAsync(Guid actor, Guid linkId, CancellationToken ct)
    {
        var session = await SessionAsync(actor, linkId, ct);
        var binding = await db.ShArchivePeerBindings.AsNoTracking().SingleOrDefaultAsync(p => p.TenantId == tenant.TenantId && p.SyncLinkId == linkId, ct);
        if (binding == null) return new { status = "not-registered", binding = (object?)null };
        if (binding.PrincipalUserId != actor || binding.CredentialIdentitySha256 != session.Credential)
            throw new ArchiveOperationException(403, "This peer registration belongs to another local principal or credential.");
        var status = binding.RemoteBindingId.HasValue && binding.RemoteBindingRevision != null && binding.LocalSourceBaseUrl != null
            && binding.PendingRemoteChallengeJson == null ? "registered" : "pending";
        try { await peers.ResolveAsync(actor, session.Credential, binding.Id, binding.BindingRevision, ct); }
        catch (ArchiveOperationException ex) when (ex.StatusCode is 409 or 404) { status = "review-required"; }
        var fresh = await SessionAsync(actor, linkId, ct);
        if (fresh.Link.LocalVaultId != session.Link.LocalVaultId || fresh.Link.RemoteVaultId != session.Link.RemoteVaultId || fresh.Connection.Id != session.Connection.Id
            || fresh.Credential != session.Credential) throw new ArchiveOperationException(409, "Saved peer configuration changed during review.");
        var current = await db.ShArchivePeerBindings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == binding.Id && p.TenantId == tenant.TenantId, ct);
        if (current == null || current.BindingRevision != binding.BindingRevision || current.PrincipalUserId != binding.PrincipalUserId
            || current.CredentialIdentitySha256 != binding.CredentialIdentitySha256) throw new ArchiveOperationException(409, "Peer registration changed during review.");
        return new { status, binding = new { binding.Id, binding.TenantId, binding.DestinationVaultId, binding.SourceTenantId, binding.SourceVaultId,
            binding.SourceBaseUrl, binding.SignerFingerprint, binding.SignerPublicKey, binding.CredentialIdentitySha256, binding.BindingRevision,
            binding.RemoteBindingId, binding.RemoteBindingRevision, binding.LocalSourceBaseUrl, binding.IsEnabled, binding.TrustProvenance } };
    }

    public async Task<ShArchivePeerBinding> RegisterAsync(Guid actor, Guid linkId, string localSourceBaseUrl, string? expectedRevision, CancellationToken ct)
    {
        var session = await SessionAsync(actor, linkId, ct);
        var localUrl = ArchivePeerProof.NormalizeBaseUrl(localSourceBaseUrl);
        var previous = await db.ShArchivePeerBindings.AsNoTracking().SingleOrDefaultAsync(p => p.SyncLinkId == linkId, ct);
        if (previous == null ? expectedRevision != null : expectedRevision != previous.BindingRevision)
            throw new ArchiveOperationException(409, "Explicitly review the current peer binding revision before re-registering.");
        using var client = credentials.CreatePlatformHttpClient(session.Link, TimeSpan.FromMinutes(10));
        client.BaseAddress = new Uri(ArchivePeerProof.NormalizeBaseUrl(session.Connection.PlatformApiUrl) + "/");
        var remoteCredential = Credential(client);
        var remoteUrl = ArchivePeerProof.NormalizeBaseUrl(session.Connection.PlatformApiUrl);
        var identity = await SendJsonAsync<Identity>(client, HttpMethod.Get, $"vaults/{session.Link.RemoteVaultId:D}/archives/identity", null, null, ct);
        if (identity.TenantId != session.Link.RemoteTenantId || identity.VaultId != session.Link.RemoteVaultId || identity.Capability != "knowz.archive-peer/v1"
            || identity.TrustProvenance != "transport-authenticated-tofu") throw new ArchiveOperationException(409, "Saved peer identity does not match the configured tenant and vault, or complete archive sync is unsupported.");
        var localIdentity = await exporter.IdentityAsync(ct);
        if (previous?.PendingRemoteChallengeJson != null)
        {
            await peers.ResolveAsync(actor, session.Credential, previous.Id, previous.BindingRevision, ct);
            if (localUrl != previous.PendingLocalSourceBaseUrl || identity.SignerFingerprint != previous.SignerFingerprint || identity.SignerPublicKey != previous.SignerPublicKey)
                throw new ArchiveOperationException(409, "Pending registration scope or source signer changed.");
            var pending = ArchivePeerProof.ReadCanonicalChallenge(Encoding.UTF8.GetBytes(previous.PendingRemoteChallengeJson));
            CheckRemoteChallenge(pending, session, localIdentity, localUrl, remoteCredential);
            if (pending.ExpiresAt <= DateTimeOffset.UtcNow || pending.IssuedAt > DateTimeOffset.UtcNow) throw new ArchiveOperationException(410, "Pending registration expired; explicit peer review is required.");
            ShArchivePeerBinding accepted;
            try { accepted = await SendJsonAsync<ShArchivePeerBinding>(client, HttpMethod.Post, "archive-peers/registration-receipt", pending, null, ct); }
            catch (ArchiveOperationException ex) when (ex.StatusCode == 404)
            {
                // A persisted pending challenge may precede the remote accept. Retry only
                // this exact proof; the receiver's one-use store still owns consumption.
                var pendingProof = await exporter.SignPeerChallengeAsync(session.Link.LocalVaultId, pending,
                    async token => { await peers.AuthorizeDiscoveryAsync(actor, session.Credential, session.Link.LocalVaultId, token); await peers.ResolveAsync(actor, session.Credential, previous.Id, previous.BindingRevision, token); }, ct);
                accepted = await SendJsonAsync<ShArchivePeerBinding>(client, HttpMethod.Post, $"archive-peers/challenges/{pending.ChallengeId:D}/accept", pendingProof, null, ct);
            }
            CheckRemoteBinding(accepted, session, localIdentity, localUrl, remoteCredential);
            return await CompleteRegistrationAsync(session, previous.Id, previous.BindingRevision, accepted, localUrl, ct);
        }
        // Canonical proof encoding also validates the public key, exact fingerprint, URL and all identifiers.
        var challenge = await peers.ChallengeAsync(actor, session.Credential, session.Link.LocalVaultId, new()
        {
            SourceTenantId = identity.TenantId, SourceVaultId = identity.VaultId, SourceBaseUrl = remoteUrl,
            SignerFingerprint = identity.SignerFingerprint, SignerPublicKey = identity.SignerPublicKey,
            ConnectionId = session.Connection.Id, ConnectionRevision = session.Connection.SecurityRevision,
            ExistingBindingId = previous?.Id, ExpectedBindingRevision = previous?.BindingRevision
        }, ct);
        var proof = await SendJsonAsync<ArchivePeerPossessionProof>(client, HttpMethod.Post, $"vaults/{identity.VaultId:D}/archives/sign-peer-challenge", challenge, null, ct);
        ArchivePeerProof.Verify(proof, challenge, identity.SignerFingerprint, DateTimeOffset.UtcNow);
        var binding = await peers.AcceptAsync(actor, session.Credential, challenge.ChallengeId, proof, ct);
        var registeredRevision = binding.BindingRevision;
        var intent = new ArchivePeerRegistrationRequest
        {
            SourceTenantId = tenant.TenantId, SourceVaultId = session.Link.LocalVaultId, SourceBaseUrl = localUrl,
            SignerFingerprint = localIdentity.SignerFingerprint, SignerPublicKey = localIdentity.SignerPublicKey,
            ExistingBindingId = previous?.RemoteBindingId, ExpectedBindingRevision = previous?.RemoteBindingRevision
        };
        var remoteChallenge = await SendJsonAsync<ArchivePeerChallenge>(client, HttpMethod.Post, $"vaults/{identity.VaultId:D}/archive-peers/challenges", intent, null, ct);
        CheckRemoteChallenge(remoteChallenge, session, localIdentity, localUrl, remoteCredential);
        if (remoteChallenge.ExistingBindingId != intent.ExistingBindingId || remoteChallenge.ExpectedBindingRevision != intent.ExpectedBindingRevision)
            throw new ArchiveOperationException(409, "Remote registration substituted a peer challenge binding.");
        var localProof = await exporter.SignPeerChallengeAsync(session.Link.LocalVaultId, remoteChallenge, async token =>
        { await peers.AuthorizeDiscoveryAsync(actor, session.Credential, session.Link.LocalVaultId, token); await peers.ResolveAsync(actor, session.Credential, binding.Id, registeredRevision, token); }, ct);
        binding.PendingRemoteChallengeJson = Encoding.UTF8.GetString(ArchivePeerProof.Canonicalize(remoteChallenge)); binding.PendingLocalSourceBaseUrl = localUrl;
        binding.BindingRevision = Guid.NewGuid().ToString("N"); binding.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct); registeredRevision = binding.BindingRevision;
        await peers.ResolveAsync(actor, session.Credential, binding.Id, registeredRevision, ct);
        var remote = await SendJsonAsync<ShArchivePeerBinding>(client, HttpMethod.Post, $"archive-peers/challenges/{remoteChallenge.ChallengeId:D}/accept", localProof, null, ct);
        CheckRemoteBinding(remote, session, localIdentity, localUrl, remoteCredential);
        return await CompleteRegistrationAsync(session, binding.Id, registeredRevision, remote, localUrl, ct);
    }

    private async Task<ShArchivePeerBinding> CompleteRegistrationAsync(Session session, Guid id, string expectedRevision, ShArchivePeerBinding remote, string localUrl, CancellationToken ct)
    {
        await peers.AuthorizeDiscoveryAsync(session.Actor, session.Credential, session.Link.LocalVaultId, ct);
        await peers.ResolveAsync(session.Actor, session.Credential, id, expectedRevision, ct);
        var binding = await db.ShArchivePeerBindings.SingleAsync(p => p.Id == id, ct);
        if (binding.BindingRevision != expectedRevision) throw new ArchiveOperationException(409, "Local peer registration changed.");
        binding.RemoteBindingId = remote.Id; binding.RemoteBindingRevision = remote.BindingRevision; binding.LocalSourceBaseUrl = localUrl;
        binding.PendingRemoteChallengeJson = null; binding.PendingLocalSourceBaseUrl = null;
        binding.BindingRevision = Guid.NewGuid().ToString("N"); binding.UpdatedAt = DateTime.UtcNow;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); throw new ArchiveOperationException(409, "Peer receipt could not be saved; explicitly resume using the current local binding revision."); }
        return binding;
    }

    public async Task<ArchiveCommitReceipt> TransferAsync(Guid actor, Guid linkId, string direction, CancellationToken ct)
    {
        if (direction is not ("pull" or "push")) throw new ArchiveOperationException(400, "Choose pull or push for a manual complete archive transfer.");
        var session = await SessionAsync(actor, linkId, ct);
        var saved = await db.ShArchivePeerBindings.AsNoTracking().SingleOrDefaultAsync(p => p.SyncLinkId == linkId, ct)
            ?? throw new ArchiveOperationException(409, "Register this saved connection for complete archive sync first.");
        var binding = await peers.ResolveAsync(actor, session.Credential, saved.Id, saved.BindingRevision, ct);
        if (binding.RemoteBindingId is not { } remoteId || binding.RemoteBindingRevision == null || binding.LocalSourceBaseUrl == null)
            throw new ArchiveOperationException(409, "Peer registration is incomplete; explicitly finish registration before sync.");
        using var client = credentials.CreatePlatformHttpClient(session.Link, TimeSpan.FromMinutes(10));
        client.BaseAddress = new Uri(ArchivePeerProof.NormalizeBaseUrl(session.Connection.PlatformApiUrl) + "/");
        var currentRemote = await SendJsonAsync<ShArchivePeerBinding>(client, HttpMethod.Get, $"archive-peers/{remoteId:D}", null, binding.RemoteBindingRevision, ct);
        var localIdentity = await exporter.IdentityAsync(ct);
        CheckRemoteBinding(currentRemote, session, localIdentity, binding.LocalSourceBaseUrl, Credential(client));
        if (currentRemote.Id != remoteId || currentRemote.BindingRevision != binding.RemoteBindingRevision) throw new ArchiveOperationException(409, "Remote peer registration changed.");
        // Independently re-read the live remote source signer; no archive can silently replace its pin.
        var identity = await SendJsonAsync<Identity>(client, HttpMethod.Get, $"vaults/{session.Link.RemoteVaultId:D}/archives/identity", null, null, ct);
        if (identity.TenantId != binding.SourceTenantId || identity.VaultId != binding.SourceVaultId || identity.SignerFingerprint != binding.SignerFingerprint
            || identity.SignerPublicKey != binding.SignerPublicKey || identity.Capability != "knowz.archive-peer/v1") throw new ArchiveOperationException(409, "Remote source identity changed; explicitly review registration again.");
        await peers.ResolveAsync(actor, session.Credential, binding.Id, binding.BindingRevision, ct);
        var operation = Guid.NewGuid().ToString("N");
        ArchiveCommitReceipt receipt;
        if (direction == "pull")
        {
            using var request = Request(HttpMethod.Post, $"archive-peers/{remoteId:D}/archives/export", binding.RemoteBindingRevision);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); await RequireSuccessAsync(response, ct);
            var source = ReadSourceReceipt(response);
            await using var archive = await response.Content.ReadAsStreamAsync(ct);
            var peer = adapter.ForPeer(binding);
            var plan = await coordinator.CreatePlanAsync(archive, peer.PeerActor, new() { Strategy = "overwrite", TargetVaultId = binding.DestinationVaultId,
                ExpectedSignerFingerprint = binding.SignerFingerprint, SourceReceipt = source, IdempotencyKey = $"sync:{binding.Id:D}:{operation}" }, peer, ct);
            try { CheckPlan(plan, source, tenant.TenantId, binding.SourceVaultId, binding.DestinationVaultId); receipt = await coordinator.CommitAsync(plan.PlanId, peer.PeerActor, peer, ct); CheckReceipt(receipt, plan); }
            catch { await coordinator.CancelAsync(plan.PlanId, peer.PeerActor, peer, CancellationToken.None); throw; }
        }
        else
        {
            var captured = await exporter.ExportAsync(new() { Type = ExportScopeType.Vault, VaultIds = [binding.DestinationVaultId] }, false, ct);
            using (captured.Stream)
            {
                await RequireOrdinaryReceiverLimitAsync(client, captured.Stream, captured.Receipt, tenant.TenantId, ct);
                await peers.ResolveAsync(actor, session.Credential, binding.Id, binding.BindingRevision, ct);
                using var request = Request(HttpMethod.Post, $"archive-peers/{remoteId:D}/imports/plan?strategy=overwrite", binding.RemoteBindingRevision);
                request.Headers.Add("X-Knowz-Source-Receipt", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(captured.Receipt, Json)));
                request.Headers.Add("Idempotency-Key", operation); request.Content = new StreamContent(captured.Stream);
                request.Content.Headers.ContentType = new("application/vnd.knowz.archive+zip");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); await RequireSuccessAsync(response, ct);
                var plan = await ReadJsonAsync<ArchiveImportPlan>(response, ct);
                try
                {
                    CheckPlan(plan, captured.Receipt, session.Link.RemoteTenantId, binding.DestinationVaultId, binding.SourceVaultId);
                    await peers.ResolveAsync(actor, session.Credential, binding.Id, binding.BindingRevision, ct);
                    receipt = await SendJsonAsync<ArchiveCommitReceipt>(client, HttpMethod.Post, $"archive-peers/{remoteId:D}/imports/{plan.PlanId:D}/commit", null, binding.RemoteBindingRevision, ct);
                    CheckReceipt(receipt, plan);
                }
                catch
                {
                    using var cancel = Request(HttpMethod.Delete, $"archive-peers/{remoteId:D}/imports/{plan.PlanId:D}", binding.RemoteBindingRevision);
                    try { using var ignored = await client.SendAsync(cancel, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None); } catch { /* The receiver's expiry journal owns any remaining stage. */ }
                    throw;
                }
            }
        }
        await peers.ResolveAsync(actor, session.Credential, binding.Id, binding.BindingRevision, ct);
        return receipt;
    }

    private static async Task RequireOrdinaryReceiverLimitAsync(HttpClient client, Stream stream, ArchiveSourceReceipt receipt, Guid tenantId, CancellationToken ct)
    {
        long largest;
        if (!stream.CanSeek) throw new ArchiveOperationException(409, "The signed source capture cannot be inspected safely.");
        using (var archive = await KnowzArchiveReader.OpenAsync(stream, new() { ExpectedSignerFingerprint = receipt.SignerFingerprint, LeaveOpen = true }, ct))
        {
            if (archive.Manifest.ArchiveId != receipt.ArchiveId || archive.Manifest.SourceTenantId != tenantId
                || archive.ManifestSha256 != receipt.ManifestSha256 || archive.Manifest.Inventory.SnapshotBoundary != receipt.SourceSnapshot
                || archive.SignerPublicKey != receipt.SignerPublicKey || archive.Manifest.Inventory.Parts.Count != receipt.EntryCount
                || archive.Manifest.Inventory.PayloadCounts.GetValueOrDefault("required") != receipt.PayloadCount)
                throw new ArchiveOperationException(409, "The signed source capture changed before peer transfer.");
            largest = archive.Packages.SelectMany(p => p.Data.FileRecords).Select(f => f.SizeBytes).DefaultIfEmpty().Max();
        }
        stream.Position = 0;
        if (largest == 0) return;
        const long legacyLimit = 64L * 1024 * 1024;
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/portability/capabilities");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            if (largest <= legacyLimit) return;
            throw new ArchiveOperationException(409, "The destination does not advertise support for this ordinary file size.");
        }
        await RequireSuccessAsync(response, ct);
        var capabilities = await ReadJsonAsync<JsonElement>(response, ct);
        if (capabilities.ValueKind != JsonValueKind.Object) throw new ArchiveOperationException(409, "The destination file limit is invalid.");
        if (!capabilities.TryGetProperty("limits", out var limits))
        {
            if (largest <= legacyLimit) return;
            throw new ArchiveOperationException(409, "The destination does not advertise support for this ordinary file size.");
        }
        if (limits.ValueKind != JsonValueKind.Object) throw new ArchiveOperationException(409, "The destination file limit is invalid.");
        var archiveMaximum = KnowzArchiveLimits.MaxArchiveBytes;
        var hasArchiveLimit = limits.TryGetProperty("archiveBytes", out var archiveLimit);
        if (hasArchiveLimit && (archiveLimit.ValueKind != JsonValueKind.Number || !archiveLimit.TryGetInt64(out archiveMaximum)
            || archiveMaximum <= 0 || archiveMaximum > KnowzArchiveLimits.MaxArchiveBytes)
            || stream.Length > archiveMaximum)
            throw new ArchiveOperationException(409, "The destination archive limit is invalid or smaller than the signed capture.");
        if (!limits.TryGetProperty("ordinaryFileBytes", out var value))
        {
            if (largest <= legacyLimit) return;
            throw new ArchiveOperationException(409, "The destination does not advertise support for this ordinary file size.");
        }
        if (largest > legacyLimit && !hasArchiveLimit
            || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var maximum) || maximum <= 0
            || maximum > archiveMaximum || largest > maximum)
            throw new ArchiveOperationException(409, "The destination file limit is invalid or smaller than the signed ordinary file.");
    }

    private async Task<Session> SessionAsync(Guid actor, Guid linkId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == actor && u.IsActive, ct);
        if (user?.ApiKey is not { Length: > 0 } key) throw new ArchiveOperationException(403, "A current local user is required.");
        var credential = ArchivePeerProof.Fingerprint(Encoding.UTF8.GetBytes(key));
        var link = await db.VaultSyncLinks.AsNoTracking().SingleOrDefaultAsync(l => l.Id == linkId && l.SyncEnabled, ct)
            ?? throw new ArchiveOperationException(404, "Enabled saved vault link is unavailable.");
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == link.PlatformConnectionId && c.TenantId == tenant.TenantId, ct)
            ?? throw new ArchiveOperationException(409, "An authenticated saved connection is required.");
        if (connection.RemoteTenantId != link.RemoteTenantId) throw new ArchiveOperationException(409, "Saved source tenant changed.");
        // Management authority is deliberately required for explicit manual complete archive exchange.
        await peers.AuthorizeDiscoveryAsync(actor, credential, link.LocalVaultId, ct);
        return new(link, connection, actor, credential);
    }
    private static void CheckRemoteBinding(ShArchivePeerBinding remote, Session session, SelfHostedArchiveIdentity identity, string localUrl, string remoteCredential)
    {
        if (remote.Id == Guid.Empty || remote.BindingRevision.Length is < 1 or > 64 || !remote.IsEnabled || remote.TenantId != session.Link.RemoteTenantId
            || remote.DestinationVaultId != session.Link.RemoteVaultId || remote.SourceTenantId != session.Connection.TenantId || remote.SourceVaultId != session.Link.LocalVaultId
            || remote.SourceBaseUrl != localUrl || remote.SignerFingerprint != identity.SignerFingerprint || remote.SignerPublicKey != identity.SignerPublicKey
            || remote.CredentialIdentitySha256 != remoteCredential || remote.TrustProvenance != "transport-authenticated-tofu")
            throw new ArchiveOperationException(409, "Remote peer receipt does not match the authorized saved connection.");
    }
    private static void CheckRemoteChallenge(ArchivePeerChallenge challenge, Session session, SelfHostedArchiveIdentity identity, string localUrl, string credential)
    {
        _ = ArchivePeerProof.Canonicalize(challenge);
        if (challenge.DestinationTenantId != session.Link.RemoteTenantId || challenge.DestinationVaultId != session.Link.RemoteVaultId
            || challenge.SourceTenantId != session.Connection.TenantId || challenge.SourceVaultId != session.Link.LocalVaultId || challenge.SourceBaseUrl != localUrl
            || challenge.SignerFingerprint != identity.SignerFingerprint || challenge.SignerPublicKey != identity.SignerPublicKey
            || challenge.CredentialIdentitySha256 != credential || challenge.ConnectionId != null || challenge.ConnectionRevision != null)
            throw new ArchiveOperationException(409, "Remote registration substituted a peer challenge binding.");
    }
    private static void CheckPlan(ArchiveImportPlan plan, ArchiveSourceReceipt source, Guid destination, Guid sourceVault, Guid destinationVault)
    {
        var ordered = plan.Mapping.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value);
        var mappingDigest = ArchivePeerProof.Fingerprint(JsonSerializer.SerializeToUtf8Bytes(ordered, Json));
        var vaultMappings = plan.Mapping.Where(p => p.Key.StartsWith("vault:", StringComparison.Ordinal)).ToList();
        if (mappingDigest != plan.MappingSha256 || vaultMappings.Count != 1 || vaultMappings[0].Key != "vault:" + sourceVault.ToString("D") || vaultMappings[0].Value != destinationVault.ToString("D")
            || plan.Mapping.Any(p => !p.Key.StartsWith("vault:", StringComparison.Ordinal) && !p.Key.StartsWith("context:", StringComparison.Ordinal)
                && (p.Key.IndexOf(':') < 0 || p.Key[(p.Key.IndexOf(':') + 1)..] != p.Value)))
            throw new ArchiveOperationException(409, "Peer plan substituted its destination mapping or logical identities.");
        if (plan.PlanId == Guid.Empty || plan.ArchiveId != source.ArchiveId || plan.ManifestSha256 != source.ManifestSha256 || plan.SourceSnapshot != source.SourceSnapshot
            || plan.DestinationTenantId != destination || plan.SourceTrust != "trusted" || plan.PreservationState != "complete" || plan.Omissions.Count != 0
            || plan.RequiredPayloadCount != source.PayloadCount || plan.MappingSha256.Length != 64 || plan.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new ArchiveOperationException(409, "Remote plan does not preserve the exact authenticated source archive.");
    }
    private static void CheckReceipt(ArchiveCommitReceipt receipt, ArchiveImportPlan plan)
    {
        if (receipt.PlanId != plan.PlanId || receipt.ArchiveId != plan.ArchiveId || receipt.ManifestSha256 != plan.ManifestSha256 || receipt.SourceSnapshot != plan.SourceSnapshot
            || receipt.DestinationTenantId != plan.DestinationTenantId || receipt.MappingSha256 != plan.MappingSha256 || receipt.RequiredPayloadCount != plan.RequiredPayloadCount
            || receipt.PreservedPayloadCount != plan.RequiredPayloadCount || receipt.SourceTrust != "trusted" || receipt.PreservationState != "complete" || receipt.Omissions.Count != 0 || receipt.CommitId == Guid.Empty)
            throw new ArchiveOperationException(409, "Destination did not return a complete source-bound preservation receipt.");
    }
    private static string Credential(HttpClient client)
    {
        var values = client.DefaultRequestHeaders.GetValues("X-Api-Key").ToArray();
        if (values.Length != 1) throw new ArchiveOperationException(403, "One saved outbound credential is required.");
        return ArchivePeerProof.Fingerprint(Encoding.UTF8.GetBytes(values[0]));
    }
    private static HttpRequestMessage Request(HttpMethod method, string path, string? revision)
    {
        var request = new HttpRequestMessage(method, "api/v1/sync/" + path);
        if (revision != null) request.Headers.Add("X-Knowz-Peer-Binding-Revision", revision);
        return request;
    }
    private static async Task<T> SendJsonAsync<T>(HttpClient client, HttpMethod method, string path, object? body, string? revision, CancellationToken ct)
    {
        using var request = Request(method, path, revision); if (body != null) request.Content = JsonContent.Create(body, options: Json);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); await RequireSuccessAsync(response, ct); return await ReadJsonAsync<T>(response, ct);
    }
    private static async Task RequireSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw new ArchiveOperationException((int)response.StatusCode, $"Saved peer rejected archive operation ({(int)response.StatusCode}); review its current registration and permissions.");
        await Task.CompletedTask;
    }
    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct); using var buffer = new MemoryStream(); var bytes = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(bytes, ct)) > 0) { if (buffer.Length + read > 1024 * 1024) throw new ArchiveOperationException(413, "Peer metadata exceeds its bounded response limit."); await buffer.WriteAsync(bytes.AsMemory(0, read), ct); }
        using var json = StrictJson.Parse(buffer.ToArray(), 1024 * 1024);
        return json.RootElement.Deserialize<T>(Json) ?? throw new ArchiveOperationException(400, "Invalid peer metadata response.");
    }
    private static ArchiveSourceReceipt ReadSourceReceipt(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-Knowz-Source-Receipt", out var values)) throw new ArchiveOperationException(400, "Peer omitted its independent source receipt.");
        var list = values.ToArray(); if (list.Length != 1 || list[0].Length > 16384) throw new ArchiveOperationException(400, "Peer source receipt is malformed.");
        using var json = StrictJson.Parse(Convert.FromBase64String(list[0]), 16384);
        return json.RootElement.Deserialize<ArchiveSourceReceipt>(Json) ?? throw new ArchiveOperationException(400, "Invalid source receipt.");
    }
}
