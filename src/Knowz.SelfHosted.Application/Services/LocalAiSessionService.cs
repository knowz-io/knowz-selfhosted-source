using System.Security.Cryptography;
using System.Text;
using Knowz.SelfHosted.Infrastructure.Services;

namespace Knowz.SelfHosted.Application.Services;

public sealed record LocalAiOwner(Guid ActorId,Guid TenantId,Guid ItemId,Guid VaultId,string ItemRevision,string PublicStateSha256,string BodyMode,Guid ContextId,string Fingerprint,int Generation);
public sealed record LocalAiChallenge(Guid ChallengeId,DateTimeOffset ExpiresAt,LocalAiConsentBinding ConsentBinding);
public sealed record LocalAiConsentBinding(LocalAiOwner Owner,string ProviderPolicyRevision,string InputSha256,string InstructionSha256,string Operation="amend-body");
public sealed record LocalAiDraft(Guid OperationId,string ItemRevision,string ProviderPolicyRevision,string Content,bool Saved=false);

/// <summary>Process-local only. Restart/another process cannot consume an issued consent.</summary>
public sealed class LocalAiConsentStore(TimeProvider? clock=null,int capacity=256)
{
    private readonly TimeProvider time=clock??TimeProvider.System;
    private readonly object gate=new();
    private readonly Dictionary<Guid,LocalAiChallenge> pending=[];
    private readonly HashSet<(Guid Tenant,Guid Actor)> running=[];
    public LocalAiChallenge Issue(LocalAiConsentBinding binding)
    {
        lock(gate)
        {
            Prune(); var actor=(binding.Owner.TenantId,binding.Owner.ActorId);
            if(running.Contains(actor)) throw new LocalAiException(429,"local-ai-capacity");
            foreach(var old in pending.Where(x=>(x.Value.ConsentBinding.Owner.TenantId,x.Value.ConsentBinding.Owner.ActorId)==actor).Select(x=>x.Key).ToArray()) pending.Remove(old);
            if(pending.Count+running.Count>=capacity) throw new LocalAiException(429,"local-ai-capacity");
            var challenge=new LocalAiChallenge(Guid.NewGuid(),time.GetUtcNow().AddMinutes(5),binding); pending.Add(challenge.ChallengeId,challenge); return challenge;
        }
    }
    public LocalAiChallenge Read(Guid id,Guid actor,Guid tenant)
    {
        lock(gate) { Prune(); if(!pending.TryGetValue(id,out var value) || value.ConsentBinding.Owner.ActorId!=actor || value.ConsentBinding.Owner.TenantId!=tenant) throw new LocalAiException(410,"local-ai-consent-unavailable"); return value; }
    }
    public IDisposable Consume(LocalAiChallenge challenge)
    {
        lock(gate)
        {
            Prune(); var actor=(challenge.ConsentBinding.Owner.TenantId,challenge.ConsentBinding.Owner.ActorId);
            if(!pending.Remove(challenge.ChallengeId,out var found) || found!=challenge || !running.Add(actor)) throw new LocalAiException(410,"local-ai-consent-unavailable");
            return new Lease(()=> { lock(gate) running.Remove(actor); });
        }
    }
    private void Prune() { foreach(var entry in pending.Where(x=>x.Value.ExpiresAt<=time.GetUtcNow()).Select(x=>x.Key).ToArray()) pending.Remove(entry); }
    private sealed class Lease(Action release):IDisposable { private Action? action=release; public void Dispose()=>Interlocked.Exchange(ref action,null)?.Invoke(); }
}

public sealed class LocalAiSessionService(LocalAiPolicy policy,LocalAiConsentStore store,ILocalAiTransport transport)
{
    public LocalAiProviderPolicy Policy=>policy.Read();
    public LocalAiChallenge Issue(LocalAiOwner owner,string inputSha256,string instructionSha256)
    {
        var current=Policy; RequirePolicy(current);
        if(!DigestValid(inputSha256)||!DigestValid(instructionSha256)) throw new LocalAiException(400,"local-ai-invalid");
        return store.Issue(new(owner,current.PolicyRevision!,inputSha256,instructionSha256));
    }
    public LocalAiChallenge Read(Guid id,Guid actor,Guid tenant)=>store.Read(id,actor,tenant);
    public async Task<LocalAiDraft> RunAsync(Guid id,Guid actor,Guid tenant,bool consent,string content,string instruction,Func<CancellationToken,Task<LocalAiOwner>> authorize,CancellationToken ct)
    {
        var challenge=store.Read(id,actor,tenant); var binding=challenge.ConsentBinding;
        if(!consent || content==null || instruction==null || Encoding.UTF8.GetByteCount(content)>LocalAiTransport.MaxInputBytes || Encoding.UTF8.GetByteCount(instruction)>LocalAiTransport.MaxInstructionBytes
            || string.IsNullOrWhiteSpace(instruction) || Hash(content)!=binding.InputSha256 || Hash(instruction)!=binding.InstructionSha256) throw new LocalAiException(400,"local-ai-invalid");
        var current=Policy; RequirePolicy(current);
        if(current.PolicyRevision!=binding.ProviderPolicyRevision || await authorize(ct)!=binding.Owner) throw new LocalAiException(409,"local-ai-stale");
        using var lease=store.Consume(challenge); using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            // Consumption and the final authority fence precede any provider call.
            if(await authorize(deadline.Token)!=binding.Owner || Policy.PolicyRevision!=binding.ProviderPolicyRevision) throw new LocalAiException(409,"local-ai-stale");
            var result=await transport.AmendAsync(current,content,instruction,deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if(result==null || Encoding.UTF8.GetByteCount(result)>LocalAiTransport.MaxOutputBytes) throw new LocalAiException(502,"local-ai-provider-unavailable");
            if(await authorize(deadline.Token)!=binding.Owner || Policy.PolicyRevision!=binding.ProviderPolicyRevision) throw new LocalAiException(409,"local-ai-stale");
            return new(id,binding.Owner.ItemRevision,binding.ProviderPolicyRevision,result);
        }
        catch(LocalAiException) { throw; }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
        catch(Exception ex) when(ex is not OutOfMemoryException) { throw new LocalAiException(502,"local-ai-provider-unavailable"); }
    }
    private static void RequirePolicy(LocalAiProviderPolicy value) { if(!value.Supported) throw new LocalAiException(409,value.Reason??"local-ai-unconfigured"); }
    private static bool DigestValid(string? value)=>value is { Length:64 } && value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Hash(string text)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
