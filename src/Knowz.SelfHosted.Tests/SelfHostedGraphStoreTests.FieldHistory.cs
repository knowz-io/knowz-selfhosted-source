using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed partial class SelfHostedGraphStoreTests
{
    private static async Task<PortableExportPackage> FieldHistoryFixtureAsync()
    {
        var source = await CurrentFieldFixtureAsync(); var item = source.Data.KnowledgeItems.Single();
        source.SealedItems = []; source.SealedComments = []; source.SealedFiles = []; source.SealedAttachments = []; source.SealedVersions = [];
        source.Data.FileRecords = []; source.Data.Comments = [];
        source.PayloadKeyReferences = source.PayloadKeyReferences!.Where(r => r.OwnerKind == "knowledge" && r.OwnerId == item.Id).ToList();
        var historical = new PortablePlainVersionRecord { Id = Guid.NewGuid(), KnowledgeId = item.Id, OriginalKnowledgeId = item.OriginalItemId!.Value,
            VersionNumber = 1, Title = "Encrypted item", Content = "Exact historical ordinary body — 猫", CreatedAt = DateTime.UtcNow.AddDays(-1), UpdatedAt = DateTime.UtcNow.AddDays(-1),
            SnapshotJson = JsonSerializer.Serialize(new { encryptedFieldsJson = item.EncryptedFieldsJson, sourceMarker = "byte-exact metadata" }) };
        source.PlainVersions = [historical];
        var reference = JsonSerializer.Deserialize<PortablePayloadKeyReference>(JsonSerializer.Serialize(source.PayloadKeyReferences.Single()))!;
        reference.OwnerKind = "version"; reference.OwnerId = historical.Id; reference.VersionId = historical.Id; source.PayloadKeyReferences.Add(reference);
        Assert.Null(EncryptedGraphProfile.Validate(source)); return source;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtectedPlainHistoryExportsItsExactVersionReferencesAndCiphertext(bool staleCompanion)
    {
        var source = await FieldHistoryFixtureAsync(); Assert.True((await _graph.ImportAsync(source, null, "trusted:plain-history", ImportConflictStrategy.Skip, null, default)).Success);
        if (staleCompanion) { (await _db.KnowledgeVersions.SingleAsync()).Title = "PRIVATE_STALE_HISTORY_TITLE"; await _db.SaveChangesAsync(); }
        var result = await _graph.ExportAsync(new() { Type = ExportScopeType.Tenant }, false, default);
        var version = Assert.Single(result.PlainVersions!); Assert.Equal("Encrypted item",version.Title); Assert.DoesNotContain("PRIVATE_STALE_HISTORY_TITLE",JsonSerializer.Serialize(result)); var reference = Assert.Single(result.PayloadKeyReferences!, r => r.OwnerKind == "version");
        Assert.Equal(source.PlainVersions![0].SnapshotJson, version.SnapshotJson); Assert.Equal(source.PlainVersions[0].OriginalKnowledgeId, version.OriginalKnowledgeId);
        Assert.Equal(version.Id, reference.OwnerId); Assert.Equal(version.Id, reference.VersionId); Assert.Equal(version.OriginalKnowledgeId, reference.OriginalItemId);
        Assert.Equal("knowledge:title", DecryptCurrentField(PlainVersionSnapshot.ReadEncryptedFields(version.SnapshotJson)!));
        Assert.Equal(source.PayloadKeyReferences!.Last().Fingerprint, reference.Fingerprint); Assert.Equal(source.PayloadKeyReferences.Last().Generation, reference.Generation);
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("merge")]
    [InlineData("overwrite")]
    public async Task ProtectedPlainHistoryConflictPreviewCountsChangedImmutableFieldBytes(string strategy)
    {
        var source = await FieldHistoryFixtureAsync(); using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var user = new User { TenantId = _tenant, Username = "history-admin", Role = Knowz.Core.Enums.UserRole.Admin, IsActive = true }; _db.Users.Add(user); await _db.SaveChangesAsync();
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant); var adapter = new SelfHostedArchiveImportAdapter(_db,tenant,_graph,new VaultAccessService(_db));
        using var coordinator = new ArchiveImportCoordinator(); var actor = new ArchiveActor(_tenant,user.Id.ToString("D"));
        async Task<ArchiveImportPlan> Plan(string key)
        {
            using var input = new MemoryStream(); var receipt = await KnowzArchiveWriter.WriteAsync(input,[source],null,new() { SigningKey = signer, SourceSnapshot = key }); input.Position = 0;
            return await coordinator.CreatePlanAsync(input,actor,new() { IdempotencyKey = key, Strategy = strategy, ExpectedSignerFingerprint = receipt.SignerFingerprint },adapter);
        }
        var first = await Plan("history-original"); await coordinator.CommitAsync(first.PlanId,actor,adapter);
        var version = source.PlainVersions!.Single(); var sidecar = System.Text.Json.Nodes.JsonNode.Parse(PlainVersionSnapshot.ReadEncryptedFields(version.SnapshotJson)!)!;
        sidecar["fields"]!["title"]!["ciphertext"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); version.SnapshotJson = JsonSerializer.Serialize(new { encryptedFieldsJson = sidecar.ToJsonString() });
        var changed = await Plan("history-changed"); Assert.Equal("partial", changed.PreservationState); Assert.Equal(2, changed.Omissions.Count);
        Assert.Contains(changed.Omissions,o => o.OwnerKind == "version" && o.OwnerId == version.Id && o.Reason.Contains("field"));
        var refused = await Assert.ThrowsAsync<ArchiveOperationException>(() => coordinator.CommitAsync(changed.PlanId,actor,adapter)); Assert.Equal(409,refused.StatusCode);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("body-reference")]
    [InlineData("opaque-body")]
    public async Task ProtectedPlainHistoryNativeHttpListsReadsRestoresAndSnapshotsCurrentBody(string mode)
    {
        var source = await FieldHistoryFixtureAsync(); Assert.True((await _graph.ImportAsync(source, null, "trusted:history-http", ImportConflictStrategy.Skip, null, default)).Success);
        var item = await _db.KnowledgeItems.SingleAsync(); var version = await _db.KnowledgeVersions.SingleAsync(); var vault = (await _db.KnowledgeVaults.SingleAsync()).VaultId;
        var originalBody = item.Content; var originalFields = item.EncryptedFieldsJson;
        var user = new User { TenantId = _tenant, Username = "history-http", IsActive = true, Role = Knowz.Core.Enums.UserRole.Admin }; _db.Users.Add(user); await _db.SaveChangesAsync();
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Services.AddSingleton(_db); builder.Services.AddSingleton(_lifecycle); builder.Services.AddSingleton(_graph);
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant); builder.Services.AddSingleton(new SelfHostedSealedFiles(_db,tenant,_lifecycle,_storage)); builder.Services.AddSingleton<Knowz.SelfHosted.Application.Interfaces.IVaultAccessService>(new VaultAccessService(_db));
        await using var app = builder.Build(); app.Use(async (context,next) => { context.User = new(new System.Security.Claims.ClaimsIdentity([new(System.Security.Claims.ClaimTypes.NameIdentifier,user.Id.ToString()),new("tenantId",_tenant.ToString()),new(System.Security.Claims.ClaimTypes.Role,"Admin")],"fixture")); await next(); });
        Knowz.SelfHosted.API.Endpoints.EncryptedVaultEndpoints.MapEncryptedVaultEndpoints(app); await app.StartAsync(); using var client = app.GetTestClient();
        if (mode != "valid")
        {
            if (mode == "body-reference") (await _db.ShPayloadKeyReferences.SingleAsync(r => r.OwnerKind == "version")).Role = "body";
            else { var dto = JsonSerializer.Deserialize<PortablePlainVersionRecord>(version.PlatformData!,new JsonSerializerOptions(JsonSerializerDefaults.Web))!; dto.SnapshotJson = "{\"isEncrypted\":true}"; version.PlatformData = JsonSerializer.Serialize(dto,new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
            await _db.SaveChangesAsync();
            using var deniedList = await client.GetAsync($"/api/v1/encrypted/items/{item.Id}/versions?vaultId={vault}"); Assert.Equal(HttpStatusCode.Conflict,deniedList.StatusCode);
            using var deniedPayload = await client.GetAsync($"/api/v1/vaults/{vault}/encryption/payloads/version/{version.Id}"); Assert.Equal(HttpStatusCode.Conflict,deniedPayload.StatusCode);
            Assert.Single(await _db.KnowledgeVersions.ToListAsync()); return;
        }
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/v1/encrypted/items/{item.Id}/versions?vaultId={vault}"); Assert.Single(list.EnumerateArray()); Assert.Equal(version.Id,list[0].GetProperty("id").GetGuid());
        var historical = await client.GetFromJsonAsync<JsonElement>($"/api/v1/vaults/{vault}/encryption/payloads/version/{version.Id}"); Assert.Equal(source.PlainVersions![0].SnapshotJson,historical.GetProperty("record").GetProperty("snapshotJson").GetString());
        Assert.Single(historical.GetProperty("payloadKeyReferences").EnumerateArray());
        var current = await client.GetFromJsonAsync<JsonElement>($"/api/v1/vaults/{vault}/encryption/payloads/knowledge/{item.Id}"); Assert.Contains(version.Id,current.GetProperty("related").GetProperty("versions").EnumerateArray().Select(x => x.GetGuid()));
        using var stale = new HttpRequestMessage(HttpMethod.Post,$"/api/v1/encrypted/items/{item.Id}/versions/{version.Id}/restore?vaultId={vault}"); stale.Headers.TryAddWithoutValidation("If-Match","stale"); using var staleResponse = await client.SendAsync(stale); Assert.Equal(HttpStatusCode.Conflict,staleResponse.StatusCode); Assert.Single(await _db.KnowledgeVersions.ToListAsync());
        using var request = new HttpRequestMessage(HttpMethod.Post,$"/api/v1/encrypted/items/{item.Id}/versions/{version.Id}/restore?vaultId={vault}"); request.Headers.TryAddWithoutValidation("If-Match",current.GetProperty("revision").GetString()); using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var actual = await _db.KnowledgeItems.AsNoTracking().SingleAsync(); Assert.Equal(source.PlainVersions[0].Content,actual.Content); Assert.Equal(originalFields,actual.EncryptedFieldsJson); Assert.Equal(2,await _db.KnowledgeVersions.CountAsync());
        var retained = await _db.KnowledgeVersions.SingleAsync(v => v.Id != version.Id); Assert.Equal(originalBody,retained.Content); Assert.Single(await _db.ShPayloadKeyReferences.Where(r => r.OwnerKind == "version" && r.OwnerId == retained.Id).ToListAsync());
        Assert.Null(EncryptedGraphProfile.Validate(await _graph.ExportAsync(new() { Type = ExportScopeType.Tenant },false,default)));
    }

    [Theory]
    [InlineData("missing-reference")]
    [InlineData("body-reference")]
    [InlineData("opaque-body")]
    [InlineData("wrong-subject")]
    [InlineData("inactive")]
    public async Task ProtectedPlainHistoryInvalidRestoreNeverChangesCurrentOrHistory(string defect)
    {
        var source = await FieldHistoryFixtureAsync(); Assert.True((await _graph.ImportAsync(source,null,"trusted:history-negative",ImportConflictStrategy.Skip,null,default)).Success);
        var actor = Substitute.For<IPortableActorProvider>(); actor.ActorId.Returns(Guid.NewGuid()); var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        await using var db = new SelfHostedDbContext((DbContextOptions<SelfHostedDbContext>)_db.GetService<IDbContextOptions>(),tenant,actor);
        var lifecycle = new SelfHostedSealedStore(db,tenant,Microsoft.Extensions.Logging.Abstractions.NullLogger<SelfHostedSealedStore>.Instance);
        var row = await db.KnowledgeItems.SingleAsync(); var version = await db.KnowledgeVersions.SingleAsync();
        var reference = await db.ShPayloadKeyReferences.SingleAsync(r => r.OwnerKind == "version");
        if (defect == "missing-reference") db.ShPayloadKeyReferences.Remove(reference);
        if (defect == "body-reference") reference.Role = "body";
        if (defect is "opaque-body" or "wrong-subject")
        {
            var dto = JsonSerializer.Deserialize<PortablePlainVersionRecord>(version.PlatformData!,new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            if (defect == "opaque-body") dto.SnapshotJson = "{\"isEncrypted\":true}"; else dto.OriginalKnowledgeId = Guid.NewGuid();
            version.PlatformData = JsonSerializer.Serialize(dto,new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        if (defect == "inactive") { row.CreatedByUserId = db.PortableActorId; row.PortableRecoveryState = "dependency-bound"; using var lease = db.BeginInactiveRecoveryImport(); await db.SaveChangesAsync(); }
        else await db.SaveChangesAsync();
        var body = row.Content; var fields = row.EncryptedFieldsJson; var metadata = version.PlatformData; var refs = await db.ShPayloadKeyReferences.CountAsync();
        var revision = SelfHostedSealedStore.PayloadRevision((await lifecycle.GetPlainItemAsync(row.Id,default))!);
        var result = await lifecycle.RestoreVersionAsync(row.Id,version.Id,revision,default);
        Assert.False(result.Ok); Assert.Equal(409,result.StatusCode);
        Assert.Equal(body,(await db.KnowledgeItems.AsNoTracking().SingleAsync()).Content); Assert.Equal(fields,(await db.KnowledgeItems.AsNoTracking().SingleAsync()).EncryptedFieldsJson);
        Assert.Equal(metadata,(await db.KnowledgeVersions.AsNoTracking().SingleAsync()).PlatformData); Assert.Equal(refs,await db.ShPayloadKeyReferences.CountAsync());
    }

    [Fact]
    public async Task ProtectedPlainHistoryOrdinaryVersionCreationPreservesExactSidecarAndReferences()
    {
        var source = await FieldHistoryFixtureAsync(); Assert.True((await _graph.ImportAsync(source,null,"trusted:history-create",ImportConflictStrategy.Skip,null,default)).Success);
        var item = await _db.KnowledgeItems.SingleAsync(); var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var service = new VersioningService(_db,tenant,Microsoft.Extensions.Logging.Abstractions.NullLogger<VersioningService>.Instance);
        var version = await service.CreateVersionAsync(item.Id,null,"Native field snapshot",default);
        var record = SelfHostedSealedStore.ToPlainVersion(version);
        Assert.Equal(item.EncryptedFieldsJson,PlainVersionSnapshot.ReadEncryptedFields(record.SnapshotJson)); Assert.Equal(item.OriginalItemId,record.OriginalKnowledgeId);
        var reference = Assert.Single(await _db.ShPayloadKeyReferences.Where(r => r.OwnerKind == "version" && r.OwnerId == version.Id).ToListAsync());
        Assert.Equal(version.Id,reference.VersionId); Assert.Equal(item.OriginalItemId,reference.OriginalItemId); Assert.Equal("knowledge:title",DecryptCurrentField(PlainVersionSnapshot.ReadEncryptedFields(record.SnapshotJson)!));
    }

    [Fact]
    public async Task ProtectedPlainHistoryLegacyNumericRestoreRequiresGuidAndCurrentRevision()
    {
        var source = await FieldHistoryFixtureAsync(); Assert.True((await _graph.ImportAsync(source,null,"trusted:history-numeric",ImportConflictStrategy.Skip,null,default)).Success);
        var item = await _db.KnowledgeItems.SingleAsync(); var content = item.Content; var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var service = new VersioningService(_db,tenant,Microsoft.Extensions.Logging.Abstractions.NullLogger<VersioningService>.Instance);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreVersionAsync(item.Id,1,null,default));
        Assert.Contains("If-Match",error.Message); Assert.Contains("/api/v1/encrypted/items/",error.Message); Assert.Equal(content,item.Content); Assert.Single(await _db.KnowledgeVersions.ToListAsync());
        var search = Substitute.For<ISearchService>(); var ai = Substitute.For<IOpenAIService>();
        var knowledge = new KnowledgeService(new SelfHostedRepository<Knowledge>(_db),new SelfHostedRepository<Tag>(_db),_db,search,ai,
            new SelfHostedChunkingService(),tenant,Microsoft.Extensions.Logging.Abstractions.NullLogger<KnowledgeService>.Instance);
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Services.AddSingleton(service); builder.Services.AddSingleton(knowledge);
        builder.Services.AddSingleton<Knowz.SelfHosted.Application.Interfaces.IVaultAccessService>(new VaultAccessService(_db));
        await using var app = builder.Build(); app.Use(async (http,next) => { http.User = new(new System.Security.Claims.ClaimsIdentity([new(System.Security.Claims.ClaimTypes.NameIdentifier,Guid.NewGuid().ToString()),new("tenantId",_tenant.ToString()),new(System.Security.Claims.ClaimTypes.Role,"Admin")],"fixture")); await next(); });
        Knowz.SelfHosted.API.Endpoints.VersionEndpoints.MapVersionEndpoints(app); await app.StartAsync(); using var client = app.GetTestClient();
        using var response = await client.PostAsync($"/api/v1/knowledge/{item.Id}/versions/1/restore",null); Assert.Equal(HttpStatusCode.Conflict,response.StatusCode); Assert.Contains("If-Match",await response.Content.ReadAsStringAsync());
        Assert.Empty(ai.ReceivedCalls()); Assert.Empty(search.ReceivedCalls());
    }
}
