using System.Net;
using System.Security.Cryptography;
using System.Text;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Configuration;

namespace Knowz.SelfHosted.Tests;

public sealed class LocalAiSessionTests
{
    private static Dictionary<string,string?> Settings() => new()
    {
        ["PortableLocalAi:Enabled"]="true", ["OpenAiCompatible:Endpoint"]="http://127.0.0.1:11434/v1/",
        ["OpenAiCompatible:ChatModel"]="synthetic-model", ["PortableLocalAi:AllowedAddresses:0"]="127.0.0.1"
    };
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static LocalAiOwner Owner() => new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"revision", "public-state", "encrypted",Guid.NewGuid(),new string('a',64),2);
    private sealed class Recorder : ILocalAiTransport
    {
        public int Calls; public Func<CancellationToken,Task<string>> Reply = _=>Task.FromResult("generated synthetic result");
        public Task<string> AmendAsync(LocalAiProviderPolicy policy,string content,string instruction,CancellationToken ct) { Calls++; return Reply(ct); }
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now=DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow()=>Now; }
    private static LocalAiChallenge Issue(LocalAiSessionService service,LocalAiOwner owner,string content="private synthetic body",string instruction="Improve") => service.Issue(owner,Hash(content),Hash(instruction));

    [Theory]
    [InlineData("disabled")][InlineData("host-disabled")][InlineData("public-address")][InlineData("missing-address")][InlineData("query")][InlineData("userinfo")]
    public void PolicyRefusesImplicitOrNonLocalProviders(string mode)
    {
        var settings=Settings();
        if(mode=="disabled") settings["PortableLocalAi:Enabled"]="false";
        if(mode=="host-disabled") settings["KNOWZ_AI_DISABLED"]="true";
        if(mode=="public-address") settings["PortableLocalAi:AllowedAddresses:0"]="8.8.8.8";
        if(mode=="missing-address") settings.Remove("PortableLocalAi:AllowedAddresses:0");
        if(mode=="query") settings["OpenAiCompatible:Endpoint"]="http://127.0.0.1/v1/?key=secret";
        if(mode=="userinfo") settings["OpenAiCompatible:Endpoint"]="http://secret@127.0.0.1/v1/";
        Assert.False(new LocalAiPolicy(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()).Read().Supported);
    }
    [Fact]
    public void PolicyBindsCredentialsAndExactModelWithoutExposingCredentials()
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build(); var policy=new LocalAiPolicy(config);
        var first=policy.Read(); Assert.True(first.Supported);
        config["OpenAiCompatible:ApiKey"]="synthetic-do-not-log"; var second=policy.Read();
        Assert.NotEqual(first.PolicyRevision,second.PolicyRevision); Assert.DoesNotContain("synthetic-do-not-log",System.Text.Json.JsonSerializer.Serialize(second.Public));
        config["OpenAiCompatible:ChatModel"]="another-model"; Assert.NotEqual(second.PolicyRevision,policy.Read().PolicyRevision);
    }
    [Theory]
    [InlineData("consent")][InlineData("input")][InlineData("instruction")][InlineData("revision")][InlineData("state")][InlineData("actor")][InlineData("policy")][InlineData("expired")]
    public async Task AlteredConsentBindingNeverDispatches(string change)
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build(); var transport=new Recorder(); var clock=new Clock();
        var service=new LocalAiSessionService(new LocalAiPolicy(config),new LocalAiConsentStore(clock),transport); var owner=Owner(); var challenge=Issue(service,owner);
        var current=change switch { "revision"=>owner with { ItemRevision="stale" },"state"=>owner with { PublicStateSha256="changed" },"actor"=>owner with { ActorId=Guid.NewGuid() },_=>owner };
        if(change=="policy") config["OpenAiCompatible:ChatModel"]="changed";
        if(change=="expired") clock.Now=clock.Now.AddMinutes(6);
        await Assert.ThrowsAsync<LocalAiException>(()=>service.RunAsync(challenge.ChallengeId,current.ActorId,current.TenantId,change!="consent",change=="input"?"private synthetic body ":"private synthetic body",change=="instruction"?"Improve ":"Improve",_=>Task.FromResult(current),default));
        Assert.Equal(0,transport.Calls);
    }
    [Fact]
    public async Task ExactConsentProducesUnsavedDraftAndCannotReplayOrCrossProcess()
    {
        var policy=new LocalAiPolicy(new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build()); var recorder=new Recorder();
        var service=new LocalAiSessionService(policy,new LocalAiConsentStore(),recorder); var owner=Owner(); var challenge=Issue(service,owner);
        var other=new LocalAiSessionService(policy,new LocalAiConsentStore(),recorder);
        await Assert.ThrowsAsync<LocalAiException>(()=>other.RunAsync(challenge.ChallengeId,owner.ActorId,owner.TenantId,true,"private synthetic body","Improve",_=>Task.FromResult(owner),default));
        var reply=await service.RunAsync(challenge.ChallengeId,owner.ActorId,owner.TenantId,true,"private synthetic body","Improve",_=>Task.FromResult(owner),default);
        Assert.Equal("generated synthetic result",reply.Content); Assert.False(reply.Saved); Assert.Equal(owner.ItemRevision,reply.ItemRevision);
        await Assert.ThrowsAsync<LocalAiException>(()=>service.RunAsync(challenge.ChallengeId,owner.ActorId,owner.TenantId,true,"private synthetic body","Improve",_=>Task.FromResult(owner),default)); Assert.Equal(1,recorder.Calls);
    }
    [Fact]
    public async Task ConcurrentAcceptAndActorOperationDispatchExactlyOnce()
    {
        var release=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); var called=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder=new Recorder { Reply=ct=> { called.SetResult(); return release.Task.WaitAsync(ct); } };
        var service=new LocalAiSessionService(new LocalAiPolicy(new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build()),new LocalAiConsentStore(),recorder); var owner=Owner(); var challenge=Issue(service,owner);
        var first=service.RunAsync(challenge.ChallengeId,owner.ActorId,owner.TenantId,true,"private synthetic body","Improve",_=>Task.FromResult(owner),default); await called.Task;
        await Assert.ThrowsAsync<LocalAiException>(()=>service.RunAsync(challenge.ChallengeId,owner.ActorId,owner.TenantId,true,"private synthetic body","Improve",_=>Task.FromResult(owner),default));
        Assert.Throws<LocalAiException>(()=>Issue(service,owner)); release.SetResult("result"); await first; Assert.Equal(1,recorder.Calls);
    }
    [Theory]
    [InlineData("revoked")][InlineData("changed")][InlineData("oversize")][InlineData("cancel")][InlineData("provider-error")]
    public async Task LateFailureNeverPublishesOrRetries(string mode)
    {
        var owner=Owner(); var current=owner; using var cancellation=new CancellationTokenSource();
        var transport=new Recorder { Reply=ct=> { if(mode=="revoked") throw new LocalAiException(403,"local-ai-denied"); if(mode=="provider-error") throw new Exception("PRIVATE_PROVIDER_BODY"); if(mode=="changed") current=owner with { PublicStateSha256="changed" }; if(mode=="cancel") cancellation.Cancel(); return Task.FromResult(mode=="oversize"?new string('x',65537):"private generated result"); } };
        var service=new LocalAiSessionService(new LocalAiPolicy(new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build()),new LocalAiConsentStore(),transport); var challenge=Issue(service,owner);
        var error=await Assert.ThrowsAnyAsync<Exception>(()=>service.RunAsync(challenge.ChallengeId,owner.ActorId,owner.TenantId,true,"private synthetic body","Improve",_=>Task.FromResult(current),cancellation.Token));
        Assert.DoesNotContain("PRIVATE_PROVIDER_BODY",error.ToString()); Assert.Equal(1,transport.Calls);
    }
    [Fact]
    public void CapacityIsBoundedAndExpiredSlotsAreReclaimed()
    {
        var clock=new Clock(); var service=new LocalAiSessionService(new LocalAiPolicy(new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build()),new LocalAiConsentStore(clock,2),new Recorder());
        Issue(service,Owner()); Issue(service,Owner()); Assert.Throws<LocalAiException>(()=>Issue(service,Owner())); clock.Now=clock.Now.AddMinutes(6); Issue(service,Owner());
    }
    [Fact]
    public async Task Utf8ByteLimitsAndUnicodeIdentityAreExact()
    {
        var transport=new Recorder(); var service=new LocalAiSessionService(new LocalAiPolicy(new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build()),new LocalAiConsentStore(),transport); var owner=Owner();
        var challenge=Issue(service,owner,"é");
        await Assert.ThrowsAsync<LocalAiException>(()=>service.RunAsync(challenge.ChallengeId,owner.ActorId,owner.TenantId,true,"e\u0301","Improve",_=>Task.FromResult(owner),default));
        challenge=Issue(service,owner,new string('é',32769));
        await Assert.ThrowsAsync<LocalAiException>(()=>service.RunAsync(challenge.ChallengeId,owner.ActorId,owner.TenantId,true,new string('é',32769),"Improve",_=>Task.FromResult(owner),default)); Assert.Equal(0,transport.Calls);
    }
}
