using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.API.Endpoints;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSec.Cryptography;
using NSubstitute;
namespace Knowz.SelfHosted.Tests;
public sealed partial class SelfHostedGraphStoreTests
{
    [Theory]
    [InlineData("body-only-retired", 200)]
    [InlineData("changed-active", 200)]
    [InlineData("null-companion", 200)]
    [InlineData("changed-retired", 400)]
    [InlineData("missing-fields", 400)]
    [InlineData("wrong-subject", 400)]
    [InlineData("mixed-body", 400)]
    [InlineData("stale", 409)]
    [InlineData("wrong-vault", 400)]
    [InlineData("foreign-reference", 400)]
    [InlineData("plaintext-companion", 400)]
    [InlineData("null-vaults", 400)]
    [InlineData("null-refs", 400)]
    [InlineData("missing-ref", 400)]
    [InlineData("body-prop", 400)]
    [InlineData("legacy-body-prop", 400)]
    [InlineData("null-legacy-body-prop", 400)]
    [InlineData("missing-subject", 400)]
    [InlineData("numeric-subject", 400)]
    [InlineData("null-content", 400)]
    [InlineData("drop-field", 400)]
    public async Task CurrentProtectedFieldHttpEditRetainsBodyModeHistoryAndExactKeys(string mode, int expected)
    {
        var f = await StartCurrentFieldWriteAppAsync(); await using var app = f.App; using var client = f.Client;
        var before = await client.GetFromJsonAsync<JsonObject>($"/api/v1/vaults/{f.Vault}/encryption/payloads/knowledge/{f.Item}");
        var record = before!["record"]!.DeepClone().AsObject(); var refs = before["payloadKeyReferences"]!.DeepClone();
        var oldFields = record["encryptedFieldsJson"]!.GetValue<string>(); var oldBody = record["content"]!.GetValue<string>();
        var original = record["originalItemId"]!.GetValue<Guid>();
        record["content"] = "Edited ordinary body — 猫";
        if (mode.StartsWith("changed-")) record["encryptedFieldsJson"] = EncryptCurrentTitle("Changed protected current title", (await _db.ShCryptoKeyIdentities.SingleAsync()).PublicKey);
        if (mode.EndsWith("retired")) { (await _db.ShCryptoKeyIdentities.SingleAsync()).SourceLifecycle = "retired"; await _db.SaveChangesAsync(); }
        if (mode == "missing-fields") record.Remove("encryptedFieldsJson");
        if (mode == "wrong-subject") record["originalItemId"] = Guid.NewGuid();
        if (mode == "mixed-body") record["ciphertext"] = Convert.ToBase64String(new byte[17]);
        if (mode == "foreign-reference") refs![0]!["ownerId"] = Guid.NewGuid();
        if (mode == "plaintext-companion") record["title"] = "MUST_NOT_LEAK";
        if (mode == "null-companion") record["title"] = null;
        if (mode == "null-vaults") record["vaultIds"] = null;
        if (mode == "null-refs") refs = null;
        if (mode == "missing-ref") refs!.AsArray().Clear();
        if (mode == "body-prop") record["encryptedDek"] = "forbidden";
        if (mode == "legacy-body-prop") record["wrappedDek"] = "forbidden";
        if (mode == "null-legacy-body-prop") record["wrappedDek"] = null;
        if (mode == "missing-subject") record.Remove("originalItemId");
        if (mode == "numeric-subject") record["originalItemId"] = 123;
        if (mode == "null-content") record["content"] = null;
        if (mode == "drop-field") { record["encryptedFieldsJson"] = "{\"schema\":\"knowz-encrypted-fields-v1\",\"fields\":{}}"; refs!.AsArray().Clear(); }
        using var request = new HttpRequestMessage(HttpMethod.Put,$"/api/v1/encrypted/items/{f.Item}") { Content = JsonContent.Create(new { vaultId = mode == "wrong-vault" ? Guid.NewGuid() : f.Vault, record, payloadKeyReferences = refs }) };
        request.Headers.TryAddWithoutValidation("If-Match",mode == "stale" ? "stale" : before["revision"]!.GetValue<string>());
        using var response = await client.SendAsync(request); Assert.Equal(expected,(int)response.StatusCode);
        var row = await _db.KnowledgeItems.AsNoTracking().SingleAsync();
        if (expected != 200) { Assert.Equal(oldBody,row.Content); Assert.Equal(oldFields,row.EncryptedFieldsJson); Assert.Single(await _db.KnowledgeVersions.ToListAsync()); return; }
        Assert.Equal("Encrypted item",row.Title); Assert.Equal("Edited ordinary body — 猫",row.Content); Assert.Equal(original,row.OriginalItemId); Assert.Empty(await _db.ShSealedKnowledge.ToListAsync());
        Assert.Equal(mode == "changed-active" ? "Changed protected current title" : "knowledge:title",DecryptCurrentField(row.EncryptedFieldsJson!));
        Assert.Equal(2,await _db.KnowledgeVersions.CountAsync());
        var snapshot = await _db.KnowledgeVersions.OrderByDescending(v => v.VersionNumber).FirstAsync(); var dto = SelfHostedSealedStore.ToPlainVersion(snapshot);
        Assert.Equal(oldBody,dto.Content); Assert.Equal(oldFields,PlainVersionSnapshot.ReadEncryptedFields(dto.SnapshotJson));
        var historyRef = Assert.Single(await _db.ShPayloadKeyReferences.Where(r => r.OwnerKind == "version" && r.OwnerId == snapshot.Id).ToListAsync()); Assert.Equal(original,historyRef.OriginalItemId);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("Edited ordinary body — 猫",payload.GetProperty("record").GetProperty("content").GetString());
        var exported = await _graph.ExportAsync(new() { Type = ExportScopeType.Tenant },false,default); Assert.Null(EncryptedGraphProfile.Validate(exported));
        Assert.Equal(row.EncryptedFieldsJson,exported.Data.KnowledgeItems.Single().EncryptedFieldsJson);
    }

    [Theory]
    [InlineData("changed-active",200)]
    [InlineData("unchanged-retired",200)]
    [InlineData("relabel",400)]
    [InlineData("changed-retired",400)]
    [InlineData("wrong-subject",400)]
    [InlineData("wrong-parent",400)]
    [InlineData("foreign-ref",400)]
    [InlineData("stale",409)]
    public async Task CurrentProtectedFieldParentAcceptsEncryptedCommentWithExactOriginalSubject(string mode,int expected)
    {
        var f = await StartCurrentFieldWriteAppAsync(); await using var app = f.App; using var client = f.Client;
        var item = await _lifecycle.GetPlainItemAsync(f.Item,default); var original = item!.OriginalItemId!.Value;
        var currentRef = await _db.ShPayloadKeyReferences.AsNoTracking().SingleAsync(r => r.OwnerKind == "knowledge");
        var reference = (PortablePayloadKeyReference)PortablePublicProjection.Project<PortablePayloadKeyReference>(currentRef)!;
        var id = Guid.NewGuid(); reference.OwnerKind = "comment"; reference.OwnerId = id; reference.OriginalItemId = id; reference.FieldName = "content";
        var publicKey = (await _db.ShCryptoKeyIdentities.SingleAsync()).PublicKey;
        var comment = new SealedCommentRecord { Id=id,OriginalCommentId=id,KnowledgeId=f.Item,OriginalKnowledgeId=original,EncryptedFieldsJson=EncryptCurrentComment("Encrypted comment on ordinary owner",publicKey) };
        var oldFields = comment.EncryptedFieldsJson;
        var catalog = await _lifecycle.GetCatalogAsync(f.Vault,default);
        using var create = new HttpRequestMessage(HttpMethod.Post,$"/api/v1/encrypted/items/{f.Item}/comments") { Content = JsonContent.Create(new { vaultId=f.Vault,record=comment,payloadKeyReferences=new[]{reference} }) };
        create.Headers.TryAddWithoutValidation("If-Match",catalog!.Revision);
        using var created = await client.SendAsync(create); Assert.Equal(HttpStatusCode.Created,created.StatusCode);
        var result = await created.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(original,result.GetProperty("record").GetProperty("originalKnowledgeId").GetGuid());
        if (mode is not ("unchanged-retired" or "relabel")) comment.EncryptedFieldsJson = EncryptCurrentComment("Updated protected comment",publicKey);
        if (mode.EndsWith("retired")) { (await _db.ShCryptoKeyIdentities.SingleAsync()).SourceLifecycle = "retired"; await _db.SaveChangesAsync(); }
        if (mode == "relabel")
        {
            var originalKey=await _db.ShCryptoKeyIdentities.SingleAsync(); originalKey.SourceLifecycle="retired";
            var next=JsonSerializer.Deserialize<Knowz.SelfHosted.Infrastructure.Data.Entities.ShCryptoKeyIdentity>(JsonSerializer.Serialize(originalKey))!;
            next.Id=Guid.NewGuid(); next.Generation++; next.SourceLifecycle="active"; _db.ShCryptoKeyIdentities.Add(next);
            foreach (var storedRef in await _db.ShPayloadKeyReferences.ToListAsync()) storedRef.Generation=originalKey.Generation;
            await _db.SaveChangesAsync(); var fresh=(await _lifecycle.GetCatalogAsync(f.Vault,default))!;
            Assert.True((await _lifecycle.SelectPrimaryAsync(f.Vault,next.PortableCryptoContextId,next.Fingerprint,fresh.Revision,default,next.Generation)).Ok);
            reference.Generation=next.Generation;
        }
        if (mode == "wrong-subject") comment.OriginalKnowledgeId = Guid.NewGuid();
        if (mode == "wrong-parent") comment.KnowledgeId = Guid.NewGuid();
        if (mode == "foreign-ref") reference.OwnerId = Guid.NewGuid();
        using var update = new HttpRequestMessage(HttpMethod.Put,$"/api/v1/encrypted/comments/{id}") { Content=JsonContent.Create(new { vaultId=f.Vault,record=comment,payloadKeyReferences=new[]{reference} }) };
        update.Headers.TryAddWithoutValidation("If-Match",mode == "stale" ? "stale" : result.GetProperty("revision").GetString());
        using var updated = await client.SendAsync(update); Assert.Equal(expected,(int)updated.StatusCode);
        var row = await _db.ShSealedComments.AsNoTracking().SingleAsync(); Assert.Equal(expected == 200 ? comment.EncryptedFieldsJson : oldFields,row.EncryptedFieldsJson);
        Assert.Equal(f.Item,row.KnowledgeId); Assert.Equal(original,row.OriginalKnowledgeId);
        Assert.Null(EncryptedGraphProfile.Validate(await _graph.ExportAsync(new() { Type=ExportScopeType.Tenant },false,default)));
    }

    [Theory]
    [InlineData("mixed",200)]
    [InlineData("semantic-reserialization",200)]
    [InlineData("relabel",400)]
    public async Task CurrentProtectedFieldEditAllowsUnchangedRetiredFieldAlongsideNewActiveGeneration(string mode,int expected)
    {
        var f = await StartCurrentFieldWriteAppAsync(); await using var app = f.App; using var client = f.Client;
        var before = (await client.GetFromJsonAsync<JsonObject>($"/api/v1/vaults/{f.Vault}/encryption/payloads/knowledge/{f.Item}"))!;
        var original = await _db.ShCryptoKeyIdentities.SingleAsync(); original.SourceLifecycle = "retired";
        var next = JsonSerializer.Deserialize<Knowz.SelfHosted.Infrastructure.Data.Entities.ShCryptoKeyIdentity>(JsonSerializer.Serialize(original))!;
        next.Id=Guid.NewGuid(); next.Generation=original.Generation+1; next.SourceLifecycle="active"; _db.ShCryptoKeyIdentities.Add(next);
        foreach (var reference in await _db.ShPayloadKeyReferences.ToListAsync()) reference.Generation=original.Generation;
        await _db.SaveChangesAsync();
        var catalog=(await _lifecycle.GetCatalogAsync(f.Vault,default))!;
        Assert.True((await _lifecycle.SelectPrimaryAsync(f.Vault,next.PortableCryptoContextId,next.Fingerprint,catalog.Revision,default,next.Generation)).Ok);
        var record=before["record"]!.DeepClone().AsObject(); var fields=JsonNode.Parse(record["encryptedFieldsJson"]!.GetValue<string>())!;
        var oldTitle=fields["fields"]!["title"]!.DeepClone();
        fields["fields"]!["summary"]=JsonNode.Parse(EncryptCurrentTitle("New active-generation summary",next.PublicKey))!["fields"]!["title"]!.DeepClone();
        if (mode == "semantic-reserialization") fields["fields"]!["title"]=new JsonObject(oldTitle.AsObject().Reverse().Select(p=>new KeyValuePair<string,JsonNode?>(p.Key,p.Value!.DeepClone())));
        record["encryptedFieldsJson"]=fields.ToJsonString(); record["summary"]=null;
        var references=before["payloadKeyReferences"]!.DeepClone().AsArray(); references[0]!["generation"]=original.Generation;
        if (mode == "relabel") references[0]!["generation"]=next.Generation;
        var summary=references[0]!.DeepClone(); summary["fieldName"]="summary"; summary["generation"]=next.Generation; references.Add(summary);
        using var request=new HttpRequestMessage(HttpMethod.Put,$"/api/v1/encrypted/items/{f.Item}") { Content=JsonContent.Create(new { vaultId=f.Vault,record,payloadKeyReferences=references }) };
        request.Headers.TryAddWithoutValidation("If-Match",before["revision"]!.GetValue<string>());
        using var response=await client.SendAsync(request); Assert.Equal(expected,(int)response.StatusCode);
        if (expected != 200) { Assert.Equal(before["record"]!["encryptedFieldsJson"]!.GetValue<string>(),(await _db.KnowledgeItems.AsNoTracking().SingleAsync()).EncryptedFieldsJson); Assert.Single(await _db.KnowledgeVersions.ToListAsync()); return; }
        var stored=await _db.KnowledgeItems.AsNoTracking().SingleAsync(); var returned=JsonNode.Parse(stored.EncryptedFieldsJson!)!;
        Assert.True(JsonNode.DeepEquals(oldTitle,returned["fields"]!["title"])); Assert.Equal("knowledge:title",DecryptCurrentField(stored.EncryptedFieldsJson!));
        var summaryOnly=new JsonObject { ["fields"]=new JsonObject { ["title"]=returned["fields"]!["summary"]!.DeepClone() } };
        Assert.Equal("New active-generation summary",DecryptCurrentField(summaryOnly.ToJsonString()));
        Assert.Equal(2,await _db.ShPayloadKeyReferences.CountAsync(r=>r.OwnerKind=="knowledge"));
        Assert.Null(EncryptedGraphProfile.Validate(await _graph.ExportAsync(new() { Type=ExportScopeType.Tenant },false,default)));
    }

    private static string EncryptCurrentComment(string text,string publicKey)
    {
        var sidecar = JsonNode.Parse(EncryptCurrentTitle(text,publicKey))!.AsObject();
        sidecar["fields"]!["content"] = sidecar["fields"]!["title"]!.DeepClone(); sidecar["fields"]!.AsObject().Remove("title"); return sidecar.ToJsonString();
    }

    private async Task<(WebApplication App,HttpClient Client,Guid Item,Guid Vault)> StartCurrentFieldWriteAppAsync(Action<WebApplicationBuilder>? configure = null)
    {
        var source = await FieldHistoryFixtureAsync(); Assert.True((await _graph.ImportAsync(source,null,"trusted:current-write",ImportConflictStrategy.Skip,null,default)).Success);
        var item = await _db.KnowledgeItems.SingleAsync(); var vault = (await _db.KnowledgeVaults.SingleAsync()).VaultId;
        var user = new User { TenantId=_tenant,Username="current-write",IsActive=true,Role=Knowz.Core.Enums.UserRole.Admin }; _db.Users.Add(user); await _db.SaveChangesAsync();
        var catalog = await _lifecycle.GetCatalogAsync(vault,default); var key = Assert.Single(catalog!.Keys);
        Assert.True((await _lifecycle.SelectPrimaryAsync(vault,key.ContextId,key.Fingerprint,catalog.Revision,default,key.Generation)).Ok);
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Services.AddSingleton(_db); builder.Services.AddSingleton(_lifecycle); builder.Services.AddSingleton(_graph);
        builder.Services.AddSingleton(new SelfHostedSealedFiles(_db,tenant,_lifecycle,_storage)); builder.Services.AddSingleton<IVaultAccessService>(new VaultAccessService(_db));
        configure?.Invoke(builder);
        var app = builder.Build(); app.Use(async (http,next) => { http.User = new(new System.Security.Claims.ClaimsIdentity([new(System.Security.Claims.ClaimTypes.NameIdentifier,user.Id.ToString()),new("tenantId",_tenant.ToString()),new(System.Security.Claims.ClaimTypes.Role,"Admin")],"fixture")); await next(); });
        EncryptedVaultEndpoints.MapEncryptedVaultEndpoints(app); await app.StartAsync(); return (app,app.GetTestClient(),item.Id,vault);
    }
    private static string EncryptCurrentTitle(string text,string publicKey)
    {
        using var ephemeral=Key.Create(KeyAgreementAlgorithm.X25519); var ep=ephemeral.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        using var shared=KeyAgreementAlgorithm.X25519.Agree(ephemeral,PublicKey.Import(KeyAgreementAlgorithm.X25519,Convert.FromBase64String(publicKey),KeyBlobFormat.RawPublicKey))!;
        var wrapping=KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(shared,ep,"knowz-dek-wrap-v1"u8.ToArray(),32); var dek=RandomNumberGenerator.GetBytes(32); var nonce=RandomNumberGenerator.GetBytes(12); var dn=RandomNumberGenerator.GetBytes(12);
        var wrapped=new byte[48]; using(var aes=new AesGcm(wrapping,16)) aes.Encrypt(dn,dek,wrapped.AsSpan(0,32),wrapped.AsSpan(32));
        var plain=Encoding.UTF8.GetBytes(text); var cipher=new byte[plain.Length+16]; using(var aes=new AesGcm(dek,16)) aes.Encrypt(nonce,plain,cipher.AsSpan(0,plain.Length),cipher.AsSpan(plain.Length));
        return JsonSerializer.Serialize(new { schema="knowz-encrypted-fields-v1",fields=new { title=new { ciphertext=Convert.ToBase64String(cipher),encryptedDek=Convert.ToBase64String(wrapped),dekNonce=Convert.ToBase64String(dn),ephemeralPublicKey=Convert.ToBase64String(ep),contentNonce=Convert.ToBase64String(nonce) } } });
    }
}
