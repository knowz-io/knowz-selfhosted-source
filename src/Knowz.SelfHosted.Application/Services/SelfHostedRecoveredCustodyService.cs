using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Application.Services;

public sealed record ShCustodyChallengeRequest(Guid PlanId, int Generation, string Fingerprint);
public sealed record ShCustodyChallengeResponse(Guid ChallengeId, string BindingRawBase64, ShCustodyEnvelope Envelope);
public sealed record ShCustodyProofRequest(Guid ChallengeId, string Proof);
public sealed record ShCustodyAcceptanceResult(Guid AcceptanceId, Guid ContextId, int Generation, string Fingerprint,
    string EffectiveCustody, string KeyPossession, string ArchiveRecovery, string SourceTrust);

/// <summary>Independent local possession consent for imported keys. No KMS or original source availability is required.</summary>
public sealed class SelfHostedRecoveredCustodyService(SelfHostedDbContext db, ITenantProvider tenant, IVaultAccessService access,
    ILogger<SelfHostedRecoveredCustodyService> logger, TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Gates = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private sealed record Imported(ShPortableCryptoContext Context, ShCryptoKeyIdentity Key, ShCryptoContextImportAnchor Anchor);

    public static async Task RecordImportAnchorsAsync(SelfHostedDbContext db, Guid tenantId, Guid planId, Guid archiveId,
        string manifestSha256, string mappingSha256, string packageSha256, string sourceTrust, IReadOnlyList<ExportedCryptoContext> mappedContexts,
        IReadOnlyList<ExportedCryptoKeyIdentity> mappedKeys, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty || planId == Guid.Empty || sourceTrust is not ("trusted" or "unverified") || !EncryptedGraphProfile.IsSha256(manifestSha256)
            || !EncryptedGraphProfile.IsSha256(mappingSha256) || !EncryptedGraphProfile.IsSha256(packageSha256)) throw new ArchiveOperationException(409, "Invalid committed context anchor.");
        foreach (var context in mappedContexts)
        {
            var keys = mappedKeys.Where(k => k.ContextId == context.ContextId).OrderBy(k => k.Generation).ToList();
            if (keys.Count > 256) throw new ArchiveOperationException(409, "Imported public key history exceeds its bound.");
            var catalog = JsonSerializer.Serialize(keys, Json);
            var existing = await db.Set<ShCryptoContextImportAnchor>().AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenantId && a.PlanId == planId && a.ContextId == context.ContextId, ct);
            var digest = PlanDigest(planId, context.ContextId, manifestSha256, mappingSha256, packageSha256, catalog);
            if (existing != null)
            { if (existing.PlanSha256 != digest || existing.SourceTrust != sourceTrust) throw new ArchiveOperationException(409, "Committed context anchor cannot be replaced."); }
            else db.Add(new ShCryptoContextImportAnchor { TenantId = tenantId, PlanId = planId, ArchiveId = archiveId, ContextId = context.ContextId,
                OriginalBindingId = context.OriginalBindingId, ManifestSha256 = manifestSha256, MappingSha256 = mappingSha256, PackageSha256 = packageSha256,
                SourceTrust = sourceTrust, PublicKeyCatalogJson = catalog, PlanSha256 = digest });
        }
        // The adapter owns the graph commit transaction and saves these associations atomically.
    }

    public async Task<ShCustodyChallengeResponse> CreateChallengeAsync(Guid tenantId, Guid vaultId, Guid contextId, Guid actorId, ShCustodyChallengeRequest request, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear(); await RequireManageAsync(tenantId, vaultId, actorId, ct);
        var key = await ReadImportedAsync(tenantId, vaultId, contextId, request.PlanId, request.Generation, request.Fingerprint, ct);
        var row = new ShCryptoCustodyChallenge { TenantId = tenantId, ActorId = actorId, VaultId = vaultId, ContextId = contextId, KeyId = key.Key.Id,
            PlanId = request.PlanId, Generation = request.Generation, Fingerprint = request.Fingerprint, KeyStateSha256 = KeyDigest(key.Context, key.Key),
            PlanSha256 = key.Anchor.PlanSha256, ExpiresAt = _clock.GetUtcNow().AddMinutes(5).UtcDateTime };
        var nonce = RandomNumberGenerator.GetBytes(32); var secret = RandomNumberGenerator.GetBytes(32); byte[]? canary = null; byte[]? proof = null;
        try
        {
            var binding = JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, challengeId = row.Id.ToString("D"), actorId = actorId.ToString("D"),
                destinationTenantId = tenantId.ToString("D"), destinationVaultId = vaultId.ToString("D"), contextId = contextId.ToString("D"), originalBindingId = key.Context.OriginalBindingId.ToString("D"),
                generation = key.Key.Generation, fingerprint = key.Key.Fingerprint, planId = request.PlanId.ToString("D"), planSha256 = key.Anchor.PlanSha256,
                purpose = "customer-held-custody-acceptance", operation = "accept-custody", nonce = Convert.ToBase64String(nonce),
                expiresAt = row.ExpiresAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) }, Json);
            row.BindingJson = Encoding.UTF8.GetString(binding); proof = HMACSHA256.HashData(secret, binding); row.ExpectedProofSha256 = Hash(proof);
            canary = EncodeCanary(secret, Hash(binding)); var envelope = ShCustodyCanaryCrypto.Encrypt(key.Key.PublicKey, canary, row.Id);
            await RequireManageAsync(tenantId, vaultId, actorId, ct);
            var fresh = await ReadImportedAsync(tenantId, vaultId, contextId, request.PlanId, request.Generation, request.Fingerprint, ct);
            if (KeyDigest(fresh.Context, fresh.Key) != row.KeyStateSha256 || fresh.Anchor.PlanSha256 != row.PlanSha256) throw new ArchiveOperationException(409, "Imported key changed during challenge issuance.");
            db.Add(row); await db.SaveChangesAsync(ct); return new(row.Id, Convert.ToBase64String(binding), envelope);
        }
        finally
        { CryptographicOperations.ZeroMemory(secret); CryptographicOperations.ZeroMemory(nonce); if (canary != null) CryptographicOperations.ZeroMemory(canary); if (proof != null) CryptographicOperations.ZeroMemory(proof); }
    }

    public async Task<ShCustodyAcceptanceResult> AcceptAsync(Guid tenantId, Guid vaultId, Guid contextId, Guid actorId, ShCustodyProofRequest request, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        var challenge = await db.Set<ShCryptoCustodyChallenge>().IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(c => c.TenantId == tenantId && c.Id == request.ChallengeId, ct);
        if (challenge == null || challenge.ActorId != actorId || challenge.VaultId != vaultId || challenge.ContextId != contextId) throw new UnauthorizedAccessException("Custody challenge belongs to another actor or scope.");
        await RequireManageAsync(tenantId, vaultId, actorId, ct); await ConsumeAsync(challenge, ct);
        try
        {
            if (challenge.ExpiresAt <= _clock.GetUtcNow().UtcDateTime) throw new ArchiveOperationException(409, "Custody challenge expired.");
            byte[] submitted;
            try { submitted = Decode(request.Proof); } catch (FormatException) { throw new ArchiveOperationException(409, "Invalid custody proof; create a new challenge."); }
            try
            {
                var actual = SHA256.HashData(submitted); var expected = Convert.FromHexString(challenge.ExpectedProofSha256);
                try { if (!CryptographicOperations.FixedTimeEquals(actual, expected)) throw new ArchiveOperationException(409, "Invalid custody proof; create a new challenge."); }
                finally { CryptographicOperations.ZeroMemory(actual); CryptographicOperations.ZeroMemory(expected); }
            }
            finally { CryptographicOperations.ZeroMemory(submitted); }
            var result = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                // No tracked write from a failed attempt may be replayed after rollback or a lost COMMIT response.
                db.ChangeTracker.Clear();
                await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct) : null;
                await RequireManageAsync(tenantId, vaultId, actorId, ct);
                var current = await db.ShCryptoCustodyChallenges.IgnoreQueryFilters().SingleAsync(c => c.Id == challenge.Id && c.TenantId == tenantId, ct);
                if (current.ActorId != actorId || current.VaultId != vaultId || current.ContextId != contextId
                    || current.ExpectedProofSha256 != challenge.ExpectedProofSha256 || current.Status is not ("consumed" or "accepted"))
                    throw new ArchiveOperationException(409, "Custody challenge changed or was blocked.");
                var state = await ReadImportedAsync(tenantId, vaultId, contextId, challenge.PlanId, challenge.Generation, challenge.Fingerprint, ct);
                if (KeyDigest(state.Context, state.Key) != challenge.KeyStateSha256 || state.Anchor.PlanSha256 != challenge.PlanSha256)
                    throw new ArchiveOperationException(409, "Imported key or committed plan changed.");
                var accepted = await db.Set<ShRecoveredKeyCustodyAcceptance>().SingleOrDefaultAsync(a => a.TenantId == tenantId && a.ContextId == contextId && a.KeyId == state.Key.Id, ct);
                if (accepted == null)
                {
                    if (current.Status == "accepted") throw new ArchiveOperationException(409, "Committed custody receipt is unavailable.");
                    accepted = new() { TenantId = tenantId, ActorId = actorId, ContextId = contextId, KeyId = state.Key.Id, PlanId = challenge.PlanId,
                        Generation = challenge.Generation, Fingerprint = challenge.Fingerprint, KeyStateSha256 = challenge.KeyStateSha256,
                        SourceTrust = state.Anchor.SourceTrust, AcceptedAt = _clock.GetUtcNow().UtcDateTime };
                    db.Add(accepted);
                }
                else if (accepted.KeyStateSha256 != challenge.KeyStateSha256)
                    throw new ArchiveOperationException(409, "Prior local custody consent differs from this key.");
                current.Status = "accepted";
                await RequireManageAsync(tenantId, vaultId, actorId, ct); await db.SaveChangesAsync(ct);
                if (transaction != null) await transaction.CommitAsync(ct);
                return new ShCustodyAcceptanceResult(accepted.Id, contextId, challenge.Generation, challenge.Fingerprint,
                    "customer-held-local", "canary-verified", "not-verified", state.Anchor.SourceTrust);
            });
            await RequireManageAsync(tenantId, vaultId, actorId, ct);
            logger.LogInformation("Exact local key possession recorded for context {ContextId}; archive recovery remains unverified", contextId);
            return result;
        }
        catch
        {
            db.ChangeTracker.Clear();
            if (db.Database.IsRelational()) await db.Set<ShCryptoCustodyChallenge>().IgnoreQueryFilters().Where(c => c.TenantId == tenantId && c.Id == challenge.Id && c.Status == "consumed").ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, "blocked"), CancellationToken.None);
            else { var row = await db.Set<ShCryptoCustodyChallenge>().IgnoreQueryFilters().SingleAsync(c => c.Id == challenge.Id); row.Status = "blocked"; await db.SaveChangesAsync(CancellationToken.None); }
            throw;
        }
    }
    private async Task ConsumeAsync(ShCryptoCustodyChallenge c, CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            var count = await db.Set<ShCryptoCustodyChallenge>().IgnoreQueryFilters().Where(x => x.Id == c.Id && x.TenantId == c.TenantId && x.ActorId == c.ActorId && x.VaultId == c.VaultId && x.ContextId == c.ContextId && x.Status == "issued")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "consumed"), ct);
            if (count != 1) throw new ArchiveOperationException(409, "Custody challenge was already consumed.");
        }
        else
        {
            var gate = Gates.GetOrAdd(c.Id, _ => new(1, 1)); await gate.WaitAsync(ct);
            try { var row = await db.Set<ShCryptoCustodyChallenge>().SingleAsync(x => x.Id == c.Id, ct); if (row.Status != "issued") throw new ArchiveOperationException(409, "Custody challenge was already consumed."); row.Status = "consumed"; await db.SaveChangesAsync(ct); }
            finally { gate.Release(); }
        }
    }
    private async Task RequireManageAsync(Guid tenantId, Guid vaultId, Guid actorId, CancellationToken ct)
    {
        if (tenant.TenantId != tenantId || actorId == Guid.Empty || !await db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId && t.IsActive, ct)) throw new UnauthorizedAccessException("Current active destination tenant is required.");
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == actorId && u.IsActive, ct);
        var membership = await db.UserTenantMemberships.AsNoTracking().SingleOrDefaultAsync(m => m.UserId == actorId && m.TenantId == tenantId, ct);
        if (user == null || (membership != null ? !membership.IsActive : user.TenantId != tenantId)
            || !await db.Vaults.IgnoreQueryFilters().AnyAsync(v => v.Id == vaultId && v.TenantId == tenantId && !v.IsDeleted, ct)
            || !await access.HasVaultAccessAsync(actorId, vaultId, requireManage: true, ct: ct)) throw new UnauthorizedAccessException("Fresh destination membership and manage authority are required.");
    }
    private async Task<Imported> ReadImportedAsync(Guid tenantId, Guid vaultId, Guid contextId, Guid planId, int generation, string fingerprint, CancellationToken ct)
    {
        var context = await db.ShPortableCryptoContexts.AsNoTracking().SingleOrDefaultAsync(c => c.TenantId == tenantId && c.Id == contextId, ct);
        var key = await db.ShCryptoKeyIdentities.AsNoTracking().SingleOrDefaultAsync(k => k.TenantId == tenantId && k.PortableCryptoContextId == contextId && k.Generation == generation && k.Fingerprint == fingerprint, ct);
        var anchor = await db.Set<ShCryptoContextImportAnchor>().AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenantId && a.ContextId == contextId && a.PlanId == planId, ct);
        var plan = await db.ShArchiveImportJournals.AsNoTracking().SingleOrDefaultAsync(p => p.PlanId == planId && p.TenantId == tenantId && p.Status == "committed", ct);
        if (generation < 1 || !EncryptedGraphProfile.IsSha256(fingerprint) || context is not { BindingKind: "vault", AlgorithmProfile: "x25519-vault" } || key == null || anchor == null || plan == null
            || anchor.OriginalBindingId != context.OriginalBindingId || anchor.ManifestSha256 != plan.ManifestSha256 || anchor.MappingSha256 != plan.MappingSha256
            || !await db.ShVaultCryptoContextLinks.AnyAsync(l => l.TenantId == tenantId && l.DestinationVaultId == vaultId && l.PortableCryptoContextId == contextId, ct)
            || Hash(Decode(key.PublicKey)) != fingerprint || key.KeyType is not ("x25519" or "x25519-vault-public")) throw new ArchiveOperationException(409, "Exact imported key/context/plan is unavailable.");
        var keys = JsonSerializer.Deserialize<List<ExportedCryptoKeyIdentity>>(anchor.PublicKeyCatalogJson, Json) ?? [];
        if (!keys.Any(k => k.ContextId == contextId && k.Generation == generation && k.PublicKey == key.PublicKey && k.Fingerprint == fingerprint)
            || anchor.PlanSha256 != PlanDigest(planId, contextId, anchor.ManifestSha256, anchor.MappingSha256, anchor.PackageSha256, anchor.PublicKeyCatalogJson))
            throw new ArchiveOperationException(409, "This key was not admitted by the committed plan.");
        return new(context, key, anchor);
    }
    public static Task<bool> IsAcceptedAsync(SelfHostedDbContext db, Guid tenantId, ShPortableCryptoContext context, ShCryptoKeyIdentity key, CancellationToken ct = default)
    {
        var digest = KeyDigest(context, key);
        return db.Set<ShRecoveredKeyCustodyAcceptance>().AsNoTracking().AnyAsync(a => a.TenantId == tenantId && a.ContextId == context.Id && a.KeyId == key.Id
            && a.Generation == key.Generation && a.Fingerprint == key.Fingerprint && a.KeyStateSha256 == digest, ct);
    }
    public static string KeyDigest(ShPortableCryptoContext context, ShCryptoKeyIdentity key) => Hash(JsonSerializer.SerializeToUtf8Bytes(new
    { context.Id, context.TenantId, context.OriginalBindingId, context.BindingKind, context.AlgorithmProfile, context.SourceTenantId, keyId = key.Id, key.Generation, key.Fingerprint, key.PublicKey, key.KeyType, key.SourceLifecycle, key.Custody }, Json));
    private static string PlanDigest(Guid plan, Guid context, string manifest, string mapping, string package, string catalog)
        => Hash(Encoding.UTF8.GetBytes($"knowz-custody-plan-v1|{plan:D}|{context:D}|{manifest}|{mapping}|{package}|{Hash(Encoding.UTF8.GetBytes(catalog))}"));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] Decode(string value)
    { if (value == null || value.Length != 44) throw new FormatException(); var bytes = Convert.FromBase64String(value); if (bytes.Length != 32 || Convert.ToBase64String(bytes) != value) throw new FormatException(); return bytes; }
    private sealed class CanaryBuffer : IBufferWriter<byte>, IDisposable
    {
        private readonly byte[] _bytes = new byte[4096]; private int _length;
        public void Advance(int count) { if (count < 0 || count > _bytes.Length - _length) throw new InvalidOperationException(); _length += count; }
        public Memory<byte> GetMemory(int sizeHint = 0) { if (sizeHint > _bytes.Length - _length) throw new InvalidOperationException(); return _bytes.AsMemory(_length); }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public byte[] Copy() => _bytes.AsSpan(0, _length).ToArray(); public void Dispose() => CryptographicOperations.ZeroMemory(_bytes);
    }
    private static byte[] EncodeCanary(byte[] secret, string digest)
    {
        using var buffer = new CanaryBuffer(); using (var writer = new Utf8JsonWriter(buffer))
        { writer.WriteStartObject(); writer.WriteNumber("formatVersion", 1); writer.WriteString("bindingSha256", digest); writer.WriteBase64String("secret", secret); writer.WriteEndObject(); writer.Flush(); }
        return buffer.Copy();
    }
}
