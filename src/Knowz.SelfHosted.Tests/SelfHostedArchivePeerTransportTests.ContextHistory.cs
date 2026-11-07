using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Knowz.SelfHosted.Infrastructure.Data;
using Npgsql;
using NSec.Cryptography;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Application.Validators;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed partial class SelfHostedGraphStoreTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task SavedPeerReturnReusesExactDestinationContextForUnchangedRecoverableHistory(bool ordinaryHistory, bool preserveLocalSelection)
        => ContextHistoryReturnAsync(ordinaryHistory,preserveLocalSelection);

    [Fact]
    public Task SavedPeerReturnExtendsAnAnchoredGenerationWithoutChangingHistoryOrPrimarySelection()
        => ContextHistoryReturnAsync(true,true,extendGeneration:true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SavedPeerReturnUsesImmutableHistoryToDistinguishExactDuplicateCatalogs(bool ordinaryHistory)
        => ContextHistoryReturnAsync(ordinaryHistory,false,duplicateHistory:true);

    [Fact]
    public Task SavedPeerPreservesUnusedKeylessContextMetadataWithoutInventingKeyAttribution()
        => ContextHistoryReturnAsync(true,false,includeUnusedContext:true);

    [Fact]
    public Task SavedPeerReturnCannotPromoteLocallyRetiredGeneration()
        => ContextHistoryReturnAsync(false,false,keepLocalRetired:true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SavedPeerReturnRefusesDifferentImmutableHistoryCiphertext(bool ordinaryHistory)
        => ContextHistoryReturnAsync(ordinaryHistory,false,changedHistory:true);

    private async Task ContextHistoryReturnAsync(bool ordinaryHistory,bool preserveLocalSelection,bool extendGeneration=false, string? localConnection=null, string? remoteConnection=null, ContextTopologyFault? fault=null, bool keepLocalRetired=false, bool changedHistory=false, bool duplicateHistory=false, bool includeUnusedContext=false)
    {
        await using var local = new PeerSide(fault,localConnection); await using var remote = new PeerSide(postgresConnection:remoteConnection);
        var source = ordinaryHistory ? await FieldHistoryFixtureAsync() : await CurrentFieldFixtureAsync();
        if(duplicateHistory) source=PortableGraphPackages.Combine([source,SecondImmutableHistoryCopy(source)]);
        if(includeUnusedContext) {var id=Guid.NewGuid();source.CryptoContexts!.Add(new(){ContextId=id,OriginalBindingId=Guid.NewGuid()});source.VaultCryptoContextLinks!.Add(new(){VaultId=source.Data.Vaults.Single().Id,ContextId=id});}
        Assert.Null(EncryptedGraphProfile.Validate(source));
        var imported = await local.Db.Database.CreateExecutionStrategy().ExecuteAsync(()=>local.Graph.ImportAsync(source,local.Vault.Id,"trusted:original-owned-history",ImportConflictStrategy.Overwrite,null,default));
        Assert.True(imported.Success,imported.Error); Assert.NotEmpty(await local.Db.ShPayloadKeyReferences.Where(r=>r.OwnerKind=="version").ToListAsync());
        var originalRefs = await local.Db.ShPayloadKeyReferences.AsNoTracking().Where(r=>r.OwnerKind=="version").ToListAsync();
        var originalContexts = originalRefs.Select(r=>r.ContextId).Distinct().ToArray();
        var originalSealedHistory=(await local.Db.ShSealedVersions.AsNoTracking().OrderBy(v=>v.Id).ToListAsync()).Select(v=>JsonSerializer.Serialize(v)).ToArray();
        var originalPlainHistory=(await local.Db.KnowledgeVersions.AsNoTracking().OrderBy(v=>v.Id).ToListAsync()).Select(v=>JsonSerializer.Serialize(v)).ToArray();
        await remote.StartAsync(); var connection = new PlatformConnection { TenantId=local.Tenant.TenantId,PlatformApiUrl="https://remote.example",RemoteTenantId=remote.Tenant.TenantId,
            ApiKeyProtected=local.Protection.CreateProtector(PlatformConnectionService.MasterPurpose).CreateProtector($"{PlatformConnectionService.MasterPurpose}.{local.Tenant.TenantId}").Protect(remote.User.ApiKey!) };
        local.Db.PlatformConnections.Add(connection); var link=new VaultSyncLink { LocalVaultId=local.Vault.Id,RemoteTenantId=remote.Tenant.TenantId,RemoteVaultId=remote.Vault.Id,PlatformConnectionId=connection.Id,SyncEnabled=true }; local.Db.VaultSyncLinks.Add(link);await local.Db.SaveChangesAsync();
        var factory=Substitute.For<IHttpClientFactory>();factory.CreateClient(PlatformSyncCredentialResolver.HttpClientName).Returns(_=>remote.App!.GetTestClient());
        var validator=Substitute.For<IUrlValidator>();validator.ValidatePlatformUrl("https://remote.example").Returns(new UrlValidationResult(true,null));
        var transport=new SelfHostedArchivePeerTransport(local.Db,local.Tenant,local.Peers,local.Exporter,local.Adapter,local.Coordinator,
            new PlatformSyncCredentialResolver(factory,local.Protection,validator,local.Db,NullLogger<PlatformSyncCredentialResolver>.Instance));
        await transport.RegisterAsync(local.User.Id,link.Id,"https://local.example",null,default);
        var pushed=await transport.TransferAsync(local.User.Id,link.Id,"push",default);Assert.Equal("complete",pushed.PreservationState);
        var remoteContexts=await remote.Db.ShPayloadKeyReferences.AsNoTracking().Where(r=>r.OwnerKind=="version").Select(r=>r.ContextId).Distinct().ToListAsync();Assert.DoesNotContain(remoteContexts,x=>originalContexts.Contains(x));
        var selected = await local.Db.ShVaultCryptoContextLinks.FirstAsync();
        var selectedKey = await local.Db.ShCryptoKeyIdentities.FirstAsync(k=>k.PortableCryptoContextId==selected.PortableCryptoContextId);
        if(preserveLocalSelection)
        {
            selected.IsPrimaryWriteContext=true; selected.PrimaryWriteFingerprint=selectedKey.Fingerprint; selected.PrimaryWriteGeneration=selectedKey.Generation;
            selectedKey.SourceLifecycle="active"; selectedKey.Lifecycle="active"; await local.Db.SaveChangesAsync();
            foreach(var key in await remote.Db.ShCryptoKeyIdentities.ToListAsync()) { key.SourceLifecycle="retired"; key.Lifecycle="decrypt-only"; } await remote.Db.SaveChangesAsync();
        }
        if(keepLocalRetired) { selectedKey.SourceLifecycle="retired";selectedKey.Lifecycle="decrypt-only";selected.IsPrimaryWriteContext=false;selected.PrimaryWriteFingerprint=null;selected.PrimaryWriteGeneration=null;await local.Db.SaveChangesAsync(); }
        if(changedHistory)
        {
            if(ordinaryHistory) { var historical=await remote.Db.KnowledgeVersions.SingleAsync();var record=SelfHostedSealedStore.ToPlainVersion(historical);var snapshot=JsonNode.Parse(record.SnapshotJson!)!;snapshot["encryptedFieldsJson"]=EncryptCurrentTitle("Contradictory immutable history",selectedKey.PublicKey);record.SnapshotJson=snapshot.ToJsonString();historical.PlatformData=JsonSerializer.Serialize(record,new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
            else { var historical=await remote.Db.ShSealedVersions.SingleAsync();historical.Ciphertext=Convert.ToBase64String(RandomNumberGenerator.GetBytes(Convert.FromBase64String(historical.Ciphertext).Length)); }
            await remote.Db.SaveChangesAsync();await Assert.ThrowsAsync<ArchiveOperationException>(()=>transport.TransferAsync(local.User.Id,link.Id,"pull",default));
            Assert.Equal(originalSealedHistory,(await local.Db.ShSealedVersions.AsNoTracking().OrderBy(v=>v.Id).ToListAsync()).Select(v=>JsonSerializer.Serialize(v)).ToArray());Assert.Equal(originalPlainHistory,(await local.Db.KnowledgeVersions.AsNoTracking().OrderBy(v=>v.Id).ToListAsync()).Select(v=>JsonSerializer.Serialize(v)).ToArray());return;
        }
        if(extendGeneration)
        {
            var old=await remote.Db.ShCryptoKeyIdentities.SingleAsync();
            using var secret=Key.Create(KeyAgreementAlgorithm.X25519); var bytes=secret.PublicKey.Export(KeyBlobFormat.RawPublicKey);
            var next=JsonSerializer.Deserialize<ShCryptoKeyIdentity>(JsonSerializer.Serialize(old))!;next.Id=Guid.NewGuid();next.Generation=old.Generation+1;next.PublicKey=Convert.ToBase64String(bytes);next.Fingerprint=Convert.ToHexStringLower(SHA256.HashData(bytes));next.SourceLifecycle="active";next.Lifecycle="decrypt-only";
            remote.Db.ShCryptoKeyIdentities.Add(next);var item=await remote.Db.KnowledgeItems.SingleAsync();item.EncryptedFieldsJson=EncryptCurrentTitle("Peer rotated current protected title",next.PublicKey);
            var current=await remote.Db.ShPayloadKeyReferences.SingleAsync(r=>r.OwnerKind=="knowledge"&&r.OwnerId==item.Id&&r.Role=="field");current.Fingerprint=next.Fingerprint;current.Generation=next.Generation;await remote.Db.SaveChangesAsync();
        }
        if(fault != null) { fault.TargetContext=selected.PortableCryptoContextId;fault.TargetVault=local.Vault.Id;fault.Armed=fault.Mode!="control"; }
        if(fault?.Armed==true)
        {
            var failure=await Record.ExceptionAsync(()=>transport.TransferAsync(local.User.Id,link.Id,"pull",default));Assert.True(fault.Fired);Assert.IsType<ArchiveOperationException>(failure);
            local.Db.ChangeTracker.Clear();Assert.Equal(originalSealedHistory,(await local.Db.ShSealedVersions.AsNoTracking().OrderBy(v=>v.Id).ToListAsync()).Select(v=>JsonSerializer.Serialize(v)).ToArray());Assert.Equal(originalPlainHistory,(await local.Db.KnowledgeVersions.AsNoTracking().OrderBy(v=>v.Id).ToListAsync()).Select(v=>JsonSerializer.Serialize(v)).ToArray());
            Assert.Equal("cancelled",(await local.Db.ShArchiveImportJournals.AsNoTracking().SingleAsync()).Status);Assert.Equal(selected.PortableCryptoContextId,(await local.Db.ShVaultCryptoContextLinks.AsNoTracking().SingleAsync()).PortableCryptoContextId);var keptKey=await local.Db.ShCryptoKeyIdentities.AsNoTracking().SingleAsync();Assert.Equal(selectedKey.Fingerprint,keptKey.Fingerprint);Assert.Equal("active",keptKey.SourceLifecycle);Assert.Equal("active",keptKey.Lifecycle);Assert.True((await local.Db.ShVaultCryptoContextLinks.AsNoTracking().SingleAsync()).IsPrimaryWriteContext);return;
        }
        var returned=await transport.TransferAsync(local.User.Id,link.Id,"pull",default);Assert.Equal("complete",returned.PreservationState);Assert.Equal(returned.RequiredPayloadCount,returned.PreservedPayloadCount);
        local.Db.ChangeTracker.Clear();var after=await local.Db.ShPayloadKeyReferences.AsNoTracking().Where(r=>r.OwnerKind=="version").ToListAsync();
        if(keepLocalRetired) { var keptLink=await local.Db.ShVaultCryptoContextLinks.AsNoTracking().SingleAsync();var keptKey=await local.Db.ShCryptoKeyIdentities.AsNoTracking().SingleAsync();Assert.False(keptLink.IsPrimaryWriteContext);Assert.Null(keptLink.PrimaryWriteFingerprint);Assert.Null(keptLink.PrimaryWriteGeneration);Assert.Equal("retired",keptKey.SourceLifecycle);Assert.Equal("decrypt-only",keptKey.Lifecycle); }
        if(preserveLocalSelection) { var keptLink=await local.Db.ShVaultCryptoContextLinks.AsNoTracking().SingleAsync(l=>l.Id==selected.Id); var keptKey=await local.Db.ShCryptoKeyIdentities.AsNoTracking().SingleAsync(k=>k.Id==selectedKey.Id); Assert.True(keptLink.IsPrimaryWriteContext); Assert.Equal(selectedKey.Fingerprint,keptLink.PrimaryWriteFingerprint); Assert.Equal(selectedKey.Generation,keptLink.PrimaryWriteGeneration); Assert.Equal("active",keptKey.Lifecycle); Assert.Equal("active",keptKey.SourceLifecycle); }
        if(extendGeneration) { Assert.Single(await local.Db.ShPortableCryptoContexts.ToListAsync());var added=await local.Db.ShCryptoKeyIdentities.SingleAsync(k=>k.Generation==selectedKey.Generation+1);Assert.Equal(selected.PortableCryptoContextId,added.PortableCryptoContextId);Assert.Equal("decrypt-only",added.Lifecycle);Assert.Equal("active",added.SourceLifecycle); }
        Assert.Equal(originalSealedHistory,(await local.Db.ShSealedVersions.AsNoTracking().OrderBy(v=>v.Id).ToListAsync()).Select(v=>JsonSerializer.Serialize(v)).ToArray());Assert.Equal(originalPlainHistory,(await local.Db.KnowledgeVersions.AsNoTracking().OrderBy(v=>v.Id).ToListAsync()).Select(v=>JsonSerializer.Serialize(v)).ToArray());
        if(duplicateHistory) Assert.Equal(2,await local.Db.ShPortableCryptoContexts.CountAsync());
        Assert.Equal(originalRefs.Count,after.Count);foreach(var reference in originalRefs){var kept=Assert.Single(after,r=>r.OwnerId==reference.OwnerId&&r.Role==reference.Role&&r.FieldName==reference.FieldName);Assert.Equal(reference.ContextId,kept.ContextId);Assert.Equal(reference.Fingerprint,kept.Fingerprint);Assert.Equal(reference.Generation,kept.Generation);Assert.Equal(reference.OriginalBindingId,kept.OriginalBindingId);Assert.Equal(reference.OriginalItemId,kept.OriginalItemId);}
    }

    [Theory]
    [InlineData("public")]
    [InlineData("fingerprint")]
    [InlineData("generation")]
    [InlineData("type")]
    [InlineData("custody")]
    [InlineData("profile")]
    [InlineData("kind")]
    public async Task CanonicalPeerContextRejectsContradictoryLinkedOriginalIdentity(string change)
    {
        await using var side=new PeerSide(); var source=await CurrentFieldFixtureAsync();
        Assert.True((await side.Graph.ImportAsync(source,side.Vault.Id,"trusted:context-control",ImportConflictStrategy.Overwrite,null,default)).Success);
        var context=await side.Db.ShPortableCryptoContexts.SingleAsync(); var key=await side.Db.ShCryptoKeyIdentities.FirstAsync();
        switch(change) { case "public":key.PublicKey="contradictory-public";break; case "fingerprint":key.Fingerprint="contradictory-fingerprint";break; case "generation":key.Generation=99;break; case "type":key.KeyType="contradictory-type";break; case "custody":key.Custody="contradictory-custody";break; case "profile":context.AlgorithmProfile="contradictory-profile";break; case "kind":context.BindingKind="contradictory-kind";break; }
        await side.Db.SaveChangesAsync(); var count=await side.Db.ShPortableCryptoContexts.CountAsync();
        await Assert.ThrowsAsync<InvalidDataException>(()=>side.Graph.ResolvePeerContextMappingsAsync(source,side.Vault.Id,"trusted:changed-lineage",default));
        Assert.Equal(count,await side.Db.ShPortableCryptoContexts.CountAsync());
    }
    [Theory]
    [InlineData("unlinked")]
    [InlineData("foreign-context")]
    [InlineData("foreign-link")]
    [InlineData("new-vault")]
    public async Task CanonicalPeerContextNeverAdoptsUnlinkedOrForeignContext(string change)
    {
        await using var side=new PeerSide(); var source=await CurrentFieldFixtureAsync();
        Assert.True((await side.Graph.ImportAsync(source,side.Vault.Id,"trusted:context-isolation",ImportConflictStrategy.Overwrite,null,default)).Success);
        var context=await side.Db.ShPortableCryptoContexts.SingleAsync(); var link=await side.Db.ShVaultCryptoContextLinks.SingleAsync(); var target=side.Vault.Id;
        switch(change) { case "unlinked":side.Db.ShVaultCryptoContextLinks.Remove(link);break; case "foreign-context":context.TenantId=Guid.NewGuid();break; case "foreign-link":link.TenantId=Guid.NewGuid();break; case "new-vault":var vault=new Knowz.Core.Entities.Vault {TenantId=side.Tenant.TenantId,Name="separate copy"};side.Db.Vaults.Add(vault);target=vault.Id;break; }
        await side.Db.SaveChangesAsync(); var mapping=await side.Graph.ResolvePeerContextMappingsAsync(source,target,"trusted:changed-lineage",default);
        Assert.NotEqual(context.Id,mapping[source.CryptoContexts!.Single().ContextId]); Assert.Single(mapping);
    }
    [Fact]
    public async Task CanonicalPeerContextRefusesTwoExactLinkedCandidates()
    {
        await using var side=new PeerSide(); var source=await CurrentFieldFixtureAsync();
        Assert.True((await side.Graph.ImportAsync(source,side.Vault.Id,"trusted:context-ambiguous",ImportConflictStrategy.Overwrite,null,default)).Success);
        var context=await side.Db.ShPortableCryptoContexts.SingleAsync(); var duplicate=JsonSerializer.Deserialize<ShPortableCryptoContext>(JsonSerializer.Serialize(context))!; duplicate.Id=Guid.NewGuid(); side.Db.ShPortableCryptoContexts.Add(duplicate);
        foreach(var key in await side.Db.ShCryptoKeyIdentities.AsNoTracking().ToListAsync()) { var clone=JsonSerializer.Deserialize<ShCryptoKeyIdentity>(JsonSerializer.Serialize(key))!;clone.Id=Guid.NewGuid();clone.PortableCryptoContextId=duplicate.Id;side.Db.ShCryptoKeyIdentities.Add(clone); }
        side.Db.ShVaultCryptoContextLinks.Add(new(){Id=Guid.NewGuid(),TenantId=side.Tenant.TenantId,DestinationVaultId=side.Vault.Id,PortableCryptoContextId=duplicate.Id});await side.Db.SaveChangesAsync();
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>side.Graph.ResolvePeerContextMappingsAsync(source,side.Vault.Id,"trusted:changed-lineage",default));Assert.Contains("ambiguous",error.Message);
    }

    [PostgresTheory]
    [InlineData("control")]
    [InlineData("unlink")]
    [InlineData("key")]
    [InlineData("profile")]
    [InlineData("drop-added-slot")]
    [InlineData("duplicate-contexts")]
    public async Task CanonicalPeerHistoryPostgresRejectsFinalSaveTopologyMutationWithoutCommit(string mode)
    {
        var configured=Environment.GetEnvironmentVariable("KNOWZ_TEST_POSTGRES");Assert.False(string.IsNullOrWhiteSpace(configured));
        var builder=new NpgsqlConnectionStringBuilder(configured);Assert.True(builder.Host is "127.0.0.1" or "localhost" && builder.Port==18460);builder.Database="postgres";builder.Pooling=false;
        await using var admin=new NpgsqlConnection(builder.ConnectionString);await admin.OpenAsync();var local="kc_context_local_"+Guid.NewGuid().ToString("N");var remote="kc_context_remote_"+Guid.NewGuid().ToString("N");
        try
        {
            foreach(var name in new[]{local,remote}) { await using var create=new NpgsqlCommand($"CREATE DATABASE \"{name}\"",admin);await create.ExecuteNonQueryAsync(); }
            builder.Database=local;var localConnection=builder.ConnectionString;builder.Database=remote;
            await ContextHistoryReturnAsync(true,true,extendGeneration:mode!="duplicate-contexts",localConnection:localConnection,remoteConnection:builder.ConnectionString,fault:new(mode=="duplicate-contexts"?"control":mode),duplicateHistory:mode=="duplicate-contexts");
        }
        finally { foreach(var name in new[]{local,remote}) { await using var drop=new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)",admin);await drop.ExecuteNonQueryAsync(); } }
    }
    private sealed class ContextTopologyFault(string mode):SaveChangesInterceptor
    {
        public string Mode=>mode;public bool Armed,Fired;public Guid TargetContext,TargetVault;
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data,int result,CancellationToken ct=default)
        {
            if(!Armed || data.Context!.ChangeTracker.Entries<ShArchiveImportJournal>().All(e=>e.Entity.Status!="committed")) return result;
            Armed=false;Fired=true;var db=(SelfHostedDbContext)data.Context!;
            if(mode=="unlink") Assert.Equal(1,await db.ShVaultCryptoContextLinks.Where(l=>l.PortableCryptoContextId==TargetContext && l.DestinationVaultId==TargetVault).ExecuteDeleteAsync(ct));
            else if(mode=="key") Assert.True(await db.ShCryptoKeyIdentities.Where(k=>k.PortableCryptoContextId==TargetContext).ExecuteUpdateAsync(s=>s.SetProperty(k=>k.Fingerprint,new string('f',64)),ct)>0);
            else if(mode=="drop-added-slot") Assert.Equal(1,await db.ShCryptoKeyIdentities.Where(k=>k.PortableCryptoContextId==TargetContext&&k.Generation==2).ExecuteDeleteAsync(ct));
            else if(mode=="profile") Assert.Equal(1,await db.ShPortableCryptoContexts.Where(c=>c.Id==TargetContext).ExecuteUpdateAsync(s=>s.SetProperty(c=>c.AlgorithmProfile,"changed-at-final-save"),ct));
            return result;
        }
    }

    private static PortableExportPackage SecondImmutableHistoryCopy(PortableExportPackage source)
    {
        var copy=JsonSerializer.Deserialize<PortableExportPackage>(JsonSerializer.Serialize(source))!;
        var logical=source.Data.KnowledgeItems.Select(k=>k.Id).Concat((source.SealedItems??[]).Select(k=>k.Id))
            .Concat((source.SealedComments??[]).Select(c=>c.Id)).Concat((source.SealedFiles??[]).Select(f=>f.Id))
            .Concat((source.SealedAttachments??[]).Select(a=>a.Id)).Concat((source.SealedVersions??[]).Select(v=>v.Id)).Concat((source.PlainVersions??[]).Select(v=>v.Id))
            .Distinct().ToDictionary(id=>id,_=>Guid.NewGuid());
        Guid M(Guid id)=>logical.GetValueOrDefault(id,id);Guid? O(Guid? id)=>id.HasValue?M(id.Value):null;
        var oldContext=copy.CryptoContexts!.Single().ContextId;var newContext=Guid.NewGuid();copy.CryptoContexts.Single().ContextId=newContext;
        foreach(var k in copy.CryptoKeyCatalog??[]) k.ContextId=newContext;
        foreach(var l in copy.VaultCryptoContextLinks??[]) {l.ContextId=newContext;l.IsPrimaryWriteContext=false;l.PrimaryWriteFingerprint=null;l.PrimaryWriteGeneration=null;}
        foreach(var k in copy.Data.KnowledgeItems) k.Id=M(k.Id);
        foreach(var k in copy.SealedItems??[]) k.Id=M(k.Id);
        foreach(var c in copy.SealedComments??[]) {c.Id=M(c.Id);c.KnowledgeId=M(c.KnowledgeId);c.ParentCommentId=O(c.ParentCommentId);}
        foreach(var f in copy.SealedFiles??[]) {f.Id=M(f.Id);f.KnowledgeId=O(f.KnowledgeId);f.RootFileRecordId=O(f.RootFileRecordId);f.PreviousFileRecordId=O(f.PreviousFileRecordId);}
        foreach(var a in copy.SealedAttachments??[]) {a.Id=M(a.Id);a.FileRecordId=M(a.FileRecordId);a.KnowledgeId=O(a.KnowledgeId);a.CommentId=O(a.CommentId);}
        foreach(var v in copy.SealedVersions??[]) {v.Id=M(v.Id);v.KnowledgeId=M(v.KnowledgeId);}
        foreach(var v in copy.PlainVersions??[]) {v.Id=M(v.Id);v.KnowledgeId=M(v.KnowledgeId);}
        foreach(var r in copy.PayloadKeyReferences??[]) {r.OwnerId=M(r.OwnerId);r.VersionId=O(r.VersionId);r.ContextId=newContext;}
        Assert.Null(EncryptedGraphProfile.Validate(copy));return copy;
    }

    [Theory]
    [InlineData("foreign-version")]
    [InlineData("foreign-vault")]
    [InlineData("unlinked-parent")]
    [InlineData("deleted-parent")]
    [InlineData("subject")]
    [InlineData("fingerprint")]
    [InlineData("generation")]
    [InlineData("role")]
    [InlineData("field")]
    [InlineData("profile")]
    [InlineData("type")]
    [InlineData("envelope")]
    [InlineData("conflicting-anchors")]
    public async Task CanonicalPeerHistoryAnchorCannotBorrowForeignOrContradictoryVersionAuthority(string change)
    {
        await using var side=new PeerSide();var source=await FieldHistoryFixtureAsync();
        Assert.True((await side.Graph.ImportAsync(source,side.Vault.Id,"trusted:anchor-denial",ImportConflictStrategy.Overwrite,null,default)).Success);
        var original=await side.Db.ShPortableCryptoContexts.SingleAsync();var duplicate=JsonSerializer.Deserialize<ShPortableCryptoContext>(JsonSerializer.Serialize(original))!;duplicate.Id=Guid.NewGuid();side.Db.ShPortableCryptoContexts.Add(duplicate);
        foreach(var key in await side.Db.ShCryptoKeyIdentities.AsNoTracking().ToListAsync()) {var copy=JsonSerializer.Deserialize<ShCryptoKeyIdentity>(JsonSerializer.Serialize(key))!;copy.Id=Guid.NewGuid();copy.PortableCryptoContextId=duplicate.Id;side.Db.ShCryptoKeyIdentities.Add(copy);}
        side.Db.ShVaultCryptoContextLinks.Add(new(){Id=Guid.NewGuid(),TenantId=side.Tenant.TenantId,DestinationVaultId=side.Vault.Id,PortableCryptoContextId=duplicate.Id});await side.Db.SaveChangesAsync();
        var incoming=await side.Graph.ExportAsync(new(){Type=ExportScopeType.Vault,VaultIds=[side.Vault.Id]},false,default);
        incoming.CryptoContexts!.RemoveAll(c=>c.ContextId==duplicate.Id);incoming.CryptoKeyCatalog!.RemoveAll(k=>k.ContextId==duplicate.Id);incoming.VaultCryptoContextLinks!.RemoveAll(l=>l.ContextId==duplicate.Id);
        var alias=Guid.NewGuid();incoming.CryptoContexts.Single().ContextId=alias;foreach(var key in incoming.CryptoKeyCatalog)key.ContextId=alias;foreach(var link in incoming.VaultCryptoContextLinks)link.ContextId=alias;foreach(var reference in incoming.PayloadKeyReferences!)reference.ContextId=alias;
        var version=await side.Db.KnowledgeVersions.SingleAsync();var stored=await side.Db.ShPayloadKeyReferences.SingleAsync(r=>r.OwnerKind=="version");var member=await side.Db.KnowledgeItems.SingleAsync();
        switch(change)
        {
            case "foreign-version":version.TenantId=Guid.NewGuid();break;
            case "foreign-vault":var vault=new Knowz.Core.Entities.Vault{TenantId=side.Tenant.TenantId,Name="unrelated owner"};side.Db.Vaults.Add(vault);side.Db.KnowledgeVaults.Remove(await side.Db.KnowledgeVaults.SingleAsync());side.Db.KnowledgeVaults.Add(new(){TenantId=side.Tenant.TenantId,KnowledgeId=member.Id,VaultId=vault.Id});break;
            case "unlinked-parent":side.Db.KnowledgeVaults.Remove(await side.Db.KnowledgeVaults.SingleAsync());break;
            case "deleted-parent":member.IsDeleted=true;break;
            case "subject":stored.OriginalItemId=Guid.NewGuid();break;
            case "fingerprint":stored.Fingerprint=new string('f',64);break;
            case "generation":stored.Generation=99;break;
            case "role":stored.Role="body";break;
            case "field":stored.FieldName="summary";break;
            case "profile":stored.AlgorithmProfile="contradictory-profile";break;
            case "type":stored.KeyType="contradictory-type";break;
            case "envelope":stored.EnvelopeVersion=99;break;
            case "conflicting-anchors":
                var prior=JsonSerializer.Deserialize<Knowz.Core.Entities.KnowledgeVersion>(JsonSerializer.Serialize(version))!;prior.Id=Guid.NewGuid();prior.VersionNumber++;
                var wire=SelfHostedSealedStore.ToPlainVersion(prior);prior.PlatformData=JsonSerializer.Serialize(wire,new JsonSerializerOptions(JsonSerializerDefaults.Web));side.Db.KnowledgeVersions.Add(prior);
                var other=JsonSerializer.Deserialize<ShPayloadKeyReference>(JsonSerializer.Serialize(stored))!;other.Id=Guid.NewGuid();other.OwnerId=prior.Id;other.VersionId=prior.Id;other.ContextId=duplicate.Id;side.Db.ShPayloadKeyReferences.Add(other);
                incoming.PlainVersions!.Add(wire);var refWire=JsonSerializer.Deserialize<PortablePayloadKeyReference>(JsonSerializer.Serialize(incoming.PayloadKeyReferences.Single(r=>r.OwnerKind=="version")))!;refWire.OwnerId=prior.Id;refWire.VersionId=prior.Id;incoming.PayloadKeyReferences.Add(refWire);break;
        }
        await side.Db.SaveChangesAsync();Assert.Null(EncryptedGraphProfile.Validate(incoming));
        await Assert.ThrowsAsync<InvalidDataException>(()=>side.Graph.ResolvePeerContextMappingsAsync(incoming,side.Vault.Id,"trusted:anchored-return",default));
    }
}
