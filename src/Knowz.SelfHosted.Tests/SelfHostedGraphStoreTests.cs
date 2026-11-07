using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using System.Security.Cryptography;
using System.Text;
using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed partial class SelfHostedGraphStoreTests : IDisposable
{
    [Fact]
    public async Task InactiveRecoveryGraphCannotSilentlyDropPoliciesOrActivateOrdinaryPlainSiblings()
    {
        var (source, _, _) = Fixture(); source.RequiredCapabilities!.Add(SuspendedReleaseProfile.Capability);
        source.SuspendedReleasePolicies = [new() { OriginalLockId = Guid.NewGuid(), KnowledgeId = source.SealedItems![0].Id,
            OriginalKnowledgeId = source.SealedItems[0].OriginalItemId, SourceTenantId = source.SourceTenantId, OriginalOwnerUserId = Guid.NewGuid(), OriginalIsSealed = true }];
        Assert.Null(EncryptedGraphProfile.Validate(source));
        var result = await _graph.ImportAsync(source, null, "trusted:unsupported-inactive", ImportConflictStrategy.Skip, null, default);
        Assert.False(result.Success); Assert.Contains("inactive", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await _db.KnowledgeItems.ToListAsync()); Assert.Empty(await _db.ShSealedFiles.ToListAsync());
    }

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "knowz-graph-" + Guid.NewGuid().ToString("N"));
    private readonly SelfHostedDbContext _db;
    private readonly LocalFileStorageProvider _storage;
    private readonly SelfHostedGraphStore _graph;
    private readonly SelfHostedSealedStore _lifecycle;
    public SelfHostedGraphStoreTests()
    {
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        _db = new SelfHostedDbContext(new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options, tenant);
        _db.Tenants.Add(new Tenant { Id = _tenant, Name = "Owned graph fixture", Slug = "graph-fixture" });
        _db.SaveChanges();
        _storage = new LocalFileStorageProvider(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Local:RootPath"] = _directory }).Build(), NullLogger<LocalFileStorageProvider>.Instance);
        var importer = new PortableImportService(_db, tenant, _storage, NullLogger<PortableImportService>.Instance);
        var exporter = new PortableExportService(_db, tenant, _storage, Options.Create(new SelfHostedOptions()), NullLogger<PortableExportService>.Instance);
        _graph = new SelfHostedGraphStore(_db, tenant, importer, exporter, _storage);
        _lifecycle = new SelfHostedSealedStore(_db, tenant, NullLogger<SelfHostedSealedStore>.Instance);
    }
    public void Dispose() { _db.Dispose(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public void PeerBindingModel_PersistsPublicIdentityAndRevisionWithoutCredentials()
    {
        var entity = _db.Model.FindEntityType("Knowz.SelfHosted.Infrastructure.Data.Entities.ShArchivePeerBinding");
        Assert.NotNull(entity);
        foreach (var property in new[] { "TenantId", "DestinationVaultId", "SourceTenantId", "SourceVaultId", "SourceBaseUrl", "BindingSlotSha256", "SignerFingerprint", "SignerPublicKey", "CredentialIdentitySha256", "PrincipalUserId", "PlatformConnectionId", "SyncLinkId", "RemoteBindingId", "RemoteBindingRevision", "LocalSourceBaseUrl", "TrustProvenance", "RegisteredByUserId", "BindingRevision", "IsEnabled" }) Assert.NotNull(entity.FindProperty(property));
        Assert.True(entity.FindProperty("BindingRevision")!.IsConcurrencyToken);
        Assert.DoesNotContain(entity.GetProperties(), p => p.Name.Contains("PrivateKey") || p.Name == "ApiKey" || p.Name.Contains("Password"));
    }

    [Fact]
    public async Task SignedPeerArchivePreservesLogicalIdsAndRejectsBindingChangeBeforeCommit()
    {
        var (source, _, _) = Fixture(); var vault = new Vault { TenantId = _tenant, Name = "Registered destination" };
        var user = new User { TenantId = _tenant, Username = "sync-owner", IsActive = true, ApiKey = "ksh_synthetic-peer" };
        _db.Vaults.Add(vault); _db.Users.Add(user); await _db.SaveChangesAsync();
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var peers = new SelfHostedArchivePeerService(_db, tenant, new VaultAccessService(_db), new SelfHostedPeerChallengeStore());
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credential = ArchivePeerProof.Fingerprint(Encoding.UTF8.GetBytes(user.ApiKey));
        var challenge = await peers.ChallengeAsync(user.Id, credential, vault.Id, new() { SourceTenantId = source.SourceTenantId, SourceVaultId = source.Data.Vaults.Single().Id,
            SourceBaseUrl = "https://source.example", SignerFingerprint = ArchivePeerProof.Fingerprint(signer.ExportSubjectPublicKeyInfo()), SignerPublicKey = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()) }, default);
        var binding = await peers.AcceptAsync(user.Id, credential, challenge.ChallengeId, ArchivePeerProof.Sign(challenge, signer), default);
        var adapter = new SelfHostedArchiveImportAdapter(_db, tenant, _graph, new VaultAccessService(_db), peers).ForPeer(binding);
        using var coordinator = new ArchiveImportCoordinator();
        async Task<ArchiveImportPlan> Plan(string operation)
        {
            using var input = new MemoryStream(); var receipt = await KnowzArchiveWriter.WriteAsync(input, [source], null, new() { SigningKey = signer, SourceSnapshot = operation }); input.Position = 0;
            return await coordinator.CreatePlanAsync(input, adapter.PeerActor, new() { TargetVaultId = vault.Id, Strategy = "overwrite", IdempotencyKey = "sync:" + binding.Id.ToString("D") + ":" + operation,
                ExpectedSignerFingerprint = binding.SignerFingerprint, SourceReceipt = receipt }, adapter);
        }
        var first = await Plan("one");
        Assert.Empty(await _db.ShCryptoContextImportAnchors.ToListAsync());
        Assert.Equal(source.SealedItems![0].Id.ToString("D"), first.Mapping["knowledge:" + source.SealedItems[0].Id.ToString("D")]);
        Assert.NotEqual(source.CryptoContexts![0].ContextId.ToString("D"), first.Mapping["context:" + source.CryptoContexts[0].ContextId.ToString("D")]);
        await Assert.ThrowsAsync<ArchiveOperationException>(() => coordinator.CommitAsync(first.PlanId, new(_tenant, user.Id.ToString("D")), adapter));
        var receipt = await coordinator.CommitAsync(first.PlanId, adapter.PeerActor, adapter);
        Assert.Equal(receipt.RequiredPayloadCount, receipt.PreservedPayloadCount);
        var anchor = Assert.Single(await _db.ShCryptoContextImportAnchors.ToListAsync());
        Assert.Equal(first.PlanId, anchor.PlanId); Assert.Equal(first.ManifestSha256, anchor.ManifestSha256); Assert.Equal(first.MappingSha256, anchor.MappingSha256);
        Assert.Equal(Guid.Parse(first.Mapping["context:" + source.CryptoContexts![0].ContextId.ToString("D")]), anchor.ContextId);
        Assert.Equal(source.CryptoContexts[0].OriginalBindingId, anchor.OriginalBindingId);
        Assert.Equal(source.SealedItems[0].Id, (await _db.ShSealedKnowledge.SingleAsync()).KnowledgeId);
        await using (var bytes = await _graph.OpenFileAsync(source.SealedFiles![0].Id, default))
        { using var copied = new MemoryStream(); await bytes.CopyToAsync(copied); Assert.Equal(Convert.FromBase64String(source.SealedFiles[0].BinaryContentBase64!), copied.ToArray()); }
        var next = await Plan("two");
        (await _db.ShArchivePeerBindings.SingleAsync()).BindingRevision = Guid.NewGuid().ToString("N"); await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArchiveOperationException>(() => coordinator.CommitAsync(next.PlanId, adapter.PeerActor, adapter));
        Assert.Equal(1, await _db.ShArchiveImportJournals.CountAsync(j => j.Status == "committed"));
    }

    [Fact]
    public async Task MixedGraph_RemapsContext_PreservesSharedCommentFiles_AndRetriesWithoutHistoryGrowth()
    {
        var (package, key, plaintext) = Fixture();
        var original = package.SealedItems![0];
        var imported = await _graph.ImportAsync(package, null, "trusted:fixture-source", ImportConflictStrategy.Skip, null, CancellationToken.None);
        Assert.True(imported.Success, imported.Error);
        var restored = await _graph.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None);
        Assert.Null(EncryptedGraphProfile.Validate(restored));
        Assert.Contains(restored.Data.KnowledgeItems, k => k.Content == "plain sibling");
        var sealedItem = Assert.Single(restored.SealedItems!);
        Assert.NotEqual(original.Id, sealedItem.Id);
        Assert.Equal(original.CryptoBindingId, sealedItem.CryptoBindingId);
        Assert.Equal(original.Ciphertext, sealedItem.Ciphertext);
        Assert.NotEqual(package.CryptoContexts![0].ContextId, restored.CryptoContexts![0].ContextId);
        Assert.Equal(package.CryptoKeyCatalog![0].Fingerprint, restored.CryptoKeyCatalog![0].Fingerprint);
        var file = Assert.Single(restored.SealedFiles!);
        Assert.Equal(package.SealedFiles![0].OriginalFileId, file.OriginalFileId);
        Assert.Equal(2, restored.SealedAttachments!.Count);
        Assert.Contains(restored.SealedAttachments, a => a.CommentId == restored.SealedComments![0].Id);
        await using var binary = await _graph.OpenFileAsync(file.Id, CancellationToken.None);
        using var bytes = new MemoryStream(); await binary.CopyToAsync(bytes);
        var ciphertext = bytes.ToArray();
        var decrypted = new byte[ciphertext.Length - 16];
        using (var aes = new AesGcm(key, 16)) aes.Decrypt(Convert.FromBase64String(file.ContentNonce!), ciphertext.AsSpan(0, decrypted.Length), ciphertext.AsSpan(decrypted.Length), decrypted);
        Assert.Equal(plaintext, decrypted);
        Assert.True((await _graph.ImportAsync(package, null, "trusted:fixture-source", ImportConflictStrategy.Skip, null, CancellationToken.None)).Success);
        Assert.Single(await _db.ShSealedKnowledge.ToListAsync());
        Assert.Empty(await _db.ShSealedVersions.ToListAsync());
        Assert.Equal(2, await _db.ShSealedAttachments.CountAsync());
    }

    [Fact]
    public async Task PlanPublicKeyMutation_IsRejectedBeforeAnyNewStageIsWritten()
    {
        var (source, _, _) = Fixture(); using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var actor = new User { Id = Guid.NewGuid(), TenantId = _tenant, Username = "preflight-admin", Role = Knowz.Core.Enums.UserRole.Admin, IsActive = true }; _db.Users.Add(actor); await _db.SaveChangesAsync();
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant); var adapter = new SelfHostedArchiveImportAdapter(_db, tenant, _graph, new VaultAccessService(_db));
        using var coordinator = new ArchiveImportCoordinator(); var identity = new ArchiveActor(_tenant, actor.Id.ToString());
        async Task<ArchiveImportPlan> Plan(string key)
        {
            using var input = new MemoryStream(); var receipt = await KnowzArchiveWriter.WriteAsync(input, [source], null, new() { SigningKey = signer, SourceSnapshot = key }); input.Position = 0;
            return await coordinator.CreatePlanAsync(input, identity, new() { ExpectedSignerFingerprint = receipt.SignerFingerprint, IdempotencyKey = key }, adapter);
        }
        var original = await Plan("original"); await coordinator.CommitAsync(original.PlanId, identity, adapter);
        var publicKey = RandomNumberGenerator.GetBytes(32); var fingerprint = Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();
        source.CryptoKeyCatalog![0].PublicKey = Convert.ToBase64String(publicKey); source.CryptoKeyCatalog[0].Fingerprint = fingerprint;
        foreach (var reference in source.PayloadKeyReferences!) reference.Fingerprint = fingerprint;
        Assert.Null(EncryptedGraphProfile.Validate(source));
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => Plan("mutated-key")); Assert.Equal(422, error.StatusCode);
        Assert.Single(await _db.ShArchiveImportJournals.ToListAsync());
        Assert.Single(Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SkipDoesNotClaimPreservedFilesWhenTheExistingPhysicalBytesAreMissing()
    {
        var (source, _, _) = Fixture(); Assert.True((await _graph.ImportAsync(source, null, "trusted:missing-existing", ImportConflictStrategy.Skip, null, CancellationToken.None)).Success);
        var row = await _db.ShSealedFiles.SingleAsync(); await _storage.DeleteAsync(_tenant, row.BlobStorageId!.Value, CancellationToken.None);
        var result = await _graph.ImportAsync(source, null, "trusted:missing-existing", ImportConflictStrategy.Skip, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Single(await _db.ShSealedFiles.ToListAsync());
    }

    [Fact]
    public async Task PlainFileWithEncryptedMetadata_PreservesTypedFieldReferencesAndOriginalBytes()
    {
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web); json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var source = System.Text.Json.JsonSerializer.Deserialize<PortableExportPackage>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portable-graph-v1.json")), json)!;
        var file = source.SealedFiles!.Single(); var bytes = Encoding.UTF8.GetBytes("Unencrypted original binary with protected filename");
        source.Data.FileRecords.Add(new() { Id = file.Id, OriginalFileId = file.OriginalFileId, FileName = "encrypted-file", ContentType = "application/octet-stream", SizeBytes = bytes.Length, BinaryContentBase64 = Convert.ToBase64String(bytes),
            EncryptedFieldsJson = file.EncryptedFieldsJson, Attachments = [new() { KnowledgeId = source.SealedItems!.Single().Id }] });
        source.SealedFiles.Clear(); source.PayloadKeyReferences!.RemoveAll(r => r.OwnerKind == "file" && r.Role == "binary");
        Assert.Null(EncryptedGraphProfile.Validate(source));
        var result = await _graph.ImportAsync(source, null, "trusted:field-only-file", ImportConflictStrategy.Skip, null, CancellationToken.None); Assert.True(result.Success, result.Error);
        var stored = await _db.FileRecords.SingleAsync(); Assert.Equal(file.EncryptedFieldsJson, stored.EncryptedFieldsJson); Assert.Equal(file.OriginalFileId, stored.OriginalFileId);
        var exported = await _graph.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None);
        Assert.Null(EncryptedGraphProfile.Validate(exported)); var restored = exported.Data.FileRecords.Single();
        stored.FileName = "legacy clear filename must not escape"; await _db.SaveChangesAsync();
        var safeProjection = await _graph.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None);
        Assert.Equal("encrypted-file", safeProjection.Data.FileRecords.Single().FileName);
        Assert.Equal(file.EncryptedFieldsJson, restored.EncryptedFieldsJson); Assert.Equal(file.OriginalFileId, restored.OriginalFileId);
        Assert.Contains(exported.PayloadKeyReferences!, r => r.OwnerKind == "file" && r.OwnerId == restored.Id && r.Role == "field");
        using var stream = await _graph.OpenFileAsync(restored.Id, CancellationToken.None); using var actual = new MemoryStream(); await stream.CopyToAsync(actual); Assert.Equal(bytes, actual.ToArray());
        var user = new User { TenantId = _tenant, Username = "protected-file-reader", Role = Knowz.Core.Enums.UserRole.Admin, IsActive = true }; _db.Users.Add(user); await _db.SaveChangesAsync();
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(_db); builder.Services.AddSingleton(_lifecycle); builder.Services.AddSingleton(_graph);
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        builder.Services.AddSingleton(new SelfHostedSealedFiles(_db, tenant, _lifecycle, _storage));
        builder.Services.AddSingleton<Knowz.SelfHosted.Application.Interfaces.IVaultAccessService>(new VaultAccessService(_db));
        await using var app = builder.Build();
        app.Use(async (context, next) => { context.User = new(new System.Security.Claims.ClaimsIdentity([new(System.Security.Claims.ClaimTypes.NameIdentifier, user.Id.ToString()), new("tenantId", _tenant.ToString()), new(System.Security.Claims.ClaimTypes.Role, "Admin")], "fixture")); await next(); });
        Knowz.SelfHosted.API.Endpoints.EncryptedVaultEndpoints.MapEncryptedVaultEndpoints(app); await app.StartAsync();
        using var client = app.GetTestClient(); var vault = exported.Data.Vaults.Single().Id;
        var payload = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v1/vaults/{vault}/encryption/payloads/file/{restored.Id}");
        Assert.Equal(file.EncryptedFieldsJson, payload.GetProperty("record").GetProperty("encryptedFieldsJson").GetString());
        Assert.False(payload.GetProperty("record").TryGetProperty("encryptedDek", out _));
        Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/v1/vaults/{vault}/encryption/files/{restored.Id}"));
        var catalog = (await _lifecycle.GetCatalogAsync(vault, CancellationToken.None))!; var key = catalog.Keys.Single();
        Assert.True((await _lifecycle.SelectPrimaryAsync(vault, key.ContextId, key.Fingerprint, catalog.Revision, CancellationToken.None, key.Generation)).Ok);
        using var update = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/encrypted/files/{restored.Id}") { Content = JsonContent.Create(new { vaultId = vault, record = payload.GetProperty("record"), payloadKeyReferences = payload.GetProperty("payloadKeyReferences") }) };
        update.Headers.TryAddWithoutValidation("If-Match", payload.GetProperty("revision").GetString());
        using var updated = await client.SendAsync(update); updated.EnsureSuccessStatusCode();
        Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/v1/vaults/{vault}/encryption/files/{restored.Id}"));
        var latest = await updated.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var changedRefs = System.Text.Json.JsonSerializer.Deserialize<List<PortablePayloadKeyReference>>(latest.GetProperty("payloadKeyReferences"), json)!;
        changedRefs.Single().OriginalItemId = Guid.NewGuid();
        using var rebound = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/encrypted/files/{restored.Id}") { Content = JsonContent.Create(new { vaultId = vault, record = latest.GetProperty("record"), payloadKeyReferences = changedRefs }) };
        rebound.Headers.TryAddWithoutValidation("If-Match", latest.GetProperty("revision").GetString());
        using var rejected = await client.SendAsync(rebound); Assert.Equal(System.Net.HttpStatusCode.BadRequest, rejected.StatusCode);
        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/encrypted/files/{restored.Id}"); delete.Headers.TryAddWithoutValidation("If-Match", latest.GetProperty("revision").GetString());
        using var deleted = await client.SendAsync(delete); deleted.EnsureSuccessStatusCode();
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/vaults/{vault}/encryption/files/{restored.Id}")).StatusCode);



    }

    [Fact]
    public async Task PlainBinaryHistoryWithProtectedFields_RoundTripsLogicalLineageAndPublicProjection()
    {
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web); json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var source = System.Text.Json.JsonSerializer.Deserialize<PortableExportPackage>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portable-graph-v1.json")), json)!;
        var sealedFile = source.SealedFiles!.Single(); var firstId = sealedFile.Id; var nextId = Guid.NewGuid();
        var oldBytes = Encoding.UTF8.GetBytes("plain original history"); var nextBytes = Encoding.UTF8.GetBytes("plain current history");
        source.Data.FileRecords = [new() { Id = firstId, OriginalFileId = sealedFile.OriginalFileId, FileName = "encrypted-file", EncryptedFieldsJson = sealedFile.EncryptedFieldsJson, SizeBytes = oldBytes.Length, BinaryContentBase64 = Convert.ToBase64String(oldBytes), IsCurrentVersion = false },
            new() { Id = nextId, OriginalFileId = sealedFile.OriginalFileId, FileName = "encrypted-file", EncryptedFieldsJson = sealedFile.EncryptedFieldsJson, SizeBytes = nextBytes.Length, BinaryContentBase64 = Convert.ToBase64String(nextBytes), FileVersion = 2, RootFileRecordId = firstId, PreviousFileRecordId = firstId, Attachments = [new() { KnowledgeId = source.SealedItems!.Single().Id }] }];
        source.SealedFiles.Clear(); foreach (var attachment in source.SealedAttachments!) attachment.FileRecordId = nextId;
        source.PayloadKeyReferences!.RemoveAll(r => r.OwnerKind == "file" && r.Role == "binary");
        var nextRef = System.Text.Json.JsonSerializer.Deserialize<PortablePayloadKeyReference>(System.Text.Json.JsonSerializer.Serialize(source.PayloadKeyReferences.Single(r => r.OwnerKind == "file")))!; nextRef.OwnerId = nextId; source.PayloadKeyReferences.Add(nextRef);
        Assert.Null(EncryptedGraphProfile.Validate(source));
        Assert.True((await _graph.ImportAsync(source, null, "trusted:plain-history", ImportConflictStrategy.Skip, null, default)).Success);
        var vault = (await _db.Vaults.SingleAsync()).Id;
        var exported = await _graph.ExportAsync(new() { Type = ExportScopeType.Vault, VaultIds = [vault] }, false, default); Assert.Null(EncryptedGraphProfile.Validate(exported));
        Assert.Equal(2, exported.Data.FileRecords.Count); var current = exported.Data.FileRecords.Single(f => f.IsCurrentVersion); var historical = exported.Data.FileRecords.Single(f => !f.IsCurrentVersion);
        Assert.Equal(historical.Id, current.RootFileRecordId); Assert.Equal(historical.Id, current.PreviousFileRecordId);
        var projection = (await SelfHostedSealedFiles.GetProtectedFileAsync(_db, current.Id, default))!;
        Assert.Equal(2, projection.FileVersion); Assert.Equal(historical.Id, projection.RootFileRecordId); Assert.Equal(historical.Id, projection.PreviousFileRecordId);
        foreach (var (id, expected) in new[] { (historical.Id, oldBytes), (current.Id, nextBytes) }) { await using var stream = await _graph.OpenFileAsync(id, default); using var bytes = new MemoryStream(); await stream.CopyToAsync(bytes); Assert.Equal(expected, bytes.ToArray()); }
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant); var service = new SelfHostedSealedFiles(_db, tenant, _lifecycle, _storage);
        var oldProjection = (await SelfHostedSealedFiles.GetProtectedFileAsync(_db, historical.Id, default))!;
        var oldRefs = exported.PayloadKeyReferences!.Where(r => r.OwnerKind == "file" && r.OwnerId == historical.Id).ToList();
        Assert.Equal(409, (await service.UpdateProtectedFieldsAsync(new() { VaultId = vault, Record = oldProjection, PayloadKeyReferences = oldRefs }, SelfHostedSealedStore.PayloadRevision(oldProjection), default)).StatusCode);
        Assert.Equal(409, (await service.DeleteFileAsync(historical.Id, SelfHostedSealedStore.PayloadRevision(oldProjection), default)).StatusCode);
        var catalog = (await _lifecycle.GetCatalogAsync(vault, default))!; var identity = catalog.Keys.Single();
        Assert.True((await _lifecycle.SelectPrimaryAsync(vault, identity.ContextId, identity.Fingerprint, catalog.Revision, default, identity.Generation)).Ok);
        var refs = exported.PayloadKeyReferences.Where(r => r.OwnerKind == "file" && r.OwnerId == current.Id).ToList();
        var metadata = await service.UpdateProtectedFieldsAsync(new() { VaultId = vault, Record = projection, PayloadKeyReferences = refs }, SelfHostedSealedStore.PayloadRevision(projection), default);
        Assert.True(metadata.Ok, metadata.Error);
        projection = (await SelfHostedSealedFiles.GetProtectedFileAsync(_db, current.Id, default))!;
        var expectedRevision = SelfHostedSealedStore.PayloadRevision(projection);
        var replacementBytes = Encoding.UTF8.GetBytes("third ordinary version with protected original-key filename");
        projection.SizeBytes = replacementBytes.Length; projection.BinarySha256 = Convert.ToHexStringLower(SHA256.HashData(replacementBytes));
        var user = new User { TenantId = _tenant, Username = "plain-file-writer", IsActive = true }; _db.Users.Add(user); await _db.SaveChangesAsync();
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Services.AddSingleton(_db); builder.Services.AddSingleton(_lifecycle); builder.Services.AddSingleton(_graph); builder.Services.AddSingleton(service);
        builder.Services.AddSingleton<Knowz.SelfHosted.Application.Interfaces.IVaultAccessService>(new VaultAccessService(_db));
        await using var app = builder.Build(); app.Use(async (http, next) => { http.User = new(new System.Security.Claims.ClaimsIdentity([new(System.Security.Claims.ClaimTypes.NameIdentifier, user.Id.ToString()), new("tenantId", _tenant.ToString()), new(System.Security.Claims.ClaimTypes.Role, "Admin")], "fixture")); await next(); });
        Knowz.SelfHosted.API.Endpoints.EncryptedVaultEndpoints.MapEncryptedVaultEndpoints(app); await app.StartAsync(); using var client = app.GetTestClient();
        using var form = new MultipartFormDataContent(); form.Add(new StringContent(System.Text.Json.JsonSerializer.Serialize(projection, json)), "record"); form.Add(new StringContent(System.Text.Json.JsonSerializer.Serialize(refs, json)), "payloadKeyReferences"); form.Add(new StringContent(vault.ToString()), "vaultId"); form.Add(new ByteArrayContent(replacementBytes), "binary", "encrypted-file");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/encrypted/files/{current.Id}") { Content = form }; request.Headers.TryAddWithoutValidation("If-Match", expectedRevision);
        using var response = await client.SendAsync(request); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(); var newId = result.GetProperty("ownerId").GetGuid(); Assert.NotEqual(current.Id, newId);
        var latest = (await SelfHostedSealedFiles.GetProtectedFileAsync(_db, newId, default))!;
        Assert.Equal(3, latest.FileVersion); Assert.Equal(current.Id, latest.PreviousFileRecordId); Assert.Equal(historical.Id, latest.RootFileRecordId); Assert.Equal(current.OriginalFileId, latest.OriginalFileId);
        foreach (var (id, expected) in new[] { (historical.Id, oldBytes), (current.Id, nextBytes), (latest.Id, replacementBytes) }) { await using var stream = await _graph.OpenFileAsync(id, default); using var bytes = new MemoryStream(); await stream.CopyToAsync(bytes); Assert.Equal(expected, bytes.ToArray()); }
        Assert.All(await _db.ShSealedAttachments.ToListAsync(), link => Assert.Equal(latest.Id, link.FileRecordId));
        Assert.All(await _db.FileAttachments.ToListAsync(), link => Assert.Equal(latest.Id, link.FileRecordId));
        var complete = await _graph.ExportAsync(new() { Type = ExportScopeType.Vault, VaultIds = [vault] }, false, default); Assert.Null(EncryptedGraphProfile.Validate(complete)); Assert.Equal(3, complete.Data.FileRecords.Count);
        var restrictedVault = new Vault { TenantId = _tenant, Name = "Restricted old owner" }; var restrictedOwner = new Knowledge { TenantId = _tenant, Title = "Restricted old owner" };
        _db.Vaults.Add(restrictedVault); _db.KnowledgeItems.Add(restrictedOwner); _db.KnowledgeVaults.Add(new() { TenantId = _tenant, KnowledgeId = restrictedOwner.Id, VaultId = restrictedVault.Id });
        _db.UserPermissions.Add(new() { TenantId = _tenant, UserId = user.Id, HasAllVaultsAccess = false }); _db.UserVaultAccess.Add(new() { TenantId = _tenant, UserId = user.Id, VaultId = vault, CanRead = true, CanWrite = true });
        var hiddenHistoricalLink = new FileAttachment { TenantId = _tenant, FileRecordId = historical.Id, KnowledgeId = restrictedOwner.Id }; _db.FileAttachments.Add(hiddenHistoricalLink); await _db.SaveChangesAsync();
        using var deniedWrite = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/encrypted/files/{latest.Id}") { Content = JsonContent.Create(new { vaultId = vault, record = latest, payloadKeyReferences = complete.PayloadKeyReferences!.Where(r => r.OwnerKind == "file" && r.OwnerId == latest.Id).ToList() }) };
        deniedWrite.Headers.TryAddWithoutValidation("If-Match", SelfHostedSealedStore.PayloadRevision(latest));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await client.SendAsync(deniedWrite)).StatusCode);
        _db.FileAttachments.Remove(hiddenHistoricalLink); await _db.SaveChangesAsync();
        Assert.Equal(409, (await service.DeleteFileAsync(current.Id, expectedRevision, default)).StatusCode);
        var deleted = await service.DeleteFileAsync(latest.Id, SelfHostedSealedStore.PayloadRevision(latest), default); Assert.True(deleted.Ok, deleted.Error);
        Assert.Empty(await _db.FileRecords.ToListAsync());
        Assert.Equal(3, await _db.FileRecords.IgnoreQueryFilters().CountAsync());
        Assert.Equal(3, await _db.ShFileObjectReferences.CountAsync());
        Assert.True(await _storage.ExistsAsync(_tenant, (await _db.ShFileObjectReferences.SingleAsync(p => p.FileRecordId == latest.Id)).BlobStorageId));
        Assert.True(await _storage.ExistsAsync(_tenant, (await _db.ShFileObjectReferences.SingleAsync(p => p.FileRecordId == historical.Id)).BlobStorageId));

    }

    [Fact]
    public async Task SameOriginalGraph_InTwoDestinationVaults_DoesNotClobberOrReuseTheFirstCopy()
    {
        var (package, _, _) = Fixture();
        var vaults = new[] { new Vault { Id = Guid.NewGuid(), TenantId = _tenant, Name = "one" }, new Vault { Id = Guid.NewGuid(), TenantId = _tenant, Name = "two" } };
        _db.Vaults.AddRange(vaults); await _db.SaveChangesAsync();
        foreach (var vault in vaults)
        {
            var result = await _graph.ImportAsync(package, vault.Id, "trusted:source", ImportConflictStrategy.Skip, null, CancellationToken.None);
            Assert.True(result.Success, result.Error);
        }
        Assert.Equal(2, await _db.ShSealedKnowledge.CountAsync());
        foreach (var vault in vaults)
        {
            var graph = await _graph.ExportAsync(new ExportScope { Type = ExportScopeType.Vault, VaultIds = [vault.Id] }, false, CancellationToken.None);
            Assert.Single(graph.SealedItems!);
            Assert.Null(EncryptedGraphProfile.Validate(graph));
        }
    }

    [Fact]
    public async Task ManualGraphSync_RetainsLogicalIdsAndCompleteFilesWithoutDuplicatingRetryHistory()
    {
        var (source, key, plaintext) = Fixture(); var vault = new Vault { Id = Guid.NewGuid(), TenantId = _tenant, Name = "Synced destination" }; _db.Vaults.Add(vault); await _db.SaveChangesAsync();
        var imported = await _graph.ImportSyncAsync(source, vault.Id, ImportConflictStrategy.Skip, CancellationToken.None); Assert.True(imported.Success, imported.Error);
        var restored = await _graph.ExportAsync(new ExportScope { Type = ExportScopeType.Vault, VaultIds = [vault.Id] }, false, CancellationToken.None);
        Assert.Equal(source.SealedItems!.Single().Id, restored.SealedItems!.Single().Id); Assert.Equal(source.SealedFiles!.Single().Id, restored.SealedFiles!.Single().Id);
        Assert.NotEqual(source.CryptoContexts!.Single().ContextId, restored.CryptoContexts!.Single().ContextId);
        Assert.All(restored.SealedItems, item => Assert.Equal(vault.Id, item.PrimaryVaultId)); Assert.Null(EncryptedGraphProfile.Validate(restored));
        using var binary = await _graph.OpenFileAsync(source.SealedFiles.Single().Id, CancellationToken.None); using var stream = new MemoryStream(); await binary.CopyToAsync(stream); var bytes = stream.ToArray(); var recovered = new byte[bytes.Length - 16];
        using (var aes = new AesGcm(key, 16)) aes.Decrypt(Convert.FromBase64String(source.SealedFiles.Single().ContentNonce!), bytes.AsSpan(0, recovered.Length), bytes.AsSpan(recovered.Length), recovered); Assert.Equal(plaintext, recovered);
        Assert.True((await _graph.ImportSyncAsync(source, vault.Id, ImportConflictStrategy.Skip, CancellationToken.None)).Success);
        Assert.Single(await _db.ShSealedKnowledge.ToListAsync()); Assert.Equal(2, await _db.ShSealedAttachments.CountAsync());
    }
    [Fact]
    public async Task PeerGraphRetryAuthorizesRetainedHistoryThroughCurrentOwnerAndRejectsSharedVaults()
    {
        var (source, _, _) = Fixture(); var original = source.SealedFiles!.Single(); original.IsCurrentVersion = false;
        var current = System.Text.Json.JsonSerializer.Deserialize<SealedFileRecord>(System.Text.Json.JsonSerializer.Serialize(original))!;
        current.Id = Guid.NewGuid(); current.FileVersion = 2; current.RootFileRecordId = original.Id; current.PreviousFileRecordId = original.Id; current.IsCurrentVersion = true; source.SealedFiles.Add(current);
        foreach (var link in source.SealedAttachments!) link.FileRecordId = current.Id;
        var reference = System.Text.Json.JsonSerializer.Deserialize<PortablePayloadKeyReference>(System.Text.Json.JsonSerializer.Serialize(source.PayloadKeyReferences!.Single(r => r.OwnerKind == "file")))!; reference.OwnerId = current.Id; source.PayloadKeyReferences.Add(reference);
        var vault = new Vault { TenantId = _tenant, Name = "History peer" }; _db.Vaults.Add(vault); await _db.SaveChangesAsync();
        Assert.True((await _graph.ImportSyncAsync(source, vault.Id, ImportConflictStrategy.Overwrite, default)).Success);
        var retry = await _graph.ImportSyncAsync(source, vault.Id, ImportConflictStrategy.Overwrite, default); Assert.True(retry.Success, retry.Error);
        Assert.Equal(2, await _db.ShSealedFiles.CountAsync());
        var other = new Vault { TenantId = _tenant, Name = "Private other owner" }; _db.Vaults.Add(other);
        _db.KnowledgeVaults.Add(new() { TenantId = _tenant, VaultId = other.Id, KnowledgeId = source.SealedItems!.Single().Id }); await _db.SaveChangesAsync();
        var refused = await _graph.ImportSyncAsync(source, vault.Id, ImportConflictStrategy.Overwrite, default); Assert.False(refused.Success);
        Assert.True(await _db.KnowledgeVaults.AnyAsync(v => v.VaultId == other.Id));
    }

    [Fact]
    public async Task ManualGraphSync_RejectsForeignLogicalIdentityBeforeStaging()
    {
        var (source, _, _) = Fixture(); var vault = new Vault { Id = Guid.NewGuid(), TenantId = _tenant, Name = "Synced destination" }; _db.Vaults.Add(vault);
        _db.KnowledgeItems.Add(new Knowledge { Id = source.SealedItems!.Single().Id, TenantId = Guid.NewGuid(), Title = "Foreign tenant" }); await _db.SaveChangesAsync();
        var result = await _graph.ImportSyncAsync(source, vault.Id, ImportConflictStrategy.Overwrite, CancellationToken.None); Assert.False(result.Success);
        Assert.Empty(await _db.ShSealedFiles.ToListAsync()); Assert.False(Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task InvalidFileGraph_RejectsBeforeDatabaseOrBlobWrites()
    {
        var (package, _, _) = Fixture();
        package.SealedAttachments![0].FileRecordId = Guid.NewGuid();
        var result = await _graph.ImportAsync(package, null, "unverified:isolated", ImportConflictStrategy.Skip, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Empty(await _db.KnowledgeItems.ToListAsync());
        Assert.Empty(await _db.ShPortableCryptoContexts.ToListAsync());
        Assert.False(Directory.Exists(_directory) && Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task ExplicitOriginalWriteKey_EditConcurrency_AndHistoryRestoreRemainRecoverable()
    {
        var (source, _, _) = Fixture();
        Assert.True((await _graph.ImportAsync(source, null, "trusted:lifecycle", ImportConflictStrategy.Skip, null, CancellationToken.None)).Success);
        var graph = await _graph.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None);
        var vault = graph.Data.Vaults.Single().Id;
        var catalog = await _lifecycle.GetCatalogAsync(vault, CancellationToken.None);
        Assert.NotNull(catalog);
        Assert.All(catalog!.Keys, k => Assert.Equal("decrypt-only", k.Lifecycle));
        var context = catalog.Contexts.Single(); var key = catalog.Keys.Single();
        var selected = await _lifecycle.SelectPrimaryAsync(vault, context.ContextId, key.Fingerprint, catalog.Revision, CancellationToken.None);
        Assert.True(selected.Ok, selected.Error);
        var item = graph.SealedItems!.Single();
        var current = await _lifecycle.GetAsync(item.Id, CancellationToken.None);
        var refs = graph.PayloadKeyReferences!.Where(r => r.OwnerKind == "knowledge" && r.OwnerId == item.Id).ToList();
        var write = new SealedItemWrite { Item = item, PayloadKeyReferences = refs };
        var refused = await _lifecycle.UpdateAsync(item.Id, write, CancellationToken.None, "stale-revision");
        Assert.Equal(409, refused.StatusCode);
        Assert.Empty(await _db.ShSealedVersions.ToListAsync());
        item.Ciphertext = Convert.ToBase64String(RandomNumberGenerator.GetBytes(40));
        var updated = await _lifecycle.UpdateAsync(item.Id, write, CancellationToken.None, current!.Revision);
        Assert.True(updated.Ok, updated.Error);
        var history = await _lifecycle.GetVersionsAsync(item.Id, CancellationToken.None);
        var version = Assert.IsType<SealedKnowledgeVersionRecord>(Assert.Single(history));
        Assert.Equal(source.SealedItems![0].Ciphertext, version.Ciphertext);
        var after = await _graph.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None);
        Assert.Null(EncryptedGraphProfile.Validate(after));
        current = await _lifecycle.GetAsync(item.Id, CancellationToken.None);
        var restored = await _lifecycle.RestoreVersionAsync(item.Id, version.Id, current!.Revision, CancellationToken.None);
        Assert.True(restored.Ok, restored.Error);
        Assert.Equal(source.SealedItems[0].Ciphertext, (await _lifecycle.GetAsync(item.Id, CancellationToken.None))!.Envelope.Ciphertext);
        Assert.Equal(2, (await _lifecycle.GetVersionsAsync(item.Id, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task AtomicFileReplacementCreatesRecoverableVersionAndRetainsHistoricalCiphertext()
    {
        var (source, symmetricKey, plaintext) = Fixture();
        Assert.True((await _graph.ImportAsync(source, null, "trusted:history-files", ImportConflictStrategy.Skip, null, default)).Success);
        var restored = await _graph.ExportAsync(new() { Type = ExportScopeType.Tenant }, false, default); var vault = restored.Data.Vaults.Single().Id;
        var file = restored.SealedFiles!.Single(); var originalId = file.Id; var originalBytes = Convert.FromBase64String(source.SealedFiles![0].BinaryContentBase64!);
        file.BinaryEntryPath = null; var revision = SelfHostedSealedStore.PayloadRevision(file);
        var catalog = (await _lifecycle.GetCatalogAsync(vault, default))!; var key = catalog.Keys.Single();
        Assert.True((await _lifecycle.SelectPrimaryAsync(vault, key.ContextId, key.Fingerprint, catalog.Revision, default, key.Generation)).Ok);
        var nonce = RandomNumberGenerator.GetBytes(12); var nextPlaintext = Encoding.UTF8.GetBytes("replacement binary retains history \0");
        var ciphertext = new byte[nextPlaintext.Length]; var tag = new byte[16]; using (var aes = new AesGcm(symmetricKey, 16)) aes.Encrypt(nonce, nextPlaintext, ciphertext, tag);
        var replacement = ciphertext.Concat(tag).ToArray(); file.ContentNonce = Convert.ToBase64String(nonce); file.SizeBytes = replacement.LongLength; file.BinarySha256 = ArchivePeerProof.Fingerprint(replacement);
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant); var service = new SelfHostedSealedFiles(_db, tenant, _lifecycle, _storage);
        var write = new SealedFileWrite { VaultId = vault, Record = file, PayloadKeyReferences = restored.PayloadKeyReferences!.Where(r => r.OwnerKind == "file").ToList() };
        var updated = await service.UpdateAsync(Guid.NewGuid(), write, revision, new MemoryStream(replacement), default);
        Assert.True(updated.Ok, updated.Error); var next = updated.Write!.Record; Assert.NotEqual(originalId, next.Id);
        Assert.Equal(2, next.FileVersion); Assert.Equal(originalId, next.RootFileRecordId); Assert.Equal(originalId, next.PreviousFileRecordId); Assert.Equal(file.OriginalFileId, next.OriginalFileId);
        Assert.All(await _db.ShSealedAttachments.ToListAsync(), a => Assert.Equal(next.Id, a.FileRecordId));
        var exported = await _graph.ExportAsync(new() { Type = ExportScopeType.Vault, VaultIds = [vault] }, false, default);
        Assert.Null(EncryptedGraphProfile.Validate(exported)); Assert.Equal(2, exported.SealedFiles!.Count);
        Assert.False(exported.SealedFiles.Single(f => f.Id == originalId).IsCurrentVersion);
        foreach (var (id, expected) in new[] { (originalId, originalBytes), (next.Id, replacement) })
        { await using var input = await _graph.OpenFileAsync(id, default); using var actual = new MemoryStream(); await input.CopyToAsync(actual); Assert.Equal(expected, actual.ToArray()); }
        var historical = exported.SealedFiles.Single(f => f.Id == originalId);
        var recovered = new byte[originalBytes.Length - 16]; using (var aes = new AesGcm(symmetricKey, 16)) aes.Decrypt(Convert.FromBase64String(historical.ContentNonce!), originalBytes.AsSpan(0, recovered.Length), originalBytes.AsSpan(recovered.Length), recovered);
        Assert.Equal(plaintext, recovered);
        var sourceRefs = restored.PayloadKeyReferences.Where(r => r.OwnerKind == "file").ToList();
        Assert.Equal(sourceRefs.Count * 2, exported.PayloadKeyReferences!.Count(r => r.OwnerKind == "file"));
        Assert.False(await _db.ShSealedFileStages.AnyAsync(s => s.IdempotencyKey.StartsWith("cleanup:")));
        var user = new User { TenantId = _tenant, Username = "history-reader", IsActive = true };
        var otherVault = new Vault { TenantId = _tenant, Name = "Unrelated history" }; _db.Users.Add(user); _db.Vaults.Add(otherVault); await _db.SaveChangesAsync();
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Services.AddSingleton(_db); builder.Services.AddSingleton(_lifecycle); builder.Services.AddSingleton(_graph); builder.Services.AddSingleton(service);
        builder.Services.AddSingleton<Knowz.SelfHosted.Application.Interfaces.IVaultAccessService>(new VaultAccessService(_db));
        await using var app = builder.Build(); app.Use(async (http, continuation) => { http.User = new(new System.Security.Claims.ClaimsIdentity(new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, user.Id.ToString()), new System.Security.Claims.Claim("tenantId", _tenant.ToString()), new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "User") }, "synthetic")); await continuation(); });
        Knowz.SelfHosted.API.Endpoints.EncryptedVaultEndpoints.MapEncryptedVaultEndpoints(app); await app.StartAsync(); using var client = app.GetTestClient();
        Assert.Equal(originalBytes, await client.GetByteArrayAsync($"/api/v1/vaults/{vault}/encryption/files/{originalId}"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/vaults/{otherVault.Id}/encryption/files/{originalId}")).StatusCode);
        var historicalWrite = new SealedFileWrite { VaultId = vault, Record = historical, PayloadKeyReferences = exported.PayloadKeyReferences.Where(r => r.OwnerKind == "file" && r.OwnerId == originalId).ToList() }; historical.BinaryEntryPath = null;
        Assert.Equal(409, (await service.UpdateAsync(user.Id, historicalWrite, SelfHostedSealedStore.PayloadRevision(historical), null, default)).Status);
        next.BinaryEntryPath = null;
        var nextWrite = new SealedFileWrite { VaultId = vault, Record = next, PayloadKeyReferences = updated.Write.PayloadKeyReferences };
        var third = await service.UpdateAsync(user.Id, nextWrite, updated.Revision!, new MemoryStream(replacement), default);
        Assert.True(third.Ok, third.Error); Assert.Equal(3, third.Write!.Record.FileVersion);
        Assert.Equal(originalId, third.Write.Record.RootFileRecordId); Assert.Equal(next.Id, third.Write.Record.PreviousFileRecordId);
        var roundtrip = await _graph.ExportAsync(new() { Type = ExportScopeType.Vault, VaultIds = [vault] }, false, default);
        Assert.Equal(3, roundtrip.SealedFiles!.Count); Assert.Null(EncryptedGraphProfile.Validate(roundtrip));
        foreach (var member in roundtrip.SealedFiles) { using var buffer = new MemoryStream(); await using var input = await _graph.OpenFileAsync(member.Id, default); await input.CopyToAsync(buffer); member.BinaryEntryPath = null; member.BinaryContentBase64 = Convert.ToBase64String(buffer.ToArray()); }
        using var destination = new SelfHostedGraphStoreTests();
        Assert.True((await destination._graph.ImportAsync(roundtrip, null, "trusted:file-history-roundtrip", ImportConflictStrategy.Skip, null, default)).Success);
        var imported = await destination._graph.ExportAsync(new() { Type = ExportScopeType.Tenant }, false, default); Assert.Null(EncryptedGraphProfile.Validate(imported));
        Assert.Equal(3, imported.SealedFiles!.Count); Assert.Equal(1, imported.SealedFiles.Count(f => f.IsCurrentVersion));
        var itemId = restored.SealedItems!.Single().Id;
        var payload = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v1/vaults/{vault}/encryption/payloads/knowledge/{itemId}");
        Assert.Contains(originalId.ToString(), payload.GetProperty("related").GetProperty("files").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(409, (await service.DeleteFileAsync(originalId, SelfHostedSealedStore.PayloadRevision(historical), default)).StatusCode);
        Assert.True((await service.DeleteFileAsync(third.Write.Record.Id, third.Revision!, default)).Ok);
        Assert.Empty(await _db.ShSealedFiles.ToListAsync()); Assert.Empty(await _db.ShSealedAttachments.ToListAsync());
        Assert.False(await _db.ShPayloadKeyReferences.AnyAsync(r => r.OwnerKind == "file"));
    }

    [Fact]
    public async Task VaultOwnedEncryptedOrphanPreservesOwningVaultAndAuthorizesExactLiveVault()
    {
        var (source, _, _) = Fixture(); var file = source.SealedFiles!.Single(); file.KnowledgeId = null; file.VaultId = source.Data.Vaults.Single().Id; source.SealedAttachments = [];
        var bytes = Convert.FromBase64String(file.BinaryContentBase64!);
        Assert.True((await _graph.ImportAsync(source, null, "trusted:orphan", ImportConflictStrategy.Skip, null, default)).Success);
        var vault = (await _db.Vaults.SingleAsync()).Id;
        var restored = await _graph.ExportAsync(new() { Type = ExportScopeType.Vault, VaultIds = [vault] }, false, default);
        var importedFile = Assert.Single(restored.SealedFiles!); Assert.Equal(vault, importedFile.VaultId); Assert.Equal(file.OriginalFileId, importedFile.OriginalFileId);
        var unrelated = new Vault { TenantId = _tenant, Name = "Unrelated" }; var user = new User { TenantId = _tenant, Username = "orphan-reader", IsActive = true };
        _db.Vaults.Add(unrelated); _db.Users.Add(user); await _db.SaveChangesAsync();
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Services.AddSingleton(_db); builder.Services.AddSingleton(_lifecycle); builder.Services.AddSingleton(_graph);
        builder.Services.AddSingleton(new SelfHostedSealedFiles(_db, tenant, _lifecycle, _storage));
        builder.Services.AddSingleton<Knowz.SelfHosted.Application.Interfaces.IVaultAccessService>(new VaultAccessService(_db));
        await using var app = builder.Build(); app.Use(async (http, next) => { http.User = new(new System.Security.Claims.ClaimsIdentity(new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, user.Id.ToString()), new System.Security.Claims.Claim("tenantId", _tenant.ToString()), new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "User") }, "synthetic")); await next(); });
        Knowz.SelfHosted.API.Endpoints.EncryptedVaultEndpoints.MapEncryptedVaultEndpoints(app); await app.StartAsync(); using var client = app.GetTestClient();
        Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/v1/vaults/{vault}/encryption/files/{importedFile.Id}"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/vaults/{unrelated.Id}/encryption/files/{importedFile.Id}")).StatusCode);
    }

    [Fact]
    public async Task HttpCatalogPayloadAndFile_AuthorizeLiveDestinationAndRevokedUser()
    {
        var (source, _, _) = Fixture();
        Assert.True((await _graph.ImportAsync(source, null, "trusted:http-fixture", ImportConflictStrategy.Skip, null, CancellationToken.None)).Success);
        var restored = await _graph.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None);
        var user = new User { Id = Guid.NewGuid(), TenantId = _tenant, Username = "local-reader", IsActive = true };
        _db.Users.Add(user); await _db.SaveChangesAsync();
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
        builder.Services.AddSingleton(_db); builder.Services.AddSingleton(_lifecycle); builder.Services.AddSingleton(_graph);
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        builder.Services.AddSingleton(new SelfHostedSealedFiles(_db, tenant, _lifecycle, _storage));
        builder.Services.AddSingleton(new ArchiveImportCoordinator());
        builder.Services.AddSingleton(new SelfHostedArchiveImportAdapter(_db, tenant, _graph, new VaultAccessService(_db)));
        builder.Services.AddSingleton(new SelfHostedArchiveExportService(_db, tenant, _graph, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()));
        builder.Services.AddSingleton<Knowz.SelfHosted.Application.Interfaces.IVaultAccessService>(new VaultAccessService(_db));
        await using var app = builder.Build();
        app.Use(async (context, next) => {
            context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
                new(System.Security.Claims.ClaimTypes.NameIdentifier, user.Id.ToString()), new("tenantId", _tenant.ToString()),
                new(System.Security.Claims.ClaimTypes.Role, "Admin")], "fixture"));
            await next();
        });
        Knowz.SelfHosted.API.Endpoints.EncryptedVaultEndpoints.MapEncryptedVaultEndpoints(app);
        Knowz.SelfHosted.API.Endpoints.ArchivePortabilityEndpoints.MapArchivePortabilityEndpoints(app);
        await app.StartAsync();
        using var client = app.GetTestClient();
        var vault = restored.Data.Vaults.Single().Id;
        var anchorPlan = Guid.NewGuid();
        _db.ShCryptoContextImportAnchors.Add(new() { TenantId = _tenant, ContextId = restored.CryptoContexts![0].ContextId, PlanId = anchorPlan,
            ArchiveId = Guid.NewGuid(), OriginalBindingId = restored.CryptoContexts[0].OriginalBindingId, ManifestSha256 = new string('a', 64), MappingSha256 = new string('b', 64), PackageSha256 = new string('c', 64), PlanSha256 = new string('d', 64), SourceTrust = "unverified" });
        await _db.SaveChangesAsync();
        var pendingCatalog = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v1/vaults/{vault}/encryption/contexts");
        Assert.Empty(pendingCatalog.GetProperty("importAnchors").EnumerateArray());
        var anchor = await _db.ShCryptoContextImportAnchors.SingleAsync();
        _db.ShArchiveImportJournals.Add(new() { TenantId = _tenant, PlanId = anchorPlan, ActorId = user.Id, ActorNamespace = user.Id.ToString(), ArchiveId = anchor.ArchiveId, ManifestSha256 = anchor.ManifestSha256, MappingSha256 = anchor.MappingSha256, Status = "committed", CommitId = Guid.NewGuid() }); await _db.SaveChangesAsync();
        var catalog = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v1/vaults/{vault}/encryption/contexts");
        Assert.Equal(restored.CryptoKeyCatalog![0].Fingerprint, catalog.GetProperty("cryptoKeyCatalog")[0].GetProperty("fingerprint").GetString());
        Assert.Empty(catalog.GetProperty("acceptedCustodyKeys").EnumerateArray());
        Assert.Equal(anchorPlan, catalog.GetProperty("importAnchors")[0].GetProperty("planId").GetGuid());
        Assert.Equal("unverified", catalog.GetProperty("importAnchors")[0].GetProperty("sourceTrust").GetString());
        var original = await client.GetAsync($"/api/v1/vaults/{vault}/encryption/payloads/knowledge/{source.SealedItems![0].Id}");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, original.StatusCode);
        var payload = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v1/vaults/{vault}/encryption/payloads/knowledge/{restored.SealedItems![0].Id}");
        Assert.Equal(source.SealedItems[0].Ciphertext, payload.GetProperty("record").GetProperty("ciphertext").GetString());
        var binary = await client.GetByteArrayAsync($"/api/v1/vaults/{vault}/encryption/files/{restored.SealedFiles![0].Id}");
        Assert.Equal(Convert.FromBase64String(source.SealedFiles![0].BinaryContentBase64!), binary);
        var selectedKey = await client.PutAsJsonAsync($"/api/v1/vaults/{vault}/encryption/primary-context", new { contextId = restored.CryptoContexts![0].ContextId, fingerprint = restored.CryptoKeyCatalog![0].Fingerprint, expectedRevision = catalog.GetProperty("revision").GetString() }); selectedKey.EnsureSuccessStatusCode();
        catalog = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v1/vaults/{vault}/encryption/contexts");
        using var itemEdit = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/encrypted/items/{restored.SealedItems[0].Id}") { Content = JsonContent.Create(new { vaultId = vault, record = payload.GetProperty("record"), payloadKeyReferences = payload.GetProperty("payloadKeyReferences") }) };
        itemEdit.Headers.TryAddWithoutValidation("If-Match", payload.GetProperty("revision").GetString());
        using var itemEdited = await client.SendAsync(itemEdit); itemEdited.EnsureSuccessStatusCode();
        var itemResult = await itemEdited.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(restored.SealedItems[0].Id.ToString(), itemResult.GetProperty("ownerId").GetString());
        Assert.Equal("knowledge", itemResult.GetProperty("ownerKind").GetString());
        Assert.Equal(source.SealedItems[0].Ciphertext, itemResult.GetProperty("record").GetProperty("ciphertext").GetString());
        Assert.True(itemResult.TryGetProperty("revision", out _));
        var stagedFile = restored.SealedFiles[0]; stagedFile.Id = Guid.NewGuid(); stagedFile.OriginalFileId = stagedFile.Id; stagedFile.KnowledgeId = restored.SealedItems[0].Id; stagedFile.BinaryEntryPath = null;
        var fileRefs = restored.PayloadKeyReferences!.Where(r => r.OwnerKind == "file").ToList(); foreach (var reference in fileRefs) { reference.OwnerId = stagedFile.Id; reference.OriginalItemId = stagedFile.Id; }
        using var stageRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/encrypted/files") { Content = JsonContent.Create(new { vaultId = vault, record = stagedFile, payloadKeyReferences = fileRefs }) };
        stageRequest.Headers.TryAddWithoutValidation("If-Match", catalog.GetProperty("revision").GetString()); stageRequest.Headers.Add("Idempotency-Key", "http-native-file");
        using var staged = await client.SendAsync(stageRequest); staged.EnsureSuccessStatusCode();
        var stagedPayload = await staged.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/vaults/{vault}/encryption/files/{stagedFile.Id}")).StatusCode);
        using var upload = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/encrypted/files/{stagedFile.Id}/ciphertext") { Content = new ByteArrayContent(binary) };
        upload.Headers.TryAddWithoutValidation("If-Match", stagedPayload.GetProperty("revision").GetString()); upload.Headers.Add("X-Knowz-Content-SHA256", stagedFile.BinarySha256);
        using var uploaded = await client.SendAsync(upload); uploaded.EnsureSuccessStatusCode();
        Assert.Equal(binary, await client.GetByteArrayAsync($"/api/v1/vaults/{vault}/encryption/files/{stagedFile.Id}"));
        var filePayload = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v1/vaults/{vault}/encryption/payloads/file/{stagedFile.Id}");
        using var form = new MultipartFormDataContent(); form.Add(new StringContent(vault.ToString()), "vaultId");
        form.Add(new StringContent(filePayload.GetProperty("record").GetRawText()), "record"); form.Add(new StringContent(filePayload.GetProperty("payloadKeyReferences").GetRawText()), "payloadKeyReferences");
        form.Add(new ByteArrayContent(binary), "binary", "ciphertext.bin");
        using var replace = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/encrypted/files/{stagedFile.Id}") { Content = form }; replace.Headers.TryAddWithoutValidation("If-Match", filePayload.GetProperty("revision").GetString());
        using var replaced = await client.SendAsync(replace); replaced.EnsureSuccessStatusCode();
        var replacementResult = await replaced.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        stagedFile.Id = replacementResult.GetProperty("ownerId").GetGuid();
        var attachment = restored.SealedAttachments!.Single(a => a.KnowledgeId.HasValue);
        var attachmentPayload = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v1/vaults/{vault}/encryption/payloads/attachment/{attachment.Id}");
        using var editAttachment = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/encrypted/attachments/{attachment.Id}") { Content = JsonContent.Create(new { vaultId = vault, record = attachment, payloadKeyReferences = Array.Empty<object>() }) };
        editAttachment.Headers.TryAddWithoutValidation("If-Match", attachmentPayload.GetProperty("revision").GetString());
        using var editedAttachment = await client.SendAsync(editAttachment); editedAttachment.EnsureSuccessStatusCode();
        attachmentPayload = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v1/vaults/{vault}/encryption/payloads/attachment/{attachment.Id}");
        using var deleteAttachment = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/encrypted/attachments/{attachment.Id}"); deleteAttachment.Headers.TryAddWithoutValidation("If-Match", attachmentPayload.GetProperty("revision").GetString());
        using var deletedAttachment = await client.SendAsync(deleteAttachment); deletedAttachment.EnsureSuccessStatusCode();
        filePayload = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v1/vaults/{vault}/encryption/payloads/file/{stagedFile.Id}");
        using var deleteFile = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/encrypted/files/{stagedFile.Id}"); deleteFile.Headers.TryAddWithoutValidation("If-Match", filePayload.GetProperty("revision").GetString());
        using var deletedFile = await client.SendAsync(deleteFile); deletedFile.EnsureSuccessStatusCode();
        var identity = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/portability/archives/identity");
        var response = await client.PostAsJsonAsync("/api/v1/portability/archives/export", new { scopeType = "Vault", vaultIds = new[] { vault } });
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.Contains("X-Knowz-Source-Receipt"));
        using var archiveStream = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var archive = await KnowzArchiveReader.OpenAsync(archiveStream, new() { ExpectedSignerFingerprint = identity.GetProperty("signerFingerprint").GetString(), LeaveOpen = true });
        Assert.Single(archive.Packages.Single().SealedItems!);
        using var planRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/portability/imports/plan?targetVaultId={vault}&expectedSignerFingerprint={identity.GetProperty("signerFingerprint").GetString()}") { Content = new ByteArrayContent(await response.Content.ReadAsByteArrayAsync()) };
        planRequest.Headers.Add("Idempotency-Key", "http-archive-copy");
        using var planResponse = await client.SendAsync(planRequest); planResponse.EnsureSuccessStatusCode();
        var planned = await planResponse.Content.ReadFromJsonAsync<ArchiveImportPlan>();
        using var committedResponse = await client.PostAsync($"/api/v1/portability/imports/{planned!.PlanId}/commit", null); committedResponse.EnsureSuccessStatusCode();
        var committed = await committedResponse.Content.ReadFromJsonAsync<ArchiveCommitReceipt>();
        Assert.Equal(planned.ManifestSha256, committed!.ManifestSha256);
        Assert.Equal(planned.RequiredPayloadCount, committed.PreservedPayloadCount);
        (await _db.Users.SingleAsync(x => x.Id == user.Id)).IsActive = false; await _db.SaveChangesAsync();
        var revokedUpdate = await client.PutAsJsonAsync($"/api/v1/encrypted/items/{restored.SealedItems[0].Id}", new { record = restored.SealedItems[0], payloadKeyReferences = restored.PayloadKeyReferences });
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, revokedUpdate.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/encrypted/items/{restored.SealedItems[0].Id}/versions")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/portability/archives/identity")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/vaults/{vault}/encryption/contexts")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/vaults/{vault}/encryption/files/{restored.SealedFiles[0].Id}")).StatusCode);
    }

    [Fact]
    public async Task ExplicitVaultMappings_PreserveDestinationMetadataAndAllMappedMemberships()
    {
        var (source, _, _) = Fixture();
        var destination = new Vault { Id = Guid.NewGuid(), TenantId = _tenant, Name = "Keep this destination name" };
        _db.Vaults.Add(destination); await _db.SaveChangesAsync();
        var mapping = new Dictionary<Guid, Guid> { [source.Data.Vaults.Single().Id] = destination.Id };
        var result = await _graph.ImportAsync(source, null, "trusted:mapping", ImportConflictStrategy.Overwrite, null, CancellationToken.None, mapping);
        Assert.True(result.Success, result.Error);
        Assert.Equal("Keep this destination name", (await _db.Vaults.SingleAsync()).Name);
        Assert.Equal(2, await _db.KnowledgeVaults.CountAsync(x => x.VaultId == destination.Id));
        Assert.Single((await _graph.ExportAsync(new ExportScope { Type = ExportScopeType.Vault, VaultIds = [destination.Id] }, false, CancellationToken.None)).SealedItems!);
    }

    [Fact]
    public async Task CancelledUpload_RemovesStageAndPropagatesCancellation()
    {
        var (source, _, _) = Fixture();
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var storage = Substitute.For<Knowz.SelfHosted.Infrastructure.Interfaces.IFileStorageProvider>();
        storage.UploadAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new OperationCanceledException());
        var graph = new SelfHostedGraphStore(_db, tenant,
            new PortableImportService(_db, tenant, storage, NullLogger<PortableImportService>.Instance),
            new PortableExportService(_db, tenant, storage, Options.Create(new SelfHostedOptions()), NullLogger<PortableExportService>.Instance), storage);
        await Assert.ThrowsAsync<OperationCanceledException>(() => graph.ImportAsync(source, null, "trusted:cancellation", ImportConflictStrategy.Skip, null, CancellationToken.None));
        await storage.Received(1).DeleteAsync(_tenant, Arg.Any<Guid>(), CancellationToken.None);
        Assert.Empty(await _db.KnowledgeItems.ToListAsync());
    }

    [Fact]
    public async Task SignedArchive_StreamsPlainAndEncryptedFiles_AndRestoresBothAfterMetadataOnlyTransport()
    {
        var (source, _, _) = Fixture();
        var plaintext = Encoding.UTF8.GetBytes("plain attachment bytes, exact after archive restore");
        source.Data.FileRecords.Add(new() { Id = Guid.NewGuid(), FileName = "plain.txt", SizeBytes = plaintext.Length, BinaryContentBase64 = Convert.ToBase64String(plaintext), Attachments = [new() { KnowledgeId = source.Data.KnowledgeItems.Single().Id }] });
        using var signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var archiveBytes = new MemoryStream();
        var receipt = await KnowzArchiveWriter.WriteAsync(archiveBytes, [source], null, new() { SigningKey = signing, SourceSnapshot = "source-fixture-revision" });
        archiveBytes.Position = 0;
        using var archive = await KnowzArchiveReader.OpenAsync(archiveBytes, new() { ExpectedSignerFingerprint = receipt.SignerFingerprint, LeaveOpen = true });
        var result = await _graph.ImportAsync(archive.Packages.Single(), null, "trusted:" + receipt.SignerFingerprint, ImportConflictStrategy.Skip, archive.OpenBinaryAsync, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        var exported = await _graph.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None);
        var plain = Assert.Single(exported.Data.FileRecords);
        await using var bytes = await _graph.OpenFileAsync(plain.Id, CancellationToken.None);
        using var restored = new MemoryStream(); await bytes.CopyToAsync(restored);
        Assert.Equal(plaintext, restored.ToArray());
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var ordinaryFiles = new FileStorageService(_storage, new SelfHostedRepository<FileRecord>(_db), _db, tenant, NullLogger<FileStorageService>.Instance);
        var ordinary = await ordinaryFiles.DownloadAsync(plain.Id);
        Assert.NotNull(ordinary);
        using var ordinaryBytes = new MemoryStream(); await ordinary.Value.stream.CopyToAsync(ordinaryBytes); await ordinary.Value.stream.DisposeAsync();
        Assert.Equal(plaintext, ordinaryBytes.ToArray());
        using var secondArchive = new MemoryStream();
        await KnowzArchiveWriter.WriteAsync(secondArchive, [exported], _graph.OpenFileAsync, new() { SigningKey = signing, SourceSnapshot = "destination-fixture-revision" });
        secondArchive.Position = 0;
        using var reloaded = await KnowzArchiveReader.OpenAsync(secondArchive, new() { ExpectedSignerFingerprint = receipt.SignerFingerprint, LeaveOpen = true });
        Assert.Equal(2, reloaded.Manifest.Inventory.PayloadCounts["files"]);
        Assert.Equal(receipt.PayloadCount, reloaded.Manifest.Inventory.PayloadCounts["required"]);
    }

    [Fact]
    public async Task ArchiveSourceIdentity_PersistsProtectedKey_AndProducesPinnedVerifiableStream()
    {
        var (source, _, _) = Fixture();
        Assert.True((await _graph.ImportAsync(source, null, "trusted:signer", ImportConflictStrategy.Skip, null, CancellationToken.None)).Success);
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var protection = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
        var service = new SelfHostedArchiveExportService(_db, tenant, _graph, protection);
        var identity = await service.IdentityAsync(CancellationToken.None);
        var reloaded = new SelfHostedArchiveExportService(_db, tenant, _graph, protection);
        Assert.Equal(identity, await reloaded.IdentityAsync(CancellationToken.None));
        var row = await _db.ShArchiveSigningIdentities.SingleAsync();
        Assert.DoesNotContain("PRIVATE KEY", row.ProtectedPrivateKey);
        var result = await reloaded.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None);
        using var stream = result.Stream;
        using var archive = await KnowzArchiveReader.OpenAsync(stream, new() { ExpectedSignerFingerprint = identity.SignerFingerprint, LeaveOpen = true });
        Assert.Equal(result.Receipt.ManifestSha256, archive.ManifestSha256);
        Assert.Equal("trusted", archive.SourceTrust);
        Assert.Single(archive.Packages.Single().SealedFiles!);
    }

    [Fact]
    public async Task PublicSdkOriginalKeyFixture_RetainsEveryPayloadBindingAndProducesDotNetArchive()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var source = System.Text.Json.JsonSerializer.Deserialize<PortableExportPackage>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portable-graph-v1.json")), options)!;
        Assert.Null(EncryptedGraphProfile.Validate(source));
        var result = await _graph.ImportAsync(source, null, "trusted:public-sdk-fixture", ImportConflictStrategy.Skip, null, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        var restored = await _graph.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None);
        Assert.Equal(8, restored.PayloadKeyReferences!.Count);
        Assert.Single(restored.SealedVersions!);
        Assert.Single(restored.SealedComments!);
        Assert.Equal(source.SealedComments![0].EncryptedFieldsJson, restored.SealedComments![0].EncryptedFieldsJson);
        Assert.Equal(source.SealedVersions![0].EnvelopeMetadata, restored.SealedVersions![0].EnvelopeMetadata);
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var service = new SelfHostedArchiveExportService(_db, tenant, _graph, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        var archive = await service.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None);
        using var stream = archive.Stream;
        // Optional acceptance lane leaves only synthetic data for the published Node SDK to verify.
        if (Environment.GetEnvironmentVariable("KNOWZ_PORTABILITY_FIXTURE_OUTPUT") is string output)
        {
            Directory.CreateDirectory(output);
            await using var target = File.Create(Path.Combine(output, "selfhosted-restored.knowzarchive"));
            await stream.CopyToAsync(target);
            await File.WriteAllTextAsync(Path.Combine(output, "selfhosted-receipt.json"), System.Text.Json.JsonSerializer.Serialize(archive.Receipt, options));
            await File.WriteAllTextAsync(Path.Combine(output, "selfhosted-context.json"), System.Text.Json.JsonSerializer.Serialize(restored.CryptoKeyCatalog, options));
        }
    }

    [Fact]
    public async Task ReusedPublicKeyAcrossGenerations_PreservesExactGenerationReference()
    {
        var (source, _, _) = Fixture();
        var original = source.CryptoKeyCatalog![0]; original.Lifecycle = "retired";
        source.CryptoKeyCatalog.Add(new() { ContextId = original.ContextId, PublicKey = original.PublicKey, Fingerprint = original.Fingerprint, Generation = 2, Lifecycle = "active" });
        foreach (var reference in source.PayloadKeyReferences!) reference.Generation = 2;
        Assert.Null(EncryptedGraphProfile.Validate(source));
        Assert.True((await _graph.ImportAsync(source, null, "trusted:generations", ImportConflictStrategy.Skip, null, CancellationToken.None)).Success);
        var restored = await _graph.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None);
        Assert.Equal(2, restored.CryptoKeyCatalog!.Count);
        Assert.Equal(2, restored.PayloadKeyReferences!.Single(r => r.OwnerKind == "knowledge").Generation);
        var vault = restored.Data.Vaults.Single().Id;
        var catalog = (await _lifecycle.GetCatalogAsync(vault, CancellationToken.None))!;
        var ambiguous = await _lifecycle.SelectPrimaryAsync(vault, catalog.Contexts.Single().ContextId, original.Fingerprint, catalog.Revision, CancellationToken.None);
        Assert.False(ambiguous.Ok);
        var selected = await _lifecycle.SelectPrimaryAsync(vault, catalog.Contexts.Single().ContextId, original.Fingerprint, catalog.Revision, CancellationToken.None, 2);
        Assert.True(selected.Ok);
        Assert.Equal(2, (await _db.ShVaultCryptoContextLinks.SingleAsync()).PrimaryWriteGeneration);
        Assert.True((await _graph.ImportAsync(source, null, "trusted:generations", ImportConflictStrategy.Skip, null, CancellationToken.None)).Success);
        Assert.True((await _db.ShVaultCryptoContextLinks.SingleAsync()).IsPrimaryWriteContext);

    }

    [Fact]
    public async Task ArchivePlan_StagesWithoutLiveRows_CancelCleans_CommitRetriesExactlyOnce()
    {
        var (source, _, _) = Fixture();
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var input = new MemoryStream();
        var sourceReceipt = await KnowzArchiveWriter.WriteAsync(input, [source], null, new() { SigningKey = signer, SourceSnapshot = "staging-source-revision" });
        var actor = new User { Id = Guid.NewGuid(), TenantId = _tenant, Username = "import-admin", Role = Knowz.Core.Enums.UserRole.Admin, IsActive = true };
        _db.Users.Add(actor); await _db.SaveChangesAsync();
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var adapter = new SelfHostedArchiveImportAdapter(_db, tenant, _graph, new VaultAccessService(_db));
        using var coordinator = new ArchiveImportCoordinator();
        var identity = new ArchiveActor(_tenant, actor.Id.ToString());
        input.Position = 0;
        var plan = await coordinator.CreatePlanAsync(input, identity, new() { ExpectedSignerFingerprint = sourceReceipt.SignerFingerprint, IdempotencyKey = "cancel-first" }, adapter);
        Assert.Empty(await _db.KnowledgeItems.ToListAsync());
        Assert.Equal("prepared", (await _db.ShArchiveImportJournals.SingleAsync()).Status);
        Assert.True(Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories).Any());
        await coordinator.CancelAsync(plan.PlanId, identity, adapter);
        Assert.False(Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories).Any());
        input.Position = 0;
        plan = await coordinator.CreatePlanAsync(input, identity, new() { ExpectedSignerFingerprint = sourceReceipt.SignerFingerprint, IdempotencyKey = "commit-next" }, adapter);
        var receipt = await coordinator.CommitAsync(plan.PlanId, identity, adapter);
        var replay = await coordinator.CommitAsync(plan.PlanId, identity, adapter);
        Assert.Equal(receipt.CommitId, replay.CommitId);
        Assert.Equal(plan.RequiredPayloadCount, receipt.PreservedPayloadCount);
        Assert.Equal("not-performed", receipt.ClientVerificationState);
        Assert.Single(await _db.ShSealedKnowledge.ToListAsync());
        Assert.Equal("committed", (await _db.ShArchiveImportJournals.SingleAsync(j => j.PlanId == plan.PlanId)).Status);
    }

    [Fact]
    public async Task ExpiryWorker_CleansWithFreshOwnerScopeAndRestoresForeignAmbientTenant()
    {
        var (source, _, _) = Fixture(); using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var input = new MemoryStream();
        var signed = await KnowzArchiveWriter.WriteAsync(input, [source], null, new() { SigningKey = signer, SourceSnapshot = "expiry-source" }); input.Position = 0;
        var actor = new User { Id = Guid.NewGuid(), TenantId = _tenant, Username = "expiry-admin", Role = Knowz.Core.Enums.UserRole.Admin, IsActive = true }; _db.Users.Add(actor); await _db.SaveChangesAsync();
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var adapter = new SelfHostedArchiveImportAdapter(_db, tenant, _graph, new VaultAccessService(_db));
        var clock = new ExpiryClock(); using var coordinator = new ArchiveImportCoordinator(clock, TimeSpan.FromMinutes(1), 1);
        var identity = new ArchiveActor(_tenant, actor.Id.ToString());
        var plan = await coordinator.CreatePlanAsync(input, identity, new() { ExpectedSignerFingerprint = signed.SignerFingerprint, IdempotencyKey = "expired" }, adapter);
        var options = (DbContextOptions<SelfHostedDbContext>)_db.GetService<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptions>();
        var services = new ServiceCollection(); services.AddSingleton(coordinator); services.AddSingleton<Knowz.SelfHosted.Infrastructure.Interfaces.IFileStorageProvider>(_storage);
        services.AddScoped<ITenantProvider>(_ => new AmbientTestTenant());
        services.AddScoped(sp => new SelfHostedDbContext(options, sp.GetRequiredService<ITenantProvider>()));
        services.AddScoped(sp => new SelfHostedGraphStore(sp.GetRequiredService<SelfHostedDbContext>(), sp.GetRequiredService<ITenantProvider>(),
            new PortableImportService(sp.GetRequiredService<SelfHostedDbContext>(), sp.GetRequiredService<ITenantProvider>(), _storage, NullLogger<PortableImportService>.Instance),
            new PortableExportService(sp.GetRequiredService<SelfHostedDbContext>(), sp.GetRequiredService<ITenantProvider>(), _storage, Options.Create(new SelfHostedOptions()), NullLogger<PortableExportService>.Instance), _storage));
        services.AddScoped(sp => new SelfHostedArchiveImportAdapter(sp.GetRequiredService<SelfHostedDbContext>(), sp.GetRequiredService<ITenantProvider>(), sp.GetRequiredService<SelfHostedGraphStore>(), new VaultAccessService(sp.GetRequiredService<SelfHostedDbContext>())));
        services.AddScoped(sp => new SelfHostedPortabilityStageReconciler(sp.GetRequiredService<SelfHostedDbContext>(), _storage, NullLogger<SelfHostedPortabilityStageReconciler>.Instance));
        await using var provider = services.BuildServiceProvider();
        using var worker = new SelfHostedPortabilityRecoveryWorker(provider.GetRequiredService<IServiceScopeFactory>(), coordinator, NullLogger<SelfHostedPortabilityRecoveryWorker>.Instance);
        var foreign = Guid.NewGuid(); TenantContext.CurrentTenantId = foreign;
        try
        {
            clock.UtcNow += TimeSpan.FromMinutes(2); await worker.RunOnceAsync(CancellationToken.None);
            Assert.Equal(foreign, TenantContext.CurrentTenantId);
            Assert.False(Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories).Any());
            _db.ChangeTracker.Clear(); Assert.Equal("cancelled", (await _db.ShArchiveImportJournals.SingleAsync()).Status);
            var expired = await Assert.ThrowsAsync<ArchiveOperationException>(() => coordinator.CommitAsync(plan.PlanId, identity, adapter)); Assert.Equal(404, expired.StatusCode);
            input.Position = 0;
            var replacement = await coordinator.CreatePlanAsync(input, identity, new() { ExpectedSignerFingerprint = signed.SignerFingerprint, IdempotencyKey = "fresh-capacity" }, adapter);
            Assert.NotEqual(plan.PlanId, replacement.PlanId); await coordinator.CancelAsync(replacement.PlanId, identity, adapter);
        }
        finally { TenantContext.CurrentTenantId = null; }
    }
    private sealed class ExpiryClock : TimeProvider { public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => UtcNow; }
    private sealed class AmbientTestTenant : ITenantProvider { public Guid TenantId => TenantContext.CurrentTenantId ?? throw new InvalidOperationException("An explicit owner tenant is required."); }

    [Fact]
    public async Task MissingPreparedObject_CancelsCommitAndNeverPublishesDanglingFileOnRetry()
    {
        var (source, _, _) = Fixture(); using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var input = new MemoryStream();
        var signed = await KnowzArchiveWriter.WriteAsync(input, [source], null, new() { SigningKey = signer, SourceSnapshot = "lost-stage" }); input.Position = 0;
        var actor = new User { Id = Guid.NewGuid(), TenantId = _tenant, Username = "missing-stage-admin", Role = Knowz.Core.Enums.UserRole.Admin, IsActive = true }; _db.Users.Add(actor); await _db.SaveChangesAsync();
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var adapter = new SelfHostedArchiveImportAdapter(_db, tenant, _graph, new VaultAccessService(_db)); using var coordinator = new ArchiveImportCoordinator(); var identity = new ArchiveActor(_tenant, actor.Id.ToString());
        var plan = await coordinator.CreatePlanAsync(input, identity, new() { ExpectedSignerFingerprint = signed.SignerFingerprint, IdempotencyKey = "lost-stage" }, adapter);
        var journal = await _db.ShArchiveImportJournals.SingleAsync();
        var objectId = System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(journal.StagedObjectIdsJson)!.Single();
        await _storage.DeleteAsync(_tenant, objectId, CancellationToken.None);
        await Assert.ThrowsAsync<ArchiveOperationException>(() => coordinator.CommitAsync(plan.PlanId, identity, adapter));
        Assert.Empty(await _db.KnowledgeItems.ToListAsync()); Assert.Empty(await _db.ShSealedFiles.ToListAsync());
        await Assert.ThrowsAsync<ArchiveOperationException>(() => coordinator.CommitAsync(plan.PlanId, identity, adapter));
        Assert.Equal("cancelled", (await _db.ShArchiveImportJournals.SingleAsync()).Status);
    }

    [Fact]
    public async Task ChangedSourceSkip_PreviewsPartialAndCannotClaimCompleteRecovery()
    {
        var (source, _, _) = Fixture();
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var actor = new User { Id = Guid.NewGuid(), TenantId = _tenant, Username = "skip-admin", Role = Knowz.Core.Enums.UserRole.Admin, IsActive = true };
        _db.Users.Add(actor); await _db.SaveChangesAsync();
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var adapter = new SelfHostedArchiveImportAdapter(_db, tenant, _graph, new VaultAccessService(_db));
        using var coordinator = new ArchiveImportCoordinator();
        var identity = new ArchiveActor(_tenant, actor.Id.ToString());
        async Task<ArchiveImportPlan> Plan(string key)
        {
            using var input = new MemoryStream();
            var signed = await KnowzArchiveWriter.WriteAsync(input, [source], null, new() { SigningKey = signer, SourceSnapshot = key }); input.Position = 0;
            return await coordinator.CreatePlanAsync(input, identity, new() { ExpectedSignerFingerprint = signed.SignerFingerprint, IdempotencyKey = key }, adapter);
        }
        var original = await Plan("original"); await coordinator.CommitAsync(original.PlanId, identity, adapter);
        source.SealedItems![0].Ciphertext = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var changed = await Plan("changed");
        Assert.Equal("partial", changed.PreservationState);
        Assert.Contains(changed.Omissions, omission => omission.OwnerKind == "knowledge");
        var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => coordinator.CommitAsync(changed.PlanId, identity, adapter));
        Assert.Equal(409, error.StatusCode);
        Assert.NotEqual(source.SealedItems[0].Ciphertext, (await _db.ShSealedKnowledge.SingleAsync()).Ciphertext);
    }

    [Fact]
    public async Task ProtectedPlainFileMetadataConflict_IsPartialBeforeCommit()
    {
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web); json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var source = System.Text.Json.JsonSerializer.Deserialize<PortableExportPackage>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portable-graph-v1.json")), json)!;
        var file = source.SealedFiles!.Single();
        source.Data.FileRecords.Add(new() { Id = file.Id, OriginalFileId = file.OriginalFileId, FileName = "encrypted-file", ContentType = "application/octet-stream", SizeBytes = 3, BinaryContentBase64 = "AQID", EncryptedFieldsJson = file.EncryptedFieldsJson });
        source.SealedFiles.Clear(); source.PayloadKeyReferences!.RemoveAll(r => r.OwnerKind == "file" && r.Role == "binary");
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var user = new User { TenantId = _tenant, Username = "protected-file-plan", Role = Knowz.Core.Enums.UserRole.Admin, IsActive = true }; _db.Users.Add(user); await _db.SaveChangesAsync();
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant); var adapter = new SelfHostedArchiveImportAdapter(_db, tenant, _graph, new VaultAccessService(_db));
        using var coordinator = new ArchiveImportCoordinator(); var actor = new ArchiveActor(_tenant, user.Id.ToString());
        async Task<ArchiveImportPlan> Plan(string key)
        {
            using var input = new MemoryStream(); var receipt = await KnowzArchiveWriter.WriteAsync(input, [source], null, new() { SigningKey = signer, SourceSnapshot = key }); input.Position = 0;
            return await coordinator.CreatePlanAsync(input, actor, new() { ExpectedSignerFingerprint = receipt.SignerFingerprint, IdempotencyKey = key }, adapter);
        }
        var original = await Plan("metadata-first"); await coordinator.CommitAsync(original.PlanId, actor, adapter);
        var fields = System.Text.Json.Nodes.JsonNode.Parse(source.Data.FileRecords[0].EncryptedFieldsJson!)!;
        fields["fields"]!["fileName"]!["ciphertext"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(40));
        source.Data.FileRecords[0].EncryptedFieldsJson = fields.ToJsonString();
        var changed = await Plan("metadata-changed"); Assert.Equal("partial", changed.PreservationState);
        Assert.Single(changed.Omissions, omission => omission.OwnerKind == "file");
        await Assert.ThrowsAsync<ArchiveOperationException>(() => coordinator.CommitAsync(changed.PlanId, actor, adapter));
    }

    [Fact]
    public async Task ImmutablePlainHistoryConflict_IsPartialBeforeCommit()
    {
        var (source, _, _) = Fixture();
        var plain = source.Data.KnowledgeItems.Single();
        source.PlainVersions = [new() { Id = Guid.NewGuid(), KnowledgeId = plain.Id, OriginalKnowledgeId = plain.Id, VersionNumber = 1, Content = "original history" }];
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var actor = new User { Id = Guid.NewGuid(), TenantId = _tenant, Username = "history-admin", Role = Knowz.Core.Enums.UserRole.Admin, IsActive = true };
        _db.Users.Add(actor); await _db.SaveChangesAsync();
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var adapter = new SelfHostedArchiveImportAdapter(_db, tenant, _graph, new VaultAccessService(_db));
        using var coordinator = new ArchiveImportCoordinator();
        var identity = new ArchiveActor(_tenant, actor.Id.ToString());
        async Task<ArchiveImportPlan> Plan(string key)
        {
            using var input = new MemoryStream();
            var receipt = await KnowzArchiveWriter.WriteAsync(input, [source], null, new() { SigningKey = signer, SourceSnapshot = key }); input.Position = 0;
            return await coordinator.CreatePlanAsync(input, identity, new() { ExpectedSignerFingerprint = receipt.SignerFingerprint, IdempotencyKey = key, Strategy = "overwrite" }, adapter);
        }
        var initial = await Plan("history-original"); await coordinator.CommitAsync(initial.PlanId, identity, adapter);
        source.PlainVersions[0].Content = "mutated immutable history";
        var changed = await Plan("history-mutated");
        Assert.Equal("partial", changed.PreservationState);
        Assert.Contains(changed.Omissions, x => x.OwnerKind == "version");
        await Assert.ThrowsAsync<ArchiveOperationException>(() => coordinator.CommitAsync(changed.PlanId, identity, adapter));
        Assert.Equal("original history", (await _db.KnowledgeVersions.SingleAsync()).Content);
    }

    [Theory]
    [InlineData("item")]
    [InlineData("managed")]
    public async Task PrimarySelection_RejectsItemBoundOrManagedCustody(string ineligible)
    {
        var (source, _, _) = Fixture();
        Assert.True((await _graph.ImportAsync(source, null, "trusted:eligibility", ImportConflictStrategy.Skip, null, CancellationToken.None)).Success);
        var context = await _db.ShPortableCryptoContexts.SingleAsync();
        var key = await _db.ShCryptoKeyIdentities.SingleAsync();
        if (ineligible == "item") context.BindingKind = "item"; else key.Custody = "platform-managed";
        await _db.SaveChangesAsync();
        var vault = (await _db.Vaults.SingleAsync()).Id;
        var catalog = await _lifecycle.GetCatalogAsync(vault, CancellationToken.None);
        var selected = await _lifecycle.SelectPrimaryAsync(vault, context.Id, key.Fingerprint, catalog!.Revision, CancellationToken.None);
        Assert.False(selected.Ok); Assert.Equal(400, selected.StatusCode);
    }

    [Fact]
    public async Task SourceRetirement_IsMonotonicAndClearsOriginalPrimaryWriteChoice()
    {
        var (source, _, _) = Fixture();
        Assert.True((await _graph.ImportAsync(source, null, "trusted:retirement", ImportConflictStrategy.Skip, null, CancellationToken.None)).Success);
        var vault = (await _db.Vaults.SingleAsync()).Id;
        var catalog = (await _lifecycle.GetCatalogAsync(vault, CancellationToken.None))!;
        Assert.True((await _lifecycle.SelectPrimaryAsync(vault, catalog.Contexts.Single().ContextId, catalog.Keys.Single().Fingerprint, catalog.Revision, CancellationToken.None)).Ok);
        source.CryptoKeyCatalog![0].Lifecycle = "retired";
        Assert.True((await _graph.ImportAsync(source, null, "trusted:retirement", ImportConflictStrategy.Skip, null, CancellationToken.None)).Success);
        Assert.False((await _db.ShVaultCryptoContextLinks.SingleAsync()).IsPrimaryWriteContext);
        Assert.Equal("retired", (await _db.ShCryptoKeyIdentities.SingleAsync()).SourceLifecycle);
        source.CryptoKeyCatalog[0].Lifecycle = "active";
        Assert.True((await _graph.ImportAsync(source, null, "trusted:retirement", ImportConflictStrategy.Skip, null, CancellationToken.None)).Success);
        catalog = (await _lifecycle.GetCatalogAsync(vault, CancellationToken.None))!;
        Assert.False((await _lifecycle.SelectPrimaryAsync(vault, catalog.Contexts.Single().ContextId, catalog.Keys.Single().Fingerprint, catalog.Revision, CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task PlainHistoryAndTypedRelationships_RemainUsableAndPreserveOriginalMetadata()
    {
        var (source, _, _) = Fixture();
        var plain = source.Data.KnowledgeItems.Single();
        var sealedItem = source.SealedItems!.Single();
        var originalVersion = new PortablePlainVersionRecord { Id = Guid.NewGuid(), KnowledgeId = plain.Id, OriginalKnowledgeId = plain.Id, VersionNumber = 7,
            Title = "Original older title", Content = "Older readable plaintext", Summary = "older summary", Tags = "alpha,beta", SnapshotJson = "{\"custom\":true}", CreatedAt = DateTime.UtcNow.AddDays(-2), UpdatedAt = DateTime.UtcNow.AddDays(-1) };
        source.PlainVersions = [originalVersion];
        var originalRelation = new PortableKnowledgeRelationship { Id = Guid.NewGuid(), OriginalRelationshipId = Guid.NewGuid(), SourceKnowledgeId = sealedItem.Id, TargetKnowledgeId = plain.Id,
            RelationshipType = Knowz.Core.Enums.KnowledgeRelationshipType.RelatedTo, Confidence = .91, PortableMetadataJson = "{\"role\":\"evidence\"}", CreatedAt = DateTime.UtcNow.AddDays(-1), UpdatedAt = DateTime.UtcNow };
        sealedItem.Relationships = [originalRelation];
        Assert.True((await _graph.ImportAsync(source, null, "trusted:history-relations", ImportConflictStrategy.Skip, null, CancellationToken.None)).Success);
        var exported = await _graph.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None);
        var history = Assert.Single(exported.PlainVersions!);
        Assert.Equal(originalVersion.OriginalKnowledgeId, history.OriginalKnowledgeId);
        Assert.Equal(originalVersion.SnapshotJson, history.SnapshotJson);
        Assert.Equal(originalVersion.Content, (await _db.KnowledgeVersions.SingleAsync()).Content);
        Assert.Equal(exported.Data.KnowledgeItems.Single().Id, history.KnowledgeId);
        var relationship = Assert.Single(exported.SealedItems!.Single().Relationships!);
        Assert.Equal(originalRelation.OriginalRelationshipId, relationship.OriginalRelationshipId);
        Assert.Equal(originalRelation.PortableMetadataJson, relationship.PortableMetadataJson);
        Assert.Equal(exported.Data.KnowledgeItems.Single().Id, (await _db.KnowledgeRelationships.SingleAsync()).TargetKnowledgeId);
        Assert.True((await _graph.ImportAsync(source, null, "trusted:history-relations", ImportConflictStrategy.Skip, null, CancellationToken.None)).Success);
        Assert.Single(await _db.KnowledgeVersions.ToListAsync()); Assert.Single(await _db.KnowledgeRelationships.ToListAsync());
    }

    [Fact]
    public async Task NativeGraphItemAndComment_CreateEditRequireRevisionsAndPreserveReferences()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var source = System.Text.Json.JsonSerializer.Deserialize<PortableExportPackage>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portable-graph-v1.json")), options)!;
        Assert.True((await _graph.ImportAsync(source, null, "trusted:native", ImportConflictStrategy.Skip, null, CancellationToken.None)).Success);
        var restored = await _graph.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None);
        var vault = restored.Data.Vaults.Single().Id;
        var catalog = (await _lifecycle.GetCatalogAsync(vault, CancellationToken.None))!;
        await _lifecycle.SelectPrimaryAsync(vault, catalog.Contexts.Single().ContextId, catalog.Keys.Single().Fingerprint, catalog.Revision, CancellationToken.None);
        catalog = (await _lifecycle.GetCatalogAsync(vault, CancellationToken.None))!;
        var item = System.Text.Json.JsonSerializer.Deserialize<SealedKnowledgeItem>(System.Text.Json.JsonSerializer.Serialize(restored.SealedItems!.Single(), options), options)!;
        item.Id = Guid.NewGuid(); item.OriginalItemId = item.Id;
        var refs = restored.PayloadKeyReferences!.Where(r => r.OwnerKind == "knowledge").Select(r => System.Text.Json.JsonSerializer.Deserialize<PortablePayloadKeyReference>(System.Text.Json.JsonSerializer.Serialize(r, options), options)!).ToList();
        foreach (var r in refs) { r.OwnerId = item.Id; r.OriginalItemId = item.Id; }
        var write = new SealedItemWrite { VaultId = vault, Item = item, PayloadKeyReferences = refs };
        Assert.False((await _lifecycle.CreateAsync(write, CancellationToken.None, "stale")).Ok);
        item.TagIds = [Guid.NewGuid()];
        Assert.False((await _lifecycle.CreateAsync(write, CancellationToken.None, catalog.Revision)).Ok);
        Assert.False(await _db.KnowledgeItems.AnyAsync(k => k.Id == item.Id));
        item.TagIds = [];
        Assert.True((await _lifecycle.CreateAsync(write, CancellationToken.None, catalog.Revision)).Ok);
        var comment = restored.SealedComments!.Single(); comment.Id = Guid.NewGuid(); comment.OriginalCommentId = comment.Id;
        comment.KnowledgeId = item.Id; comment.OriginalKnowledgeId = item.OriginalItemId;
        var commentRefs = restored.PayloadKeyReferences.Where(r => r.OwnerKind == "comment").ToList();
        foreach (var r in commentRefs) { r.OwnerId = comment.Id; r.OriginalItemId = comment.Id; }
        var commentWrite = new SealedCommentWrite { Comment = comment, PayloadKeyReferences = commentRefs, VaultId = vault };
        Assert.True((await _lifecycle.CreateCommentAsync(item.Id, commentWrite, CancellationToken.None, catalog.Revision)).Ok);
        Assert.Equal("", (await _db.ShSealedComments.SingleAsync(c => c.Id == comment.Id)).Ciphertext);
        Assert.Empty(await _db.Comments.ToListAsync());
        var current = (await _lifecycle.GetAsync(item.Id, CancellationToken.None))!;
        Assert.False((await _lifecycle.UpdateAsync(item.Id, write, CancellationToken.None, "stale")).Ok);
        Assert.True((await _lifecycle.UpdateAsync(item.Id, write, CancellationToken.None, current.Revision)).Ok);
        var exported = await _graph.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None);
        Assert.Null(EncryptedGraphProfile.Validate(exported));
        Assert.Equal(2, exported.SealedItems!.Count);
        Assert.Equal(_tenant, (await _db.KnowledgeVaults.SingleAsync(v => v.KnowledgeId == item.Id)).TenantId);
        Assert.Contains(exported.PayloadKeyReferences!, r => r.OwnerKind == "comment" && r.OwnerId == comment.Id);
    }

    [Fact]
    public async Task NativeEncryptedFile_StageIsInvisible_UploadChecksBytesAndActivatesOnce()
    {
        var (source, _, _) = Fixture();
        Assert.True((await _graph.ImportAsync(source, null, "trusted:native-files", ImportConflictStrategy.Skip, null, CancellationToken.None)).Success);
        var restored = await _graph.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None);
        var vault = restored.Data.Vaults.Single().Id;
        var catalog = (await _lifecycle.GetCatalogAsync(vault, CancellationToken.None))!;
        await _lifecycle.SelectPrimaryAsync(vault, catalog.Contexts.Single().ContextId, catalog.Keys.Single().Fingerprint, catalog.Revision, CancellationToken.None);
        catalog = (await _lifecycle.GetCatalogAsync(vault, CancellationToken.None))!;
        var file = restored.SealedFiles!.Single(); var bytes = Convert.FromBase64String(source.SealedFiles!.Single().BinaryContentBase64!);
        file.KnowledgeId = restored.SealedItems!.Single().Id; file.Id = Guid.NewGuid(); file.OriginalFileId = file.Id; file.BinaryEntryPath = null; file.BinaryContentBase64 = null;
        var refs = restored.PayloadKeyReferences!.Where(r => r.OwnerKind == "file").ToList();
        foreach (var r in refs) { r.OwnerId = file.Id; r.OriginalItemId = file.Id; }
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var service = new SelfHostedSealedFiles(_db, tenant, _lifecycle, _storage);
        var actor = Guid.NewGuid();
        var write = new SealedFileWrite { VaultId = vault, Record = file, PayloadKeyReferences = refs };
        var stage = await service.StageAsync(actor, write, catalog.Revision, "file-once", CancellationToken.None);
        Assert.True(stage.Ok, stage.Error);
        Assert.False(await _db.ShSealedFiles.AnyAsync(f => f.Id == file.Id));
        Assert.Single((await _graph.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None)).SealedFiles!);
        var bad = await service.UploadAsync(actor, file.Id, new MemoryStream(bytes), stage.Revision!, new string('0', 64), CancellationToken.None);
        Assert.False(bad.Ok);
        Assert.False(await _db.ShSealedFiles.AnyAsync(f => f.Id == file.Id));
        var saved = await service.UploadAsync(actor, file.Id, new MemoryStream(bytes), stage.Revision!, file.BinarySha256!, CancellationToken.None);
        Assert.True(saved.Ok, saved.Error);
        using var downloaded = await _graph.OpenFileAsync(file.Id, CancellationToken.None); using var actual = new MemoryStream(); await downloaded.CopyToAsync(actual);
        Assert.Equal(bytes, actual.ToArray());
        Assert.True((await service.StageAsync(actor, write, catalog.Revision, "file-once", CancellationToken.None)).Ok);
        var changedBytes = bytes.ToArray(); changedBytes[0] ^= 1;
        var priorDigest = file.BinarySha256;
        file.BinarySha256 = Convert.ToHexString(SHA256.HashData(changedBytes)).ToLowerInvariant();
        var rejected = await service.UpdateAsync(actor, write, saved.Revision!, new MemoryStream(bytes), CancellationToken.None);
        Assert.False(rejected.Ok);
        using (var unchanged = await _graph.OpenFileAsync(file.Id, CancellationToken.None)) { using var content = new MemoryStream(); await unchanged.CopyToAsync(content); Assert.Equal(bytes, content.ToArray()); }
        var replacement = await service.UpdateAsync(actor, write, saved.Revision!, new MemoryStream(changedBytes), CancellationToken.None);
        Assert.True(replacement.Ok, replacement.Error);
        Assert.False((await service.UpdateAsync(actor, write, saved.Revision!, null, CancellationToken.None)).Ok);
        using (var changed = await _graph.OpenFileAsync(replacement.Write!.Record.Id, CancellationToken.None)) { using var content = new MemoryStream(); await changed.CopyToAsync(content); Assert.Equal(changedBytes, content.ToArray()); }
        using (var retained = await _graph.OpenFileAsync(file.Id, CancellationToken.None)) { using var content = new MemoryStream(); await retained.CopyToAsync(content); Assert.Equal(bytes, content.ToArray()); }
        file.BinarySha256 = priorDigest;
        Assert.Equal(3, await _db.ShSealedFiles.CountAsync());
        Assert.Null(EncryptedGraphProfile.Validate(await _graph.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None)));
    }

    [Fact]
    public async Task FailedUploadCleanup_RemainsDurablyOwnedUntilAReconcilerCanDeleteIt()
    {
        var (source, _, _) = Fixture(); Assert.True((await _graph.ImportAsync(source, null, "trusted:failed-cleanup", ImportConflictStrategy.Skip, null, CancellationToken.None)).Success);
        var exported = await _graph.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None); var vault = exported.Data.Vaults.Single().Id;
        var catalog = (await _lifecycle.GetCatalogAsync(vault, CancellationToken.None))!;
        Assert.True((await _lifecycle.SelectPrimaryAsync(vault, catalog.Contexts.Single().ContextId, catalog.Keys.Single().Fingerprint, catalog.Revision, CancellationToken.None)).Ok);
        catalog = (await _lifecycle.GetCatalogAsync(vault, CancellationToken.None))!;
        var file = exported.SealedFiles!.Single(); file.Id = Guid.NewGuid(); file.OriginalFileId = file.Id; file.KnowledgeId = exported.SealedItems!.Single().Id; file.BinaryEntryPath = null;
        var refs = exported.PayloadKeyReferences!.Where(r => r.OwnerKind == "file").ToList(); foreach (var r in refs) { r.OwnerId = file.Id; r.OriginalItemId = file.Id; }
        var failedStorage = Substitute.For<Knowz.SelfHosted.Infrastructure.Interfaces.IFileStorageProvider>();
        var persistedUpload = false;
        async Task<string> PersistAndFailUpload(NSubstitute.Core.CallInfo call)
        {
            await _storage.UploadAsync(call.ArgAt<Guid>(0), call.ArgAt<Guid>(1), call.ArgAt<Stream>(2), call.ArgAt<string>(3), call.ArgAt<CancellationToken>(4));
            persistedUpload = true; throw new IOException("Synthetic interrupted response after persisted write.");
        }
        failedStorage.UploadAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => PersistAndFailUpload(call));
        failedStorage.DeleteAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromException<bool>(new IOException("Synthetic storage unavailable.")));
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant); var service = new SelfHostedSealedFiles(_db, tenant, _lifecycle, failedStorage); var actor = Guid.NewGuid();
        var stage = await service.StageAsync(actor, new() { VaultId = vault, Record = file, PayloadKeyReferences = refs }, catalog.Revision, "cleanup-failure", CancellationToken.None); Assert.True(stage.Ok);
        var uploadBytes = Convert.FromBase64String(source.SealedFiles!.Single().BinaryContentBase64!);
        Assert.Equal(file.SizeBytes, uploadBytes.LongLength); Assert.Equal(file.BinarySha256, Convert.ToHexStringLower(SHA256.HashData(uploadBytes)));
        var result = await service.UploadAsync(actor, file.Id, new MemoryStream(uploadBytes), stage.Revision!, file.BinarySha256!, CancellationToken.None);
        Assert.False(result.Ok);
        var intent = await _db.ShSealedFileStages.SingleAsync(); Assert.NotNull(intent.BlobStorageId); Assert.Equal("cancelled", intent.Status);
        Assert.True(persistedUpload, "Synthetic post-persistence upload fault was not reached.");
        var orphan = intent.BlobStorageId!.Value; Assert.True(await _storage.ExistsAsync(_tenant, orphan), "Persisted operation-owned bytes disappeared before the explicit reconciler.");
        await new SelfHostedPortabilityStageReconciler(_db, _storage, NullLogger<SelfHostedPortabilityStageReconciler>.Instance).ReconcileAsync(DateTime.UtcNow.AddMinutes(1), CancellationToken.None);
        Assert.False(await _storage.ExistsAsync(_tenant, orphan)); Assert.Null(intent.BlobStorageId); Assert.Single(await _db.ShSealedFiles.ToListAsync());
    }

    [Fact]
    public async Task LogicalFileDeletionRetainsReplacedPhysicalObjectsWhenStorageIsUnavailable()
    {
        var (source, _, _) = Fixture(); Assert.True((await _graph.ImportAsync(source, null, "trusted:old-object-cleanup", ImportConflictStrategy.Skip, null, CancellationToken.None)).Success);
        var exported = await _graph.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None); var vault = exported.Data.Vaults.Single().Id;
        var catalog = (await _lifecycle.GetCatalogAsync(vault, CancellationToken.None))!; Assert.True((await _lifecycle.SelectPrimaryAsync(vault, catalog.Contexts.Single().ContextId, catalog.Keys.Single().Fingerprint, catalog.Revision, CancellationToken.None)).Ok);
        var failedStorage = Substitute.For<Knowz.SelfHosted.Infrastructure.Interfaces.IFileStorageProvider>();
        failedStorage.UploadAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _storage.UploadAsync(call.ArgAt<Guid>(0), call.ArgAt<Guid>(1), call.ArgAt<Stream>(2), call.ArgAt<string>(3), call.ArgAt<CancellationToken>(4)));
        failedStorage.DeleteAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromException<bool>(new IOException("Synthetic storage unavailable.")));
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant); var service = new SelfHostedSealedFiles(_db, tenant, _lifecycle, failedStorage);
        var old = await _db.ShSealedFiles.SingleAsync(); var oldBlob = old.BlobStorageId!.Value;
        var record = System.Text.Json.JsonSerializer.Deserialize<SealedFileRecord>(System.Text.Json.JsonSerializer.Serialize(old))!;
        var write = new SealedFileWrite { VaultId = vault, Record = record, PayloadKeyReferences = exported.PayloadKeyReferences!.Where(r => r.OwnerKind == "file").ToList() };
        var replacement = await service.UpdateAsync(Guid.NewGuid(), write, SelfHostedSealedStore.PayloadRevision(record), new MemoryStream(Convert.FromBase64String(source.SealedFiles!.Single().BinaryContentBase64!)), CancellationToken.None); Assert.True(replacement.Ok, replacement.Error);
        var currentBlob = (await _db.ShSealedFiles.SingleAsync(f => f.IsCurrentVersion)).BlobStorageId!.Value;
        Assert.True(await _storage.ExistsAsync(_tenant, oldBlob));
        Assert.DoesNotContain(await _db.ShSealedFileStages.ToListAsync(), s => s.Status != "committed" && s.BlobStorageId == oldBlob);
        var reconciler = new SelfHostedPortabilityStageReconciler(_db, _storage, NullLogger<SelfHostedPortabilityStageReconciler>.Instance);
        await reconciler.ReconcileAsync(DateTime.UtcNow.AddMinutes(1), CancellationToken.None); Assert.True(await _storage.ExistsAsync(_tenant, oldBlob)); Assert.True(await _storage.ExistsAsync(_tenant, currentBlob));
        Assert.True((await service.DeleteFileAsync(replacement.Write!.Record.Id, replacement.Revision!, CancellationToken.None)).Ok);
        Assert.DoesNotContain(await _db.ShSealedFileStages.ToListAsync(), s => s.Status != "committed" && (s.BlobStorageId == currentBlob || s.BlobStorageId == oldBlob));
        await reconciler.ReconcileAsync(DateTime.UtcNow.AddMinutes(1), CancellationToken.None); Assert.True(await _storage.ExistsAsync(_tenant, currentBlob)); Assert.True(await _storage.ExistsAsync(_tenant, oldBlob));
        Assert.Equal(2, await _db.ShSealedFiles.IgnoreQueryFilters().CountAsync(f => f.IsDeleted));
    }

    [Fact]
    public async Task ExpiredUploadAndArchiveStages_AreReconciledWithoutDeletingLiveOrActiveObjects()
    {
        var orphan = Guid.NewGuid(); var live = Guid.NewGuid(); var active = Guid.NewGuid();
        foreach (var id in new[] { orphan, live, active }) await _storage.UploadAsync(_tenant, id, new MemoryStream([1, 2, 3]), "application/octet-stream", CancellationToken.None);
        _db.ShArchiveImportJournals.Add(new() { PlanId = Guid.NewGuid(), TenantId = _tenant, Status = "prepared", UpdatedAt = DateTime.UtcNow.AddHours(-2), StagedObjectIdsJson = System.Text.Json.JsonSerializer.Serialize(new[] { orphan, live }) });
        _db.ShFileObjectReferences.Add(new() { FileRecordId = Guid.NewGuid(), TenantId = _tenant, BlobStorageId = live, Sha256 = new string('a', 64) });
        _db.ShSealedFileStages.Add(new() { Id = Guid.NewGuid(), TenantId = _tenant, ActorId = Guid.NewGuid(), FileId = Guid.NewGuid(), IdempotencyKey = "not-expired", BlobStorageId = active, Status = "uploading", ExpiresAt = DateTime.UtcNow.AddHours(1) });
        var reconciler = new SelfHostedPortabilityStageReconciler(_db, _storage, NullLogger<SelfHostedPortabilityStageReconciler>.Instance);
        await _db.SaveChangesAsync();
        await reconciler.ReconcileAsync(DateTime.UtcNow, CancellationToken.None);
        await Assert.ThrowsAsync<FileNotFoundException>(() => _storage.DownloadAsync(_tenant, orphan, CancellationToken.None));
        using var preserved = (await _storage.DownloadAsync(_tenant, live, CancellationToken.None)).stream;
        using var uploading = (await _storage.DownloadAsync(_tenant, active, CancellationToken.None)).stream;
        Assert.Equal("cancelled", (await _db.ShArchiveImportJournals.SingleAsync()).Status);
        Assert.Equal("uploading", (await _db.ShSealedFileStages.SingleAsync()).Status);
    }

    [Fact]
    public async Task AttachmentEditAndOwnerDeletion_UseRevisionsAndKeepTheRemainingGraphValid()
    {
        var (source, _, _) = Fixture(); Assert.True((await _graph.ImportAsync(source, null, "trusted:deletions", ImportConflictStrategy.Skip, null, CancellationToken.None)).Success);
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var files = new SelfHostedSealedFiles(_db, tenant, _lifecycle, _storage);
        var exported = await _graph.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None);
        var link = exported.SealedAttachments!.Single(a => a.KnowledgeId.HasValue); var revision = SelfHostedSealedStore.PayloadRevision(link);
        link.DisplayOrder = 9; link.ExcludeFromAiProcessing = true;
        var write = new SealedAttachmentWrite { VaultId = exported.Data.Vaults.Single().Id, Record = link, PayloadKeyReferences = [] };
        Assert.False((await files.UpdateAttachmentAsync(write, "stale", CancellationToken.None)).Ok);
        Assert.True((await files.UpdateAttachmentAsync(write, revision, CancellationToken.None)).Ok);
        Assert.Equal(9, (await _db.ShSealedAttachments.SingleAsync(a => a.Id == link.Id)).DisplayOrder);
        var comment = (await _lifecycle.GetCommentAsync(exported.SealedComments!.Single().Id, CancellationToken.None))!;
        Assert.False((await _lifecycle.DeleteCommentAsync(comment.Id, CancellationToken.None, "stale")).Ok);
        Assert.True((await _lifecycle.DeleteCommentAsync(comment.Id, CancellationToken.None, SelfHostedSealedStore.PayloadRevision(comment.Envelope))).Ok);
        exported = await _graph.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None); Assert.Null(EncryptedGraphProfile.Validate(exported));
        Assert.Single(exported.SealedAttachments!);
        var item = (await _lifecycle.GetAsync(exported.SealedItems!.Single().Id, CancellationToken.None))!;
        Assert.False((await _lifecycle.DeleteAsync(item.Id, CancellationToken.None, "stale")).Ok);
        Assert.True((await _lifecycle.DeleteAsync(item.Id, CancellationToken.None, item.Revision)).Ok);
        exported = await _graph.ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, false, CancellationToken.None); Assert.Null(EncryptedGraphProfile.Validate(exported));
        Assert.Empty(exported.SealedAttachments!); Assert.Empty(exported.SealedItems!); Assert.Single(exported.SealedFiles!);
        var file = exported.SealedFiles!.Single();
        // Deleting the last owner preserves an orphan for tenant export; explicit file deletion removes it.
        var stored = await _db.ShSealedFiles.SingleAsync(); var publicFile = System.Text.Json.JsonSerializer.Deserialize<SealedFileRecord>(System.Text.Json.JsonSerializer.Serialize(stored))!;
        Assert.True((await files.DeleteFileAsync(file.Id, SelfHostedSealedStore.PayloadRevision(publicFile), CancellationToken.None)).Ok);
        Assert.Empty(await _db.ShSealedFiles.ToListAsync()); Assert.Empty(await _db.ShPayloadKeyReferences.ToListAsync());
    }

    private static (PortableExportPackage Package, byte[] Key, byte[] Plaintext) Fixture()
    {
        var vault = Guid.NewGuid(); var item = Guid.NewGuid(); var plain = Guid.NewGuid(); var context = Guid.NewGuid(); var file = Guid.NewGuid(); var comment = Guid.NewGuid();
        var publicKey = RandomNumberGenerator.GetBytes(32); var fingerprint = Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();
        var key = RandomNumberGenerator.GetBytes(32); var nonce = RandomNumberGenerator.GetBytes(12); var text = Encoding.UTF8.GetBytes("original binary \0 data with authenticated recovery");
        var cipher = new byte[text.Length]; var tag = new byte[16]; using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, text, cipher, tag);
        var payload = Convert.ToBase64String(cipher.Concat(tag).ToArray());
        var dek = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)); var epk = Convert.ToBase64String(publicKey); var n = Convert.ToBase64String(nonce);
        var package = new PortableExportPackage {
            SchemaVersion = EncryptedGraphProfile.SchemaVersion, RequiredCapabilities = [EncryptedGraphProfile.Capability], SourceEdition = "platform", SourceTenantId = Guid.NewGuid(),
            Data = new PortableExportData { Vaults = [new() { Id = vault, Name = "Imported mixed vault" }], KnowledgeItems = [new() { Id = plain, Title = "plain", Content = "plain sibling", VaultIds = [vault], PrimaryVaultId = vault }] },
            CryptoContexts = [new() { ContextId = context, OriginalBindingId = vault }],
            CryptoKeyCatalog = [new() { ContextId = context, PublicKey = epk, Fingerprint = fingerprint, Generation = 1, Lifecycle = "active" }],
            VaultCryptoContextLinks = [new() { VaultId = vault, ContextId = context }],
            SealedItems = [new() { Id = item, VaultIds = [vault], PrimaryVaultId = vault, OriginalItemId = item, Ciphertext = payload, EnvelopeVersion = 2, EncryptedDek = dek, EncryptedDekNonce = n, EphemeralPublicKey = epk, ContentNonce = n, CryptoBindingId = vault }],
            SealedComments = [new() { Id = comment, OriginalCommentId = comment, KnowledgeId = item, OriginalKnowledgeId = item, Ciphertext = payload, EnvelopeVersion = 2, EncryptedDek = dek, EncryptedDekNonce = n, EphemeralPublicKey = epk, ContentNonce = n, CryptoBindingId = vault }],
            SealedFiles = [new() { Id = file, OriginalFileId = file, BinaryContentBase64 = payload, SizeBytes = cipher.Length + tag.Length, EnvelopeVersion = 2, EncryptedDek = dek, EncryptedDekNonce = n, EphemeralPublicKey = epk, ContentNonce = n, CryptoBindingId = vault }],
            SealedAttachments = [new() { Id = Guid.NewGuid(), OriginalAttachmentId = Guid.NewGuid(), FileRecordId = file, KnowledgeId = item }, new() { Id = Guid.NewGuid(), OriginalAttachmentId = Guid.NewGuid(), FileRecordId = file, CommentId = comment }],
            PayloadKeyReferences = []
        };
        foreach (var (kind, owner, role) in new[] { ("knowledge", item, "body"), ("comment", comment, "body"), ("file", file, "binary") })
            package.PayloadKeyReferences.Add(new() { OwnerKind = kind, OwnerId = owner, Role = role, ContextId = context, Fingerprint = fingerprint, OriginalBindingId = vault, OriginalItemId = kind == "knowledge" ? item : null, EnvelopeVersion = 2 });
        Assert.Null(EncryptedGraphProfile.Validate(package));
        return (package, key, text);
    }
}
