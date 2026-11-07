using System.Data;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text.Json;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Application.Services;

public sealed record SelfHostedArchiveIdentity(string SignerFingerprint, string SignerPublicKey);
public sealed record SelfHostedArchiveExport(ProtectedSpoolStream Stream, ArchiveSourceReceipt Receipt);

public sealed class SelfHostedArchiveExportService(SelfHostedDbContext db, ITenantProvider tenant,
    SelfHostedGraphStore graph, IDataProtectionProvider protection)
{
    private static readonly SemaphoreSlim IdentityGate = new(1, 1);
    private IDataProtector Protector => protection.CreateProtector("Knowz.SelfHosted.ArchiveSigning.v1", tenant.TenantId.ToString("D"));
    private async Task<ECDsa> SigningKeyAsync(CancellationToken ct)
    {
        await IdentityGate.WaitAsync(ct);
        try
        {
            var row = await db.ShArchiveSigningIdentities.AsNoTracking().SingleOrDefaultAsync(ct);
            if (row == null)
            {
                using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var encoded = generated.ExportPkcs8PrivateKey();
                try
                {
                    row = new() { TenantId = tenant.TenantId, ProtectedPrivateKey = Convert.ToBase64String(Protector.Protect(encoded)) };
                    db.ShArchiveSigningIdentities.Add(row);
                    try { await db.SaveChangesAsync(ct); }
                    catch (DbUpdateException)
                    {
                        db.Entry(row).State = EntityState.Detached;
                        row = await db.ShArchiveSigningIdentities.SingleAsync(ct);
                    }
                }
                finally { CryptographicOperations.ZeroMemory(encoded); }
            }
            var bytes = Protector.Unprotect(Convert.FromBase64String(row.ProtectedPrivateKey));
            var key = ECDsa.Create();
            try { key.ImportPkcs8PrivateKey(bytes, out var read); if (read != bytes.Length) throw new CryptographicException("Invalid signing identity."); return key; }
            catch { key.Dispose(); throw; }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { IdentityGate.Release(); }
    }
    public async Task<SelfHostedArchiveIdentity> IdentityAsync(CancellationToken ct)
    {
        using var key = await SigningKeyAsync(ct); var publicKey = key.ExportSubjectPublicKeyInfo();
        return new(Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant(), Convert.ToBase64String(publicKey));
    }
    public async Task<ArchivePeerPossessionProof> SignPeerChallengeAsync(Guid vault, ArchivePeerChallenge challenge, Func<CancellationToken, Task> requireLiveAuthority, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requireLiveAuthority);
        await requireLiveAuthority(ct);
        _ = ArchivePeerProof.Canonicalize(challenge);
        if (challenge.SourceTenantId != tenant.TenantId || challenge.SourceVaultId != vault) throw new ArchiveOperationException(403, "Peer challenge names another source tenant or vault.");
        var now = DateTimeOffset.UtcNow;
        if (challenge.ExpiresAt <= now || challenge.IssuedAt > now) throw new ArchiveOperationException(400, "Peer challenge is expired or not yet valid.");
        using var key = await SigningKeyAsync(ct);
        var proof = ArchivePeerProof.Sign(challenge, key);
        await requireLiveAuthority(ct);
        if ((await IdentityAsync(ct)).SignerFingerprint != challenge.SignerFingerprint) throw new ArchiveOperationException(409, "Source signer changed; explicitly review registration again.");
        return proof;
    }
    public static string Snapshot(PortableExportPackage package)
    {
        var stamp = package.ExportedAt;
        try { package.ExportedAt = default; return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(package))).ToLowerInvariant(); }
        finally { package.ExportedAt = stamp; }
    }
    public Task<SelfHostedArchiveExport> ExportAsync(ExportScope scope, bool allowPartial, CancellationToken ct)
        => db.Database.CreateExecutionStrategy().ExecuteAsync(() => ExportSnapshotAsync(scope, allowPartial, ct));

    private async Task<SelfHostedArchiveExport> ExportSnapshotAsync(ExportScope scope, bool allowPartial, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        using var key = await SigningKeyAsync(ct);
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction == null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        var package = await graph.ExportAsync(scope, allowPartial, ct);
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 1024 * 1024, resumeWriterThreshold: 512 * 1024));
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var write = ProduceAsync();
        ProtectedSpoolStream? spool = null;
        try
        {
            await using var input = pipe.Reader.AsStream();
            spool = await ProtectedSpoolStream.CreateAsync(input, ct: abort.Token);
            var receipt = await write;
            if (transaction != null) await transaction.CommitAsync(ct);
            return new(spool, receipt);
        }
        catch
        {
            abort.Cancel(); await pipe.Reader.CompleteAsync();
            try { await write; } catch { }
            spool?.Dispose(); throw;
        }
        async Task<ArchiveSourceReceipt> ProduceAsync()
        {
            Exception? failure = null;
            try { return await KnowzArchiveWriter.WriteAsync(pipe.Writer.AsStream(leaveOpen: true), PortableGraphPackages.Partition(package), graph.OpenFileAsync,
                new() { SigningKey = key, SourceSnapshot = Snapshot(package), AllowPartial = allowPartial }, abort.Token); }
            catch (Exception ex) { failure = ex; throw; }
            finally { await pipe.Writer.CompleteAsync(failure); }
        }
    }
}
