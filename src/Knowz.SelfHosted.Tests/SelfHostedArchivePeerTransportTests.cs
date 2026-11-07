using System.Security.Claims;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.API.Endpoints;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Validators;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed partial class SelfHostedGraphStoreTests
{
    [Fact]
    public async Task RemoteAcceptanceSurvivesLocalReceiptWriteFailureAndExplicitlyResumesTheExactChallenge()
    {
        var failure = new FailCompletedRegistrationSave(); await using var local = new PeerSide(failure); await using var remote = new PeerSide(); await remote.StartAsync();
        var connection = new PlatformConnection { TenantId = local.Tenant.TenantId, PlatformApiUrl = "https://remote.example", RemoteTenantId = remote.Tenant.TenantId,
            ApiKeyProtected = local.Protection.CreateProtector(PlatformConnectionService.MasterPurpose).CreateProtector($"{PlatformConnectionService.MasterPurpose}.{local.Tenant.TenantId}").Protect(remote.User.ApiKey!) };
        local.Db.PlatformConnections.Add(connection); var link = new VaultSyncLink { LocalVaultId = local.Vault.Id, RemoteTenantId = remote.Tenant.TenantId, RemoteVaultId = remote.Vault.Id, PlatformConnectionId = connection.Id, SyncEnabled = true }; local.Db.VaultSyncLinks.Add(link); await local.Db.SaveChangesAsync();
        var factory = Substitute.For<IHttpClientFactory>(); factory.CreateClient(PlatformSyncCredentialResolver.HttpClientName).Returns(_ => remote.App!.GetTestClient());
        var validator = Substitute.For<IUrlValidator>(); validator.ValidatePlatformUrl("https://remote.example").Returns(new UrlValidationResult(true, null));
        var transport = new SelfHostedArchivePeerTransport(local.Db, local.Tenant, local.Peers, local.Exporter, local.Adapter, local.Coordinator,
            new PlatformSyncCredentialResolver(factory, local.Protection, validator, local.Db, NullLogger<PlatformSyncCredentialResolver>.Instance));
        failure.Armed = true;
        await Assert.ThrowsAsync<ArchiveOperationException>(() => transport.RegisterAsync(local.User.Id, link.Id, "https://local.example", null, default));
        local.Db.ChangeTracker.Clear(); remote.Db.ChangeTracker.Clear();
        var accepted = await remote.Db.ShArchivePeerBindings.AsNoTracking().SingleAsync();
        var pending = await local.Db.ShArchivePeerBindings.AsNoTracking().SingleAsync(); Assert.Null(pending.LocalSourceBaseUrl);
        var resumed = await transport.RegisterAsync(local.User.Id, link.Id, "https://local.example", pending.BindingRevision, default);
        Assert.Equal(accepted.Id, resumed.RemoteBindingId); Assert.Equal(accepted.BindingRevision, resumed.RemoteBindingRevision);
        Assert.Equal("https://local.example", resumed.LocalSourceBaseUrl);
        Assert.Single(await remote.Db.ShArchivePeerBindings.ToListAsync());
        await Assert.ThrowsAsync<ArchiveOperationException>(() => transport.RegisterAsync(local.User.Id, link.Id, "https://local.example", pending.BindingRevision, default));
    }

    [Fact]
    public async Task SavedConnectionStreamsMaximumCiphertextAndRecoversItWithTheOriginalX25519Key()
    {
        await using var local = new PeerSide(); await using var remote = new PeerSide(); await remote.StartAsync();
        using var originalKey = NSec.Cryptography.Key.Create(NSec.Cryptography.KeyAgreementAlgorithm.X25519);
        var publicBytes = originalKey.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey); var publicKey = Convert.ToBase64String(publicBytes);
        var fingerprint = ArchivePeerProof.Fingerprint(publicBytes); var context = Guid.NewGuid(); var fileId = Guid.NewGuid();
        var original = System.Security.Cryptography.RandomNumberGenerator.GetBytes(64 * 1024 * 1024 - 16);
        var envelope = ShCustodyCanaryCrypto.Encrypt(publicKey, original, remote.Vault.Id);
        var package = new PortableExportPackage { SchemaVersion = EncryptedGraphProfile.SchemaVersion, RequiredCapabilities = [EncryptedGraphProfile.Capability], SourceEdition = "selfhosted", SourceTenantId = remote.Tenant.TenantId,
            Data = new() { Vaults = [new() { Id = remote.Vault.Id, Name = remote.Vault.Name }] },
            CryptoContexts = [new() { ContextId = context, OriginalBindingId = remote.Vault.Id }],
            CryptoKeyCatalog = [new() { ContextId = context, PublicKey = publicKey, Fingerprint = fingerprint, Generation = 1, Lifecycle = "active" }], VaultCryptoContextLinks = [new() { VaultId = remote.Vault.Id, ContextId = context }],
            SealedFiles = [new() { Id = fileId, OriginalFileId = fileId, VaultId = remote.Vault.Id, SizeBytes = 64 * 1024 * 1024, BinaryContentBase64 = envelope.Ciphertext,
                EnvelopeVersion = 2, EncryptedDek = envelope.WrappedDek, EncryptedDekNonce = envelope.DekNonce, ContentNonce = envelope.ContentNonce, EphemeralPublicKey = envelope.EphemeralPublicKey, CryptoBindingId = remote.Vault.Id }],
            PayloadKeyReferences = [new() { OwnerKind = "file", OwnerId = fileId, Role = "binary", ContextId = context, Fingerprint = fingerprint, OriginalBindingId = remote.Vault.Id, EnvelopeVersion = 2 }]
        };
        Assert.Null(EncryptedGraphProfile.Validate(package));
        Assert.True((await remote.Graph.ImportAsync(package, remote.Vault.Id, "trusted:maximum-file", ImportConflictStrategy.Skip, null, default)).Success);
        var connection = new PlatformConnection { TenantId = local.Tenant.TenantId, PlatformApiUrl = "https://remote.example", RemoteTenantId = remote.Tenant.TenantId,
            ApiKeyProtected = local.Protection.CreateProtector(PlatformConnectionService.MasterPurpose).CreateProtector($"{PlatformConnectionService.MasterPurpose}.{local.Tenant.TenantId}").Protect(remote.User.ApiKey!) };
        local.Db.PlatformConnections.Add(connection); var link = new VaultSyncLink { LocalVaultId = local.Vault.Id, RemoteTenantId = remote.Tenant.TenantId, RemoteVaultId = remote.Vault.Id, PlatformConnectionId = connection.Id, SyncEnabled = true }; local.Db.VaultSyncLinks.Add(link); await local.Db.SaveChangesAsync();
        var factory = Substitute.For<IHttpClientFactory>(); factory.CreateClient(PlatformSyncCredentialResolver.HttpClientName).Returns(_ => { var client = remote.App!.GetTestClient(); client.MaxResponseContentBufferSize = 1024 * 1024; return client; });
        var validator = Substitute.For<IUrlValidator>(); validator.ValidatePlatformUrl("https://remote.example").Returns(new UrlValidationResult(true, null));
        var transport = new SelfHostedArchivePeerTransport(local.Db, local.Tenant, local.Peers, local.Exporter, local.Adapter, local.Coordinator,
            new PlatformSyncCredentialResolver(factory, local.Protection, validator, local.Db, NullLogger<PlatformSyncCredentialResolver>.Instance));
        await transport.RegisterAsync(local.User.Id, link.Id, "https://local.example", null, default);
        var receipt = await transport.TransferAsync(local.User.Id, link.Id, "pull", default); Assert.Equal("complete", receipt.PreservationState);
        var restored = await local.Db.ShSealedFiles.SingleAsync(); Assert.Equal(package.SealedFiles[0].OriginalFileId, restored.OriginalFileId); Assert.Equal(remote.Vault.Id, restored.CryptoBindingId);
        await using var stream = await local.Graph.OpenFileAsync(restored.Id, default); using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer); var cipher = buffer.ToArray(); Assert.Equal(64 * 1024 * 1024, cipher.Length);
        var ephemeral = Convert.FromBase64String(restored.EphemeralPublicKey!);
        using var shared = NSec.Cryptography.KeyAgreementAlgorithm.X25519.Agree(originalKey, NSec.Cryptography.PublicKey.Import(NSec.Cryptography.KeyAgreementAlgorithm.X25519, ephemeral, NSec.Cryptography.KeyBlobFormat.RawPublicKey));
        var wrapping = NSec.Cryptography.KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(shared!, ephemeral, "knowz-dek-wrap-v1"u8.ToArray(), 32);
        var dek = new byte[32]; var wrapped = Convert.FromBase64String(restored.EncryptedDek!);
        using (var aes = new System.Security.Cryptography.AesGcm(wrapping, 16)) aes.Decrypt(Convert.FromBase64String(restored.EncryptedDekNonce!), wrapped.AsSpan(0, 32), wrapped.AsSpan(32), dek);
        var recovered = new byte[cipher.Length - 16]; using (var aes = new System.Security.Cryptography.AesGcm(dek, 16)) aes.Decrypt(Convert.FromBase64String(restored.ContentNonce!), cipher.AsSpan(0, recovered.Length), cipher.AsSpan(recovered.Length), recovered, Encoding.UTF8.GetBytes($"knowz-msg-v2|{remote.Vault.Id:D}|x25519-vault-public|2"));
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(original), System.Security.Cryptography.SHA256.HashData(recovered));
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(dek); System.Security.Cryptography.CryptographicOperations.ZeroMemory(wrapping);
    }

    [Fact]
    public async Task SubstitutedRemoteChallengeLeavesDualRegistrationIncompleteAndCannotResumeAnOldReceipt()
    {
        await using var local = new PeerSide(); await using var remote = new PeerSide(); await remote.StartAsync();
        var connection = new PlatformConnection { TenantId = local.Tenant.TenantId, PlatformApiUrl = "https://remote.example", RemoteTenantId = remote.Tenant.TenantId,
            ApiKeyProtected = local.Protection.CreateProtector(PlatformConnectionService.MasterPurpose).CreateProtector($"{PlatformConnectionService.MasterPurpose}.{local.Tenant.TenantId}").Protect(remote.User.ApiKey!) };
        local.Db.PlatformConnections.Add(connection);
        var link = new VaultSyncLink { LocalVaultId = local.Vault.Id, RemoteTenantId = remote.Tenant.TenantId, RemoteVaultId = remote.Vault.Id, PlatformConnectionId = connection.Id, SyncEnabled = true };
        local.Db.VaultSyncLinks.Add(link); await local.Db.SaveChangesAsync();
        var factory = Substitute.For<IHttpClientFactory>(); factory.CreateClient(PlatformSyncCredentialResolver.HttpClientName).Returns(_ => remote.App!.GetTestClient());
        var validator = Substitute.For<IUrlValidator>(); validator.ValidatePlatformUrl("https://remote.example").Returns(new UrlValidationResult(true, null));
        var resolver = new PlatformSyncCredentialResolver(factory, local.Protection, validator, local.Db, NullLogger<PlatformSyncCredentialResolver>.Instance);
        var transport = new SelfHostedArchivePeerTransport(local.Db, local.Tenant, local.Peers, local.Exporter, local.Adapter, local.Coordinator, resolver);
        var first = await transport.RegisterAsync(local.User.Id, link.Id, "https://local.example", null, default); var firstRevision = first.BindingRevision; var remoteId = first.RemoteBindingId;
        remote.RewriteResponse = (path, body) => { if (path.EndsWith("/archive-peers/challenges")) body["sourceVaultId"] = Guid.NewGuid().ToString("D"); return body; };
        await Assert.ThrowsAsync<ArchiveOperationException>(() => transport.RegisterAsync(local.User.Id, link.Id, "https://local.example", firstRevision, default));
        var incomplete = await local.Db.ShArchivePeerBindings.AsNoTracking().SingleAsync();
        Assert.NotEqual(firstRevision, incomplete.BindingRevision); Assert.Equal(remoteId, incomplete.RemoteBindingId);
        Assert.Null(incomplete.LocalSourceBaseUrl);
        await Assert.ThrowsAsync<ArchiveOperationException>(() => transport.TransferAsync(local.User.Id, link.Id, "pull", default));
    }

    [Fact]
    public async Task SavedConnectionRegistersBothPinsAndStreamsMixedArchiveWithoutChangingOriginalCiphertext()
    {
        await using var local = new PeerSide(); await using var remote = new PeerSide();
        var (package, _, _) = Fixture();
        Assert.True((await remote.Graph.ImportAsync(package, remote.Vault.Id, "trusted:fixture", ImportConflictStrategy.Skip, null, default)).Success);
        await remote.StartAsync();
        var connection = new PlatformConnection { TenantId = local.Tenant.TenantId, PlatformApiUrl = "https://remote.example", RemoteTenantId = remote.Tenant.TenantId,
            ApiKeyProtected = local.Protection.CreateProtector(PlatformConnectionService.MasterPurpose).CreateProtector($"{PlatformConnectionService.MasterPurpose}.{local.Tenant.TenantId}").Protect(remote.User.ApiKey!) };
        local.Db.PlatformConnections.Add(connection);
        var link = new VaultSyncLink { LocalVaultId = local.Vault.Id, RemoteTenantId = remote.Tenant.TenantId, RemoteVaultId = remote.Vault.Id, PlatformConnectionId = connection.Id, SyncEnabled = true };
        local.Db.VaultSyncLinks.Add(link); await local.Db.SaveChangesAsync();
        var factory = Substitute.For<IHttpClientFactory>(); factory.CreateClient(PlatformSyncCredentialResolver.HttpClientName).Returns(_ => remote.App!.GetTestClient());
        var validator = Substitute.For<IUrlValidator>(); validator.ValidatePlatformUrl("https://remote.example").Returns(new UrlValidationResult(true, null));
        var resolver = new PlatformSyncCredentialResolver(factory, local.Protection, validator, local.Db, NullLogger<PlatformSyncCredentialResolver>.Instance);
        var transport = new SelfHostedArchivePeerTransport(local.Db, local.Tenant, local.Peers, local.Exporter, local.Adapter, local.Coordinator, resolver);
        await local.StartAsync(transport); using var localClient = local.App!.GetTestClient(); localClient.DefaultRequestHeaders.Add("X-Api-Key", local.User.ApiKey);
        var registration = await localClient.PostAsJsonAsync($"/api/v1/sync/links/{link.Id:D}/archive-peer/register", new { localSourceBaseUrl = "https://local.example" }); registration.EnsureSuccessStatusCode();
        var binding = (await registration.Content.ReadFromJsonAsync<ShArchivePeerBinding>())!;
        Assert.NotNull(binding.RemoteBindingId); Assert.NotNull(binding.RemoteBindingRevision); Assert.Equal("https://local.example", binding.LocalSourceBaseUrl);
        var transferred = await localClient.PostAsJsonAsync($"/api/v1/sync/links/{link.Id:D}/archives/transfer", new { direction = "pull" }); transferred.EnsureSuccessStatusCode();
        var received = (await transferred.Content.ReadFromJsonAsync<ArchiveCommitReceipt>())!;
        Assert.Equal("complete", received.PreservationState); Assert.Equal(received.RequiredPayloadCount, received.PreservedPayloadCount);
        var original = await remote.Graph.ExportAsync(new() { Type = ExportScopeType.Vault, VaultIds = [remote.Vault.Id] }, false, default);
        var copied = await local.Graph.ExportAsync(new() { Type = ExportScopeType.Vault, VaultIds = [local.Vault.Id] }, false, default);
        Assert.Equal(original.SealedItems![0].Id, copied.SealedItems![0].Id); Assert.Equal(original.SealedItems[0].Ciphertext, copied.SealedItems[0].Ciphertext);
        Assert.Equal(original.SealedItems[0].CryptoBindingId, copied.SealedItems[0].CryptoBindingId);
        await using (var before = await remote.Graph.OpenFileAsync(original.SealedFiles![0].Id, default))
        await using (var after = await local.Graph.OpenFileAsync(copied.SealedFiles![0].Id, default))
        { using var a = new MemoryStream(); using var b = new MemoryStream(); await before.CopyToAsync(a); await after.CopyToAsync(b); Assert.Equal(a.ToArray(), b.ToArray()); }
        var note = await local.Db.KnowledgeItems.SingleAsync(k => k.Content == "plain sibling"); note.Content = "edited locally after pull"; await local.Db.SaveChangesAsync();
        var pushed = await transport.TransferAsync(local.User.Id, link.Id, "push", default);
        Assert.Equal("complete", pushed.PreservationState); Assert.Equal("edited locally after pull", (await remote.Db.KnowledgeItems.AsNoTracking().SingleAsync(k => k.Id == note.Id)).Content);
        Assert.Equal(2, await remote.Db.KnowledgeItems.CountAsync());
        remote.RewriteResponse = (path, body) => { if (path.EndsWith("/imports/plan")) body["mapping"]!.AsObject()["vault:" + local.Vault.Id.ToString("D")] = Guid.NewGuid().ToString("D"); return body; };
        note.Content = "must not activate through a substituted mapping"; await local.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArchiveOperationException>(() => transport.TransferAsync(local.User.Id, link.Id, "push", default));
        Assert.Equal("edited locally after pull", (await remote.Db.KnowledgeItems.AsNoTracking().SingleAsync(k => k.Id == note.Id)).Content);
        remote.RewriteResponse = null;
        var oldRevision = binding.BindingRevision;
        await Assert.ThrowsAsync<ArchiveOperationException>(() => transport.RegisterAsync(local.User.Id, link.Id, "https://local.example", null, default));
        Assert.Equal(EntityState.Detached,local.Db.Entry(link).State);
        var liveLink=await local.Db.VaultSyncLinks.SingleAsync(l=>l.Id==link.Id);
        var changedVault=Guid.NewGuid();liveLink.RemoteVaultId=changedVault;await local.Db.SaveChangesAsync();
        Assert.Equal(changedVault,(await local.Db.VaultSyncLinks.AsNoTracking().SingleAsync(l=>l.Id==link.Id)).RemoteVaultId);
        await Assert.ThrowsAsync<ArchiveOperationException>(() => transport.TransferAsync(local.User.Id, link.Id, "pull", default));
        Assert.Equal(oldRevision, (await local.Db.ShArchivePeerBindings.AsNoTracking().SingleAsync()).BindingRevision);
        validator.Received().ValidatePlatformUrl("https://remote.example");
    }

    private sealed class PeerSide : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "peer-stream-" + Guid.NewGuid().ToString("N"));
        public ITenantProvider Tenant { get; } = Substitute.For<ITenantProvider>();
        public SelfHostedDbContext Db { get; }
        public User User { get; }
        public Vault Vault { get; }
        public IDataProtectionProvider Protection { get; } = new EphemeralDataProtectionProvider();
        public SelfHostedGraphStore Graph { get; }
        public SelfHostedArchivePeerService Peers { get; }
        public SelfHostedArchiveExportService Exporter { get; }
        public SelfHostedArchiveImportAdapter Adapter { get; }
        public ArchiveImportCoordinator Coordinator { get; } = new();
        public WebApplication? App { get; private set; }
        public Func<string, JsonObject, JsonObject>? RewriteResponse { get; set; }
        public JsonObject? CapabilitiesResponse { get; set; }
        public int ArchivePlanBodyPosts { get; private set; }
        public PeerSide(SaveChangesInterceptor? interceptor = null, string? postgresConnection = null)
        {
            Tenant.TenantId.Returns(Guid.NewGuid());
            var options = new DbContextOptionsBuilder<SelfHostedDbContext>();
            if (postgresConnection != null) options.UseNpgsql(postgresConnection,provider=>provider.EnableRetryOnFailure());
            else options.UseInMemoryDatabase(Guid.NewGuid().ToString()).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
            if (interceptor != null) options.AddInterceptors(interceptor);
            Db = new(options.Options, Tenant);
            if (postgresConnection != null) Db.Database.Migrate();
            User = new() { TenantId = Tenant.TenantId, Username = "peer-stream", IsActive = true, ApiKey = "ksh_" + Guid.NewGuid().ToString("N") };
            Vault = new() { TenantId = Tenant.TenantId, Name = "Stream vault" };
            Db.Tenants.Add(new() { Id = Tenant.TenantId, Name = "Stream tenant", Slug = "stream" }); Db.Users.Add(User); Db.Vaults.Add(Vault); Db.SaveChanges();
            var storage = new LocalFileStorageProvider(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Local:RootPath"] = directory }).Build(), NullLogger<LocalFileStorageProvider>.Instance);
            Graph = new(Db, Tenant, new PortableImportService(Db, Tenant, storage, NullLogger<PortableImportService>.Instance),
                new PortableExportService(Db, Tenant, storage, Options.Create(new SelfHostedOptions()), NullLogger<PortableExportService>.Instance), storage);
            Peers = new(Db, Tenant, new VaultAccessService(Db), new SelfHostedPeerChallengeStore());
            Exporter = new(Db, Tenant, Graph, Protection); Adapter = new(Db, Tenant, Graph, new VaultAccessService(Db), Peers);
        }
        public async Task StartAsync(SelfHostedArchivePeerTransport? transport = null)
        {
            var builder = WebApplication.CreateBuilder(); builder.Configuration["AllowedHosts"] = "*"; builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(Db); builder.Services.AddSingleton(Peers); builder.Services.AddSingleton(Exporter); builder.Services.AddSingleton(Adapter); builder.Services.AddSingleton(Coordinator);
            if (transport != null) builder.Services.AddSingleton(transport);
            App = builder.Build(); App.Use(async (http, next) => { http.User = new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, User.Id.ToString()), new Claim("tenantId", Tenant.TenantId.ToString()), new Claim(ClaimTypes.Role, "User") }, "SyntheticAuthenticatedUser"));
                if (Microsoft.AspNetCore.Http.HttpMethods.IsPost(http.Request.Method) && http.Request.Path.Value!.Contains("/imports/plan")) ArchivePlanBodyPosts++;
                if (RewriteResponse == null) { await next(); return; }
                var original = http.Response.Body; using var buffered = new MemoryStream(); http.Response.Body = buffered; await next();
                http.Response.Body = original; var altered = RewriteResponse(http.Request.Path.Value!, JsonNode.Parse(buffered.ToArray())!.AsObject()).ToJsonString();
                var bytes = Encoding.UTF8.GetBytes(altered); http.Response.ContentLength = bytes.Length; await original.WriteAsync(bytes);
            });
            App.MapGet("/api/v1/portability/capabilities", () => CapabilitiesResponse == null ? Microsoft.AspNetCore.Http.Results.NotFound() : Microsoft.AspNetCore.Http.Results.Json(CapabilitiesResponse)); App.MapArchivePeerEndpoints(); await App.StartAsync();
        }
        public async ValueTask DisposeAsync() { if (App != null) await App.DisposeAsync(); Coordinator.Dispose(); await Db.DisposeAsync(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private sealed class FailCompletedRegistrationSave : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Armed && data.Context!.ChangeTracker.Entries<ShArchivePeerBinding>().Any(e => e.Entity.LocalSourceBaseUrl != null))
            { Armed = false; throw new DbUpdateException("Synthetic local write failure after successful remote registration."); }
            return ValueTask.FromResult(result);
        }
    }
}
