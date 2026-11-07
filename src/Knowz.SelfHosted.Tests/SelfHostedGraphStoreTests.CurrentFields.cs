using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Infrastructure.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSec.Cryptography;
using NSubstitute;
namespace Knowz.SelfHosted.Tests;
public sealed partial class SelfHostedGraphStoreTests
{
    private static async Task<PortableExportPackage> CurrentFieldFixtureAsync()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web); json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var source = JsonSerializer.Deserialize<PortableExportPackage>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portable-graph-v1.json")), json)!;
        var item = source.Data.KnowledgeItems.Single(); item.Title = "Encrypted item";
        item.EncryptedFieldsJson = source.SealedItems!.Single().EncryptedFieldsJson; item.OriginalItemId = item.Id;
        var reference = JsonSerializer.Deserialize<PortablePayloadKeyReference>(JsonSerializer.Serialize(source.PayloadKeyReferences!.Single(r => r.OwnerKind == "knowledge" && r.Role == "field")))!;
        reference.OwnerId = item.Id; reference.OriginalItemId = item.Id; source.PayloadKeyReferences.Add(reference);
        Assert.Null(EncryptedGraphProfile.Validate(source)); return source;
    }
    [Fact]
    public async Task CurrentOrdinaryKnowledgePersistsTypedFieldsAndReturnsExactOriginalKeyCiphertext()
    {
        var source = await CurrentFieldFixtureAsync(); var incoming = source.Data.KnowledgeItems.Single();
        var result = await _graph.ImportAsync(source, null, "trusted:current-fields", ImportConflictStrategy.Skip, null, default); Assert.True(result.Success, result.Error);
        var row = await _db.KnowledgeItems.SingleAsync(k => k.Content == incoming.Content);
        Assert.NotNull(_db.Model.FindEntityType(typeof(Knowledge))!.FindProperty("EncryptedFieldsJson"));
        Assert.Equal(incoming.EncryptedFieldsJson, typeof(Knowledge).GetProperty("EncryptedFieldsJson")!.GetValue(row));
        Assert.DoesNotContain("encryptedFieldsJson", row.PlatformData ?? "", StringComparison.OrdinalIgnoreCase);
        row.Title = "STALE_PRIVATE_CURRENT_TITLE"; await _db.SaveChangesAsync();
        var exported = await _graph.ExportAsync(new() { Type = ExportScopeType.Tenant }, false, default);
        var restored = exported.Data.KnowledgeItems.Single(k => k.Id == row.Id);
        Assert.Equal(incoming.EncryptedFieldsJson, restored.EncryptedFieldsJson); Assert.Equal(incoming.OriginalItemId, restored.OriginalItemId);
        Assert.Equal("Encrypted item", restored.Title); Assert.DoesNotContain("STALE_PRIVATE_CURRENT_TITLE", JsonSerializer.Serialize(exported));
        var reference = Assert.Single(exported.PayloadKeyReferences!.Where(r => r.OwnerKind == "knowledge" && r.OwnerId == row.Id));
        Assert.Equal(incoming.OriginalItemId, reference.OriginalItemId); Assert.Equal(source.PayloadKeyReferences!.Last().OriginalBindingId, reference.OriginalBindingId);
        Assert.Equal(source.PayloadKeyReferences.Last().Fingerprint, reference.Fingerprint); Assert.Equal(source.PayloadKeyReferences.Last().Generation, reference.Generation);
        Assert.Equal("knowledge:title", DecryptCurrentField(restored.EncryptedFieldsJson!));
    }
    [Fact]
    public async Task LegacyExportCannotClaimPortableCurrentFieldsWithoutTheirGraphReferences()
    {
        var source = await CurrentFieldFixtureAsync(); var plain = source.Data.KnowledgeItems.Single();
        source.SealedItems = []; source.SealedComments = []; source.SealedFiles = []; source.SealedAttachments = []; source.SealedVersions = []; source.PlainVersions = [];
        source.PayloadKeyReferences = source.PayloadKeyReferences!.Where(r => r.OwnerKind == "knowledge" && r.OwnerId == plain.Id).ToList();
        source.Data.FileRecords = []; source.Data.Comments = [];
        Assert.Null(EncryptedGraphProfile.Validate(source));
        Assert.True((await _graph.ImportAsync(source, null, "trusted:legacy-current-only", ImportConflictStrategy.Skip, null, default)).Success);
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var legacy = new PortableExportService(_db, tenant, _storage, Microsoft.Extensions.Options.Options.Create(new Knowz.Core.Configuration.SelfHostedOptions()), Microsoft.Extensions.Logging.Abstractions.NullLogger<PortableExportService>.Instance);
        await Assert.ThrowsAsync<SealedItemLockedException>(() => legacy.ExportAsync());
        var modern = await _graph.ExportAsync(new() { Type = ExportScopeType.Tenant }, false, default);
        Assert.Equal(plain.EncryptedFieldsJson, modern.Data.KnowledgeItems.Single().EncryptedFieldsJson);
        Assert.Single(modern.PayloadKeyReferences!); Assert.Null(EncryptedGraphProfile.Validate(modern));
    }
    [Fact]
    public async Task CurrentFieldHttpPayloadRetainsTypedFieldAndRejectsForeignVault()
    {
        var source = await CurrentFieldFixtureAsync(); Assert.True((await _graph.ImportAsync(source, null, "trusted:current-http", ImportConflictStrategy.Skip, null, default)).Success);
        var row = await _db.KnowledgeItems.SingleAsync(k => k.Content == source.Data.KnowledgeItems.Single().Content);
        row.Title = "STALE_PRIVATE_CURRENT_TITLE"; await _db.SaveChangesAsync();
        var user = new User { TenantId = _tenant, Username = "current-admin", IsActive = true, Role = Knowz.Core.Enums.UserRole.Admin }; _db.Users.Add(user); await _db.SaveChangesAsync();
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Services.AddSingleton(_db); builder.Services.AddSingleton(_lifecycle); builder.Services.AddSingleton(_graph);
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        builder.Services.AddSingleton(new SelfHostedSealedFiles(_db, tenant, _lifecycle, _storage)); builder.Services.AddSingleton<Knowz.SelfHosted.Application.Interfaces.IVaultAccessService>(new VaultAccessService(_db));
        await using var app = builder.Build(); app.Use(async (context, next) => { context.User = new(new System.Security.Claims.ClaimsIdentity([new(System.Security.Claims.ClaimTypes.NameIdentifier, user.Id.ToString()), new("tenantId", _tenant.ToString()), new(System.Security.Claims.ClaimTypes.Role, "Admin")], "fixture")); await next(); });
        Knowz.SelfHosted.API.Endpoints.EncryptedVaultEndpoints.MapEncryptedVaultEndpoints(app); await app.StartAsync(); using var client = app.GetTestClient();
        var vault = (await _db.KnowledgeVaults.SingleAsync(v => v.KnowledgeId == row.Id)).VaultId;
        using var response = await client.GetAsync($"/api/v1/vaults/{vault}/encryption/payloads/knowledge/{row.Id}"); response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(source.Data.KnowledgeItems.Single().EncryptedFieldsJson, payload.GetProperty("record").GetProperty("encryptedFieldsJson").GetString());
        Assert.Equal("Encrypted item", payload.GetProperty("record").GetProperty("title").GetString()); Assert.DoesNotContain("STALE_PRIVATE_CURRENT_TITLE", payload.GetRawText());
        Assert.Single(payload.GetProperty("payloadKeyReferences").EnumerateArray()); Assert.Equal("knowledge:title", DecryptCurrentField(payload.GetProperty("record").GetProperty("encryptedFieldsJson").GetString()!));
        using var denied = await client.GetAsync($"/api/v1/vaults/{Guid.NewGuid()}/encryption/payloads/knowledge/{row.Id}"); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NormalKnowledgeHttpDeclaresCurrentProtectedFieldsWithoutChangingBodyMode(bool protectedFields)
    {
        var source = await CurrentFieldFixtureAsync();
        Assert.True((await _graph.ImportAsync(source,null,"trusted:normal-current-marker",ImportConflictStrategy.Skip,null,default)).Success);
        var row = await _db.KnowledgeItems.SingleAsync(k => k.Content == source.Data.KnowledgeItems.Single().Content);
        if (!protectedFields) { row.EncryptedFieldsJson = null; await _db.SaveChangesAsync(); }
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var service = new KnowledgeService(new SelfHostedRepository<Knowledge>(_db),new SelfHostedRepository<Tag>(_db),_db,Substitute.For<ISearchService>(),Substitute.For<IOpenAIService>(),new SelfHostedChunkingService(),tenant,Microsoft.Extensions.Logging.Abstractions.NullLogger<KnowledgeService>.Instance);
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Services.AddSingleton(service); builder.Services.AddSingleton(_db); builder.Services.AddSingleton(tenant);
        builder.Services.AddSingleton<Knowz.SelfHosted.Application.Interfaces.IVaultAccessService>(new VaultAccessService(_db));
        builder.Services.AddSingleton<FileStorageService>(_ => throw new InvalidOperationException("Unused write service"));
        builder.Services.AddSingleton<VaultService>(_ => throw new InvalidOperationException("Unused list service"));
        builder.Services.AddSingleton(Substitute.For<Knowz.SelfHosted.Infrastructure.Interfaces.IContentAmendmentService>());
        builder.Services.AddSingleton(Substitute.For<Knowz.SelfHosted.Infrastructure.Interfaces.IEnrichmentOutboxWriter>());
        await using var app = builder.Build(); app.Use(async (http,next) => { http.User = new(new System.Security.Claims.ClaimsIdentity([new(System.Security.Claims.ClaimTypes.NameIdentifier,Guid.NewGuid().ToString()),new("tenantId",_tenant.ToString()),new(System.Security.Claims.ClaimTypes.Role,"SuperAdmin")],"fixture")); await next(); });
        Knowz.SelfHosted.API.Endpoints.KnowledgeEndpoints.MapKnowledgeEndpoints(app); await app.StartAsync(); using var client = app.GetTestClient();
        using var response = await client.GetAsync($"/api/v1/knowledge/{row.Id}"); response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("isEncrypted").GetBoolean()); Assert.Equal(row.Content,payload.GetProperty("content").GetString());
        Assert.Equal(protectedFields,payload.GetProperty("hasEncryptedFields").GetBoolean());
        Assert.False(payload.TryGetProperty("encryptedFieldsJson",out _));
        var bulk = JsonSerializer.SerializeToElement(await service.BulkGetKnowledgeItemsAsync([row.Id],default),new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(protectedFields,bulk.GetProperty("items")[0].GetProperty("hasEncryptedFields").GetBoolean());
        using var foreign = await client.GetAsync($"/api/v1/knowledge/{Guid.NewGuid()}"); Assert.Equal(HttpStatusCode.NotFound,foreign.StatusCode);
    }
    [Fact]
    public void CurrentFieldsNeedFirstClassPersistentMigrationColumns()
    {
        var entity = _db.Model.FindEntityType(typeof(Knowledge))!;
        Assert.NotNull(entity.FindProperty("EncryptedFieldsJson")); Assert.NotNull(entity.FindProperty("OriginalItemId"));
    }
    [Theory]
    [InlineData("skip")]
    [InlineData("merge")]
    [InlineData("overwrite")]
    public async Task CurrentFieldConflictsNeverClaimCompletePreservationOfDifferentBytes(string strategy)
    {
        var source = await CurrentFieldFixtureAsync(); using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var actor = new User { TenantId = _tenant, Username = "current-conflict", IsActive = true, Role = Knowz.Core.Enums.UserRole.Admin }; _db.Users.Add(actor); await _db.SaveChangesAsync();
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant); var adapter = new SelfHostedArchiveImportAdapter(_db,tenant,_graph,new VaultAccessService(_db));
        using var coordinator = new ArchiveImportCoordinator(); var identity = new ArchiveActor(_tenant,actor.Id.ToString("D"));
        async Task<ArchiveImportPlan> Plan(string key,string selectedStrategy)
        {
            using var input = new MemoryStream(); var receipt = await KnowzArchiveWriter.WriteAsync(input,[source],null,new() { SigningKey = signer, SourceSnapshot = key }); input.Position = 0;
            return await coordinator.CreatePlanAsync(input,identity,new() { IdempotencyKey = key, Strategy = selectedStrategy, ExpectedSignerFingerprint = receipt.SignerFingerprint },adapter);
        }
        var first = await Plan("current-original","skip"); var receipt = await coordinator.CommitAsync(first.PlanId,identity,adapter); Assert.Equal(receipt.RequiredPayloadCount,receipt.PreservedPayloadCount);
        var incoming = source.Data.KnowledgeItems.Single(); var old = incoming.EncryptedFieldsJson; var fields = System.Text.Json.Nodes.JsonNode.Parse(old!)!;
        fields["fields"]!["title"]!["ciphertext"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); incoming.EncryptedFieldsJson = fields.ToJsonString();
        var changed = await Plan("current-changed",strategy);
        if (strategy == "overwrite") { Assert.Equal("complete",changed.PreservationState); var committed = await coordinator.CommitAsync(changed.PlanId,identity,adapter); Assert.Equal(committed.RequiredPayloadCount,committed.PreservedPayloadCount); }
        else { Assert.Equal("partial",changed.PreservationState); Assert.Contains(changed.Omissions,o => o.OwnerKind == "knowledge" && o.OwnerId == incoming.Id && o.Reason.Contains("field")); var error = await Assert.ThrowsAsync<ArchiveOperationException>(() => coordinator.CommitAsync(changed.PlanId,identity,adapter)); Assert.Equal(409,error.StatusCode); }
        var stored = await _db.KnowledgeItems.SingleAsync(k => k.Content == incoming.Content); Assert.Equal(strategy == "overwrite" ? incoming.EncryptedFieldsJson : old,stored.EncryptedFieldsJson);
    }
    private static string DecryptCurrentField(string json)
    {
        using var sidecar = JsonDocument.Parse(json); var field = sidecar.RootElement.GetProperty("fields").GetProperty("title"); byte[] Read(string name) => Convert.FromBase64String(field.GetProperty(name).GetString()!);
        using var key = Key.Import(KeyAgreementAlgorithm.X25519, Enumerable.Range(1,32).Select(x => (byte)x).ToArray(), KeyBlobFormat.RawPrivateKey);
        var ephemeral = Read("ephemeralPublicKey"); using var shared = KeyAgreementAlgorithm.X25519.Agree(key, PublicKey.Import(KeyAgreementAlgorithm.X25519, ephemeral, KeyBlobFormat.RawPublicKey))!;
        var wrapping = KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(shared, ephemeral, "knowz-dek-wrap-v1"u8.ToArray(), 32); var dek = new byte[32]; var wrapped = Read("encryptedDek");
        using (var aes = new AesGcm(wrapping,16)) aes.Decrypt(Read("dekNonce"), wrapped.AsSpan(0,32), wrapped.AsSpan(32), dek);
        var bytes = Read("ciphertext"); var plain = new byte[bytes.Length-16]; using (var aes = new AesGcm(dek,16)) aes.Decrypt(Read("contentNonce"), bytes.AsSpan(0,plain.Length), bytes.AsSpan(plain.Length), plain);
        return Encoding.UTF8.GetString(plain);
    }
}
