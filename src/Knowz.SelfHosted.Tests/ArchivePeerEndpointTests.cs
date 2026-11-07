using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.API.Endpoints;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed class ArchivePeerEndpointTests
{
    [Fact]
    public async Task AuthenticatedPeerRegistrationPinsIdentityAndRejectsChangedCredentialOnHttpRoutes()
    {
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(Guid.NewGuid());
        await using var db = new SelfHostedDbContext(new DbContextOptionsBuilder<SelfHostedDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
        var user = new User { TenantId = tenant.TenantId, Username = "peer-http", IsActive = true, ApiKey = "ksh_http_synthetic" };
        db.Tenants.Add(new Tenant { Id = tenant.TenantId, Name = "Peer HTTP tenant", Slug = "peer-http" });
        var vault = new Vault { TenantId = tenant.TenantId, Name = "Peer HTTP vault" }; db.Users.Add(user); db.Vaults.Add(vault); await db.SaveChangesAsync();
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(new SelfHostedArchiveExportService(db, tenant, null!, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()));
        builder.Services.AddSingleton(new SelfHostedArchivePeerService(db, tenant, new VaultAccessService(db), new SelfHostedPeerChallengeStore()));
        await using var app = builder.Build();
        app.Use(async (http, next) => { http.User = new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim("tenantId", tenant.TenantId.ToString()), new Claim(ClaimTypes.Role, "User") }, "SyntheticAuthenticatedUser")); await next(); });
        app.MapArchivePeerEndpoints(); await app.StartAsync(); using var client = app.GetTestClient();
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var intent = new ArchivePeerRegistrationRequest { SourceTenantId = Guid.NewGuid(), SourceVaultId = Guid.NewGuid(), SourceBaseUrl = "https://source.example", SignerFingerprint = ArchivePeerProof.Fingerprint(signer.ExportSubjectPublicKeyInfo()), SignerPublicKey = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()) };
        var denied = await client.PostAsJsonAsync($"/api/v1/sync/vaults/{vault.Id}/archive-peers/challenges", intent); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        client.DefaultRequestHeaders.Add("X-Api-Key", user.ApiKey);
        var response = await client.PostAsJsonAsync($"/api/v1/sync/vaults/{vault.Id}/archive-peers/challenges", intent); response.EnsureSuccessStatusCode();
        var challenge = (await response.Content.ReadFromJsonAsync<ArchivePeerChallenge>())!;
        var proof = ArchivePeerProof.Sign(challenge, signer);
        var accepted = await client.PostAsJsonAsync($"/api/v1/sync/archive-peers/challenges/{challenge.ChallengeId}/accept", proof); accepted.EnsureSuccessStatusCode();
        var stored = await db.ShArchivePeerBindings.SingleAsync(); Assert.Equal(intent.SignerFingerprint, stored.SignerFingerprint);
        Assert.Equal("transport-authenticated-tofu", (await accepted.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("trustProvenance").GetString());
        client.DefaultRequestHeaders.Add("X-Knowz-Peer-Binding-Revision", stored.BindingRevision);
        var current = await client.GetAsync($"/api/v1/sync/archive-peers/{stored.Id}"); current.EnsureSuccessStatusCode();
        var identity = (await client.GetFromJsonAsync<SelfHostedArchiveIdentity>($"/api/v1/sync/vaults/{vault.Id}/archives/identity"))!;
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var signingRequest = new ArchivePeerChallenge { ChallengeId = Guid.NewGuid(), Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), IssuedAt = now, ExpiresAt = now.AddMinutes(2), DestinationTenantId = Guid.NewGuid(), DestinationVaultId = Guid.NewGuid(), SourceTenantId = tenant.TenantId, SourceVaultId = vault.Id, SourceBaseUrl = "https://source.example", SignerFingerprint = identity.SignerFingerprint, SignerPublicKey = identity.SignerPublicKey, CredentialIdentitySha256 = new string('c', 64) };
        var signed = await client.PostAsJsonAsync($"/api/v1/sync/vaults/{vault.Id}/archives/sign-peer-challenge", signingRequest); signed.EnsureSuccessStatusCode();
        ArchivePeerProof.Verify((await signed.Content.ReadFromJsonAsync<ArchivePeerPossessionProof>())!, signingRequest, identity.SignerFingerprint, DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/v1/sync/vaults/{vault.Id}/archives/sign-peer-challenge", signingRequest with { SourceVaultId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/v1/sync/vaults/{vault.Id}/archives/sign-peer-challenge", signingRequest with { Purpose = "sign-arbitrary-archive" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/v1/sync/vaults/{vault.Id}/archives/sign-peer-challenge", signingRequest with { IssuedAt = now.AddHours(-1), ExpiresAt = now.AddMinutes(-58) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/v1/sync/vaults/{vault.Id}/archives/sign-peer-challenge", signingRequest with { SignerPublicKey = intent.SignerPublicKey, SignerFingerprint = intent.SignerFingerprint })).StatusCode);
        user.ApiKey = "ksh_replaced"; await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/sync/archive-peers/{stored.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/v1/sync/archive-peers/challenges/{challenge.ChallengeId}/accept", proof)).StatusCode);
    }
}
