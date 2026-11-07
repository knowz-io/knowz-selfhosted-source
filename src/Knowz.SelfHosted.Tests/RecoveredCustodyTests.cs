using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using Knowz.Core.Configuration;
using Knowz.SelfHosted.API.Endpoints;
using Knowz.SelfHosted.API.Middleware;
using Knowz.SelfHosted.Application.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSec.Cryptography;

namespace Knowz.SelfHosted.Tests;

public sealed partial class RecoveredCustodyTests
{
    [Fact]
    public async Task CustodyHttpUsesProductionAuthenticationAndLiveOwnerChecks()
    {
        await using var f = await Fixture.Create();
        const string jwtSecret = "synthetic-custody-http-secret-32-characters";
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.Configure<SelfHostedOptions>(o => { o.JwtSecret = jwtSecret; o.JwtIssuer = "custody-http-test"; o.ApiKey = "synthetic-legacy-key"; });
        builder.Services.AddSingleton(f.Db); builder.Services.AddSingleton(f.Service);
        await using var app = builder.Build();
        // Match Program.cs: custom authentication populates claims; each endpoint checks live authority.
        app.UseMiddleware<AuthenticationMiddleware>(); app.MapRecoveredCustodyEndpoints(); await app.StartAsync();
        using var client = app.GetTestClient(); var prefix = $"/api/v1/vaults/{f.Vault}/encryption/contexts/{f.Context}";
        var request = new ShCustodyChallengeRequest(f.Plan, 1, f.Fingerprint);
        using (var anonymous = await client.PostAsJsonAsync(prefix + "/custody-challenges", request)) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Add("X-Api-Key", "synthetic-legacy-key");
        using (var legacy = await client.PostAsJsonAsync(prefix + "/custody-challenges", request)) Assert.Equal(HttpStatusCode.Forbidden, legacy.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", JwtTokenHelper.GenerateToken(f.Actor, "synthetic", f.Tenant, UserRole.Admin,
            DateTime.UtcNow.AddMinutes(5), jwtSecret, "custody-http-test", NullLogger.Instance));
        f.CanManage = false;
        using (var denied = await client.PostAsJsonAsync(prefix + "/custody-challenges", request)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        f.CanManage = true;
        using var issued = await client.PostAsJsonAsync(prefix + "/custody-challenges", request); Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        var challenge = (await issued.Content.ReadFromJsonAsync<ShCustodyChallengeResponse>())!;
        using var accepted = await client.PostAsJsonAsync(prefix + "/recovered-custody", new ShCustodyProofRequest(challenge.ChallengeId, f.Prove(challenge)));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode); Assert.Equal("canary-verified", (await accepted.Content.ReadFromJsonAsync<ShCustodyAcceptanceResult>())!.KeyPossession);
        (await f.Db.Users.SingleAsync()).IsActive = false; await f.Db.SaveChangesAsync();
        using var revoked = await client.PostAsJsonAsync(prefix + "/custody-challenges", request); Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
    }

    [Fact]
    public async Task OriginalImportedManagedKeyProvesLocalPossessionAndKeepsSourceCustodyWithoutMothership()
    {
        await using var f = await Fixture.Create();
        var challenge = await f.Service.CreateChallengeAsync(f.Tenant, f.Vault, f.Context, f.Actor, new(f.Plan, 1, f.Fingerprint));
        var proof = f.Prove(challenge); var accepted = await f.Service.AcceptAsync(f.Tenant, f.Vault, f.Context, f.Actor, new(challenge.ChallengeId, proof));
        Assert.Equal("customer-held-local", accepted.EffectiveCustody); Assert.Equal("canary-verified", accepted.KeyPossession);
        Assert.Equal("not-verified", accepted.ArchiveRecovery); Assert.Equal("unverified", accepted.SourceTrust);
        Assert.Equal("platform-managed-private-key-withheld", (await f.Db.ShCryptoKeyIdentities.SingleAsync()).Custody);
        Assert.True(await SelfHostedRecoveredCustodyService.IsAcceptedAsync(f.Db, f.Tenant, await f.Db.ShPortableCryptoContexts.SingleAsync(), await f.Db.ShCryptoKeyIdentities.SingleAsync()));
        var store = new SelfHostedSealedStore(f.Db, f.TenantProvider, NullLogger<SelfHostedSealedStore>.Instance);
        var catalog = await store.GetCatalogAsync(f.Vault, default);
        var selected = await store.SelectPrimaryAsync(f.Vault, f.Context, f.Fingerprint, catalog!.Revision, default, generation: 1);
        Assert.True(selected.Ok, selected.Error);
        var live = await store.GetCatalogAsync(f.Vault, default);
        Assert.Null(SelfHostedSealedStore.ValidatePrimaryReferences(live!, [new() { ContextId = f.Context, Fingerprint = f.Fingerprint, Generation = 1 }]));
        Assert.Equal("platform-managed-private-key-withheld", (await f.Db.ShCryptoKeyIdentities.SingleAsync()).Custody);
        await Assert.ThrowsAsync<ArchiveOperationException>(() => f.Service.AcceptAsync(f.Tenant, f.Vault, f.Context, f.Actor, new(challenge.ChallengeId, proof)));
    }

    [Fact]
    public async Task WrongActorCannotConsumeAndRightfulWrongProofConsumesOnlyItsChallenge()
    {
        await using var f = await Fixture.Create(); var challenge = await f.Issue(); var proof = f.Prove(challenge);
        var other = Guid.NewGuid(); f.Db.Users.Add(new() { Id = other, TenantId = f.Tenant, IsActive = true, Role = UserRole.Admin }); await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.AcceptAsync(f.Tenant, f.Vault, f.Context, other, new(challenge.ChallengeId, proof)));
        await Assert.ThrowsAsync<ArchiveOperationException>(() => f.Accept(challenge, Convert.ToBase64String(new byte[32])));
        await Assert.ThrowsAsync<ArchiveOperationException>(() => f.Accept(challenge, proof)); Assert.Empty(await f.Db.Set<ShRecoveredKeyCustodyAcceptance>().ToListAsync());
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("tenant")]
    [InlineData("membership")]
    [InlineData("manage")]
    [InlineData("key")]
    [InlineData("plan")]
    [InlineData("context")]
    public async Task FreshAuthorityAndExactCatalogPlanExpiryFenceAcceptance(string mutation)
    {
        await using var f = await Fixture.Create(); var challenge = await f.Issue(); var proof = f.Prove(challenge);
        if (mutation == "expired") f.Clock.Utc = f.Now.AddMinutes(6);
        if (mutation == "tenant") (await f.Db.Tenants.SingleAsync()).IsActive = false;
        if (mutation == "membership") (await f.Db.Users.SingleAsync()).IsActive = false;
        if (mutation == "manage") f.CanManage = false;
        if (mutation == "key") (await f.Db.ShCryptoKeyIdentities.SingleAsync()).SourceLifecycle = "retired";
        if (mutation == "context") (await f.Db.ShPortableCryptoContexts.SingleAsync()).OriginalBindingId = Guid.NewGuid();
        if (mutation == "plan") (await f.Db.ShArchiveImportJournals.SingleAsync()).MappingSha256 = new string('d', 64);
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Accept(challenge, proof)); Assert.Empty(await f.Db.Set<ShRecoveredKeyCustodyAcceptance>().ToListAsync());
    }

    [MonorepoCryptoInteropFact]
    [Trait("Category", "PortabilityInterop")]
    public async Task PublishedNodeCryptoDecryptsActualShCanaryAndProducesAcceptedBoundProof()
    {
        await using var f = await Fixture.Create(); var challenge = await f.Issue();
        var start = new ProcessStartInfo("node") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(SourceDirectory(), "Fixtures/custody-canary-consumer.mjs"));
        using var process = Process.Start(start)!; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { privateKey = Convert.ToBase64String(f.Private), response = challenge }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        process.StandardInput.Close(); var proof = await process.StandardOutput.ReadLineAsync(timeout.Token); await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync(timeout.Token));
        var accepted = await f.Accept(challenge, proof!); Assert.Equal("canary-verified", accepted.KeyPossession);
    }
    private static string SourceDirectory([CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;

    [Fact]
    public async Task ConcurrentScopedProofConsumptionAppliesExactlyOnce()
    {
        await using var f = await Fixture.Create(); var challenge = await f.Issue(); var proof = f.Prove(challenge);
        await using var otherDb = new SelfHostedDbContext(f.Options, f.TenantProvider);
        var other = new SelfHostedRecoveredCustodyService(otherDb, f.TenantProvider, f.Access, NullLogger<SelfHostedRecoveredCustodyService>.Instance, f.Clock);
        var outcomes = await Task.WhenAll(new[] { f.Service, other }.Select(async service =>
        { try { await service.AcceptAsync(f.Tenant, f.Vault, f.Context, f.Actor, new(challenge.ChallengeId, proof)); return "accepted"; } catch (ArchiveOperationException) { return "replayed"; } }));
        Assert.Equal(1, outcomes.Count(x => x == "accepted")); Assert.Equal(1, outcomes.Count(x => x == "replayed"));
        Assert.Single(await f.Db.Set<ShRecoveredKeyCustodyAcceptance>().AsNoTracking().ToListAsync());
    }

    private sealed class Clock : TimeProvider { public DateTimeOffset Utc { get; set; } public override DateTimeOffset GetUtcNow() => Utc; }
    private sealed class Fixture : IAsyncDisposable
    {
        public Guid Tenant { get; } = Guid.NewGuid(); public Guid Actor { get; } = Guid.NewGuid(); public Guid Vault { get; } = Guid.NewGuid();
        public Guid Context { get; } = Guid.NewGuid(); public Guid Original { get; } = Guid.NewGuid(); public Guid Plan { get; } = Guid.NewGuid();
        public DateTimeOffset Now { get; } = new(2026, 10, 6, 16, 0, 0, TimeSpan.Zero); public Clock Clock { get; } = new();
        public byte[] Private { get; } = RandomNumberGenerator.GetBytes(32); public string Public { get; private set; } = ""; public string Fingerprint { get; private set; } = "";
        public ITenantProvider TenantProvider { get; } = Substitute.For<ITenantProvider>(); public IVaultAccessService Access { get; } = Substitute.For<IVaultAccessService>();
        public SelfHostedDbContext Db { get; private set; } = null!; public SelfHostedRecoveredCustodyService Service { get; private set; } = null!; public bool CanManage { get; set; } = true;
        public DbContextOptions<SelfHostedDbContext> Options { get; private set; } = new DbContextOptionsBuilder<SelfHostedDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public Task<ShCustodyChallengeResponse> Issue() => Service.CreateChallengeAsync(Tenant, Vault, Context, Actor, new(Plan, 1, Fingerprint));
        public Task<ShCustodyAcceptanceResult> Accept(ShCustodyChallengeResponse response, string proof) => Service.AcceptAsync(Tenant, Vault, Context, Actor, new(response.ChallengeId, proof));
        public string Prove(ShCustodyChallengeResponse response)
        {
            using var privateKey = Key.Import(KeyAgreementAlgorithm.X25519, Private, KeyBlobFormat.RawPrivateKey);
            using var shared = KeyAgreementAlgorithm.X25519.Agree(privateKey, PublicKey.Import(KeyAgreementAlgorithm.X25519, Convert.FromBase64String(response.Envelope.EphemeralPublicKey), KeyBlobFormat.RawPublicKey))!;
            var wrapping = KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(shared, Convert.FromBase64String(response.Envelope.EphemeralPublicKey), "knowz-dek-wrap-v1"u8.ToArray(), 32);
            var dek = new byte[32]; var wrapped = Convert.FromBase64String(response.Envelope.WrappedDek);
            using (var aes = new AesGcm(wrapping, 16)) aes.Decrypt(Convert.FromBase64String(response.Envelope.DekNonce), wrapped.AsSpan(0, 32), wrapped.AsSpan(32), dek);
            var cipher = Convert.FromBase64String(response.Envelope.Ciphertext); var plain = new byte[cipher.Length - 16];
            try
            {
                using (var aes = new AesGcm(dek, 16)) aes.Decrypt(Convert.FromBase64String(response.Envelope.ContentNonce), cipher.AsSpan(0, plain.Length), cipher.AsSpan(plain.Length), plain, Encoding.UTF8.GetBytes($"knowz-msg-v2|{response.ChallengeId:D}|x25519-vault-public|2"));
                using var doc = JsonDocument.Parse(plain); var binding = Convert.FromBase64String(response.BindingRawBase64);
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(binding)), doc.RootElement.GetProperty("bindingSha256").GetString());
                var secret = Convert.FromBase64String(doc.RootElement.GetProperty("secret").GetString()!);
                try { var proof = HMACSHA256.HashData(secret, binding); try { return Convert.ToBase64String(proof); } finally { CryptographicOperations.ZeroMemory(proof); } }
                finally { CryptographicOperations.ZeroMemory(secret); }
            }
            finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(dek); CryptographicOperations.ZeroMemory(wrapping); }
        }
        public static async Task<Fixture> Create(DbContextOptions<SelfHostedDbContext>? options = null)
        {
            var f = new Fixture(); if (options != null) f.Options = options; f.Clock.Utc = f.Now; f.TenantProvider.TenantId.Returns(f.Tenant);
            using var key = Key.Import(KeyAgreementAlgorithm.X25519, f.Private, KeyBlobFormat.RawPrivateKey); f.Public = Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey)); f.Fingerprint = Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(f.Public)));
            f.Db = new(f.Options, f.TenantProvider);
            if (options != null) await f.Db.Database.MigrateAsync();
            f.Db.Tenants.Add(new() { Id = f.Tenant, Name = "synthetic", Slug = "synthetic", IsActive = true }); f.Db.Users.Add(new() { Id = f.Actor, TenantId = f.Tenant, Role = UserRole.Admin, IsActive = true });
            f.Db.Vaults.Add(new() { Id = f.Vault, TenantId = f.Tenant, Name = "destination" });
            f.Db.ShPortableCryptoContexts.Add(new() { Id = f.Context, TenantId = f.Tenant, OriginalBindingId = f.Original });
            f.Db.ShVaultCryptoContextLinks.Add(new() { Id = Guid.NewGuid(), TenantId = f.Tenant, DestinationVaultId = f.Vault, PortableCryptoContextId = f.Context });
            f.Db.ShCryptoKeyIdentities.Add(new() { Id = Guid.NewGuid(), TenantId = f.Tenant, PortableCryptoContextId = f.Context, Generation = 1, Fingerprint = f.Fingerprint, PublicKey = f.Public, SourceLifecycle = "active", Custody = "platform-managed-private-key-withheld" });
            f.Db.ShArchiveImportJournals.Add(new() { PlanId = f.Plan, TenantId = f.Tenant, ActorId = f.Actor, ArchiveId = Guid.NewGuid(), Status = "committed", ManifestSha256 = new string('a', 64), MappingSha256 = new string('b', 64) }); await f.Db.SaveChangesAsync();
            await SelfHostedRecoveredCustodyService.RecordImportAnchorsAsync(f.Db, f.Tenant, f.Plan, (await f.Db.ShArchiveImportJournals.SingleAsync()).ArchiveId, new string('a', 64), new string('b', 64), new string('c', 64), "unverified",
                [new() { ContextId = f.Context, OriginalBindingId = f.Original }], [new() { ContextId = f.Context, Generation = 1, Fingerprint = f.Fingerprint, PublicKey = f.Public }]); await f.Db.SaveChangesAsync();
            f.Access.HasVaultAccessAsync(f.Actor, f.Vault, false, false, true, Arg.Any<CancellationToken>()).Returns(_ => f.CanManage);
            f.Access.HasVaultAccessAsync(Arg.Any<Guid>(), f.Vault, false, false, true, Arg.Any<CancellationToken>()).Returns(_ => f.CanManage);
            f.Service = new(f.Db, f.TenantProvider, f.Access, NullLogger<SelfHostedRecoveredCustodyService>.Instance, f.Clock); return f;
        }
        public async ValueTask DisposeAsync() { CryptographicOperations.ZeroMemory(Private); await Db.DisposeAsync(); }
    }
}

// The canary consumer imports the monorepo's built @knowzai/crypto package, which the standalone
// self-hosted source tree does not carry; skip there instead of failing.
public sealed class MonorepoCryptoInteropFactAttribute : FactAttribute
{
    public MonorepoCryptoInteropFactAttribute([CallerFilePath] string file = "")
    {
        var module = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../../../cli/packages/crypto/dist/portable/vault-crypto.js"));
        if (!File.Exists(module)) Skip = "Requires the monorepo's built cli/packages/crypto (not part of the standalone self-hosted source).";
    }
}
