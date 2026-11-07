using System.Net;
using System.Security.Cryptography;
using System.Text;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NSubstitute;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;

namespace Knowz.SelfHosted.Tests;

public sealed partial class SelfHostedGraphStoreTests
{
    [Fact]
    public async Task LocalAiPolicyTruthfullyReportsDisabledForAuthorizedProtectedOwner()
    {
        var fixture = await StartCurrentFieldWriteAppAsync();
        await using var app = fixture.App; using var client = fixture.Client;
        using var response = await client.GetAsync($"/api/v1/encrypted/items/{fixture.Item}/ai-session-policy?vaultId={fixture.Vault}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var policy = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(policy.GetProperty("supported").GetBoolean());
        Assert.Equal("local-ai-disabled", policy.GetProperty("reason").GetString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var foreign = await client.GetAsync($"/api/v1/encrypted/items/{fixture.Item}/ai-session-policy?vaultId={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
    }

    private sealed class HttpLocalModel : ILocalAiTransport
    {
        public int Calls; public Func<Task>? DuringCall;
        public async Task<string> AmendAsync(LocalAiProviderPolicy policy,string content,string instruction,CancellationToken ct) { Calls++;if(DuringCall!=null)await DuringCall();return "PRIVATE_GENERATED_RESULT"; }
    }
    private static string AiDigest(string text)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private async Task<(Microsoft.AspNetCore.Builder.WebApplication App,HttpClient Client,Guid Item,Guid Vault,HttpLocalModel Model)> StartAiAsync()
    {
        var model=new HttpLocalModel();
        var fixture=await StartCurrentFieldWriteAppAsync(builder=>
        {
            builder.Configuration["PortableLocalAi:Enabled"]="true";builder.Configuration["OpenAiCompatible:Endpoint"]="http://127.0.0.1:11434/v1/";builder.Configuration["OpenAiCompatible:ChatModel"]="synthetic-model";builder.Configuration["PortableLocalAi:AllowedAddresses:0"]="127.0.0.1";
            builder.Services.AddSingleton<LocalAiPolicy>();builder.Services.AddSingleton<LocalAiConsentStore>();builder.Services.AddSingleton<ILocalAiTransport>(model);builder.Services.AddScoped<LocalAiSessionService>();
        });return(fixture.App,fixture.Client,fixture.Item,fixture.Vault,model);
    }
    private async Task<(Guid Challenge,string Revision)> AiChallengeAsync(HttpClient client,Guid item,Guid vault)
    {
        using var policy=await client.GetAsync($"/api/v1/encrypted/items/{item}/ai-session-policy?vaultId={vault}");Assert.Equal(HttpStatusCode.OK,policy.StatusCode);
        var descriptor=await policy.Content.ReadFromJsonAsync<JsonElement>();Assert.True(descriptor.GetProperty("supported").GetBoolean());
        var revision=descriptor.GetProperty("itemRevision").GetString()!;var catalog=(await _lifecycle.GetCatalogAsync(vault,default))!;var key=catalog.Keys.Single();
        using var request=new HttpRequestMessage(HttpMethod.Post,$"/api/v1/encrypted/items/{item}/ai-session-challenges") { Content=JsonContent.Create(new { vaultId=vault,operation="amend-body",contextId=key.ContextId,fingerprint=key.Fingerprint,generation=key.Generation,providerPolicyRevision=descriptor.GetProperty("provider").GetProperty("policyRevision").GetString(),inputSha256=AiDigest("PRIVATE_INPUT"),instructionSha256=AiDigest("Improve") }) };
        request.Headers.TryAddWithoutValidation("If-Match",revision);using var response=await client.SendAsync(request);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var challenge=await response.Content.ReadFromJsonAsync<JsonElement>();return(challenge.GetProperty("challengeId").GetGuid(),revision);
    }
    private static Task<HttpResponseMessage> AiRunAsync(HttpClient client,Guid item,Guid vault,Guid challenge,string revision,bool consent=true)
    {
        var request=new HttpRequestMessage(HttpMethod.Post,$"/api/v1/encrypted/items/{item}/ai-sessions") { Content=JsonContent.Create(new { vaultId=vault,challengeId=challenge,consent,content="PRIVATE_INPUT",instruction="Improve" }) };
        request.Headers.TryAddWithoutValidation("If-Match",revision);return client.SendAsync(request);
    }
    [Fact]
    public async Task LocalAiHttpExactConsentReturnsAnUnsavedDraftOnceWithoutChangingData()
    {
        var f=await StartAiAsync();await using var app=f.App;using var client=f.Client;
        var before=(await _db.KnowledgeItems.AsNoTracking().SingleAsync()).Content;var history=await _db.KnowledgeVersions.CountAsync();var refs=await _db.ShPayloadKeyReferences.CountAsync();
        var c=await AiChallengeAsync(client,f.Item,f.Vault);using var response=await AiRunAsync(client,f.Item,f.Vault,c.Challenge,c.Revision);
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.Equal("no-store",response.Headers.CacheControl?.ToString());var reply=await response.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal("PRIVATE_GENERATED_RESULT",reply.GetProperty("content").GetString());Assert.False(reply.GetProperty("saved").GetBoolean());
        Assert.Equal(before,(await _db.KnowledgeItems.AsNoTracking().SingleAsync()).Content);Assert.Equal(history,await _db.KnowledgeVersions.CountAsync());Assert.Equal(refs,await _db.ShPayloadKeyReferences.CountAsync());
        using var replay=await AiRunAsync(client,f.Item,f.Vault,c.Challenge,c.Revision);Assert.Equal(HttpStatusCode.Gone,replay.StatusCode);Assert.Equal(1,f.Model.Calls);
    }
    [Theory]
    [InlineData("actor",false)][InlineData("actor",true)][InlineData("tenant",false)][InlineData("tenant",true)][InlineData("membership",false)][InlineData("membership",true)]
    [InlineData("owner",false)][InlineData("owner",true)][InlineData("revision",false)][InlineData("revision",true)][InlineData("key",false)][InlineData("key",true)][InlineData("inactive",false)][InlineData("inactive",true)]
    public async Task LocalAiHttpRechecksAuthorityAndRevisionBeforeDispatchAndBeforeResponse(string change,bool duringCall)
    {
        var f=await StartAiAsync();await using var app=f.App;using var client=f.Client;var c=await AiChallengeAsync(client,f.Item,f.Vault);
        async Task Change()
        {
            if(change=="actor") (await _db.Users.SingleAsync()).IsActive=false;
            if(change=="tenant") (await _db.Tenants.SingleAsync()).IsActive=false;
            if(change=="membership") {var user=await _db.Users.SingleAsync();_db.UserTenantMemberships.Add(new UserTenantMembership {UserId=user.Id,TenantId=_tenant,IsActive=false,Role=user.Role});}
            if(change=="owner") _db.KnowledgeVaults.Remove(await _db.KnowledgeVaults.SingleAsync());
            if(change=="revision") (await _db.KnowledgeItems.SingleAsync()).Content="another current revision";
            if(change=="inactive")
            {
                var actor=Substitute.For<IPortableActorProvider>();actor.ActorId.Returns((await _db.Users.SingleAsync()).Id);
                var tenant=Substitute.For<ITenantProvider>();tenant.TenantId.Returns(_tenant);
                await using var importing=new SelfHostedDbContext((DbContextOptions<SelfHostedDbContext>)_db.GetService<IDbContextOptions>(),tenant,actor);
                using var lease=importing.BeginInactiveRecoveryImport();(await importing.KnowledgeItems.SingleAsync()).PortableRecoveryState="dependency-bound";
                await importing.SaveChangesAsync();return;
            }
            if(change=="key") (await _db.ShCryptoKeyIdentities.SingleAsync()).SourceLifecycle="retired";
            await _db.SaveChangesAsync();
        }
        if(duringCall) f.Model.DuringCall=Change;else await Change();
        using var response=await AiRunAsync(client,f.Item,f.Vault,c.Challenge,c.Revision);Assert.Contains(response.StatusCode,new[]{HttpStatusCode.Forbidden,HttpStatusCode.Conflict});Assert.DoesNotContain("PRIVATE_GENERATED_RESULT",await response.Content.ReadAsStringAsync());Assert.Equal(duringCall?1:0,f.Model.Calls);
    }
}
