using System.Data.Common;
using System.Text;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.Core.Schema;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class PortableImportAtomicPostgresTests
{
    [PostgresTheory]
    [InlineData("plain-create", true)]
    [InlineData("plain-overwrite", true)]
    [InlineData("binary-create", false)]
    [InlineData("binary-create", true)]
    [InlineData("binary-overwrite", false)]
    [InlineData("binary-overwrite", true)]
    [InlineData("before-commit", false)]
    [InlineData("after-commit", false)]
    [InlineData("upload-failure", false)]
    [InlineData("before-commit", true)]
    [InlineData("after-commit", true)]
    [InlineData("upload-failure", true)]
    [InlineData("revoke-before", true)]
    [InlineData("revoke-after", true)]
    [InlineData("changed-during-stage", true)]
    [InlineData("cancel-upload", true)]
    [InlineData("bad-size", true)]
    [InlineData("recovery-unavailable", true)]
    [InlineData("journal-substitution", true)]
    [InlineData("selected-tenant", true)]
    [InlineData("inactive-membership", true)]
    [InlineData("downgraded-membership", true)]
    [InlineData("membership-revoked", true)]
    [InlineData("membership-downgraded", true)]
    public async Task DirectLegacyImportIsAtomicWithoutReplayingBytes(string phase, bool retries)
    {
        var configured=Environment.GetEnvironmentVariable("KNOWZ_TEST_POSTGRES"); Assert.False(string.IsNullOrWhiteSpace(configured));
        var connection=new NpgsqlConnectionStringBuilder(configured); Assert.True(connection.Host is "localhost" or "127.0.0.1" && connection.Port==18460);
        connection.Database="postgres";connection.Pooling=false;
        await using var admin=new NpgsqlConnection(connection.ConnectionString);await admin.OpenAsync();
        var name="kc_legacy_atomic_"+Guid.NewGuid().ToString("N");await using(var create=new NpgsqlCommand($"CREATE DATABASE \"{name}\"",admin))await create.ExecuteNonQueryAsync();
        connection.Database=name;var tenant=Substitute.For<ITenantProvider>();tenant.TenantId.Returns(Guid.NewGuid());
        var readFault=new LegacyRecoveryReadFault();var fault=new LegacyCommitFault(phase);var options=new DbContextOptionsBuilder<SelfHostedDbContext>().UseNpgsql(connection.ConnectionString,o=>{if(retries)o.EnableRetryOnFailure();}).AddInterceptors(fault,readFault).Options;
        var directory=Path.Combine(Path.GetTempPath(),name);var local=new LocalFileStorageProvider(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Storage:Local:RootPath",directory}}).Build(),NullLogger<LocalFileStorageProvider>.Instance);
        var storage=new CountingLegacyStorage(local,phase=="upload-failure");var id=Guid.NewGuid();var actorId=Guid.NewGuid();var actor=Substitute.For<IPortableActorProvider>();actor.ActorId.Returns(actorId);var plain=phase.StartsWith("plain-");var creating=phase.EndsWith("create");
        var externalOptions=new DbContextOptionsBuilder<SelfHostedDbContext>().UseNpgsql(connection.ConnectionString).Options;
        async Task ChangeExternalAsync(string mode)
        {
            await using var other=new SelfHostedDbContext(externalOptions,tenant);
            if(mode.StartsWith("revoke")){(await other.Users.SingleAsync(u=>u.Id==actorId)).IsActive=false;await other.SaveChangesAsync();}
            if(mode is "membership-revoked" or "membership-downgraded") { var member=await other.UserTenantMemberships.SingleAsync(m=>m.UserId==actorId);if(mode=="membership-revoked")member.IsActive=false;else member.Role=Knowz.Core.Enums.UserRole.User;await other.SaveChangesAsync(); }
            if(mode=="changed-during-stage"){var current=await other.FileRecords.SingleAsync(f=>f.Id==id);current.FileName="concurrent.txt";current.UpdatedAt=DateTime.UtcNow;await other.SaveChangesAsync();}
            if(mode=="journal-substitution"){var journal=await other.ShArchiveImportJournals.SingleAsync(j=>j.Status=="committed");journal.ManifestSha256=new string('0',64);await other.SaveChangesAsync();}
        }
        storage.AfterUpload=async()=>{if(phase is "revoke-before" or "changed-during-stage" or "membership-revoked" or "membership-downgraded")await ChangeExternalAsync(phase);if(phase=="cancel-upload")throw new OperationCanceledException("Synthetic cancelled upload.");};
        fault.AfterCommit=async()=>{if(phase is "revoke-after" or "journal-substitution")await ChangeExternalAsync(phase);if(phase=="recovery-unavailable")readFault.Enabled=true;};
        var oldBytes=Encoding.UTF8.GetBytes("Previous exact bytes — 猫");var newBytes=Encoding.UTF8.GetBytes("Imported complete replacement bytes — λ");
        try
        {
            await using(var db=new SelfHostedDbContext(options,tenant))
            {
                await db.Database.MigrateAsync();db.Tenants.Add(new(){Id=tenant.TenantId,Name="Owned legacy atomic fixture",Slug=name});
                var homeTenant=phase=="selected-tenant"?Guid.NewGuid():tenant.TenantId;
                if(homeTenant!=tenant.TenantId)db.Tenants.Add(new(){Id=homeTenant,Name="Distinct actor home tenant",Slug=name+"-home"});
                db.Users.Add(new(){Id=actorId,TenantId=homeTenant,Username="legacy-admin",IsActive=true,Role=phase=="selected-tenant"?Knowz.Core.Enums.UserRole.User:Knowz.Core.Enums.UserRole.SuperAdmin});
                db.UserTenantMemberships.Add(new(){UserId=actorId,TenantId=tenant.TenantId,IsActive=phase!="inactive-membership",Role=phase=="downgraded-membership"?Knowz.Core.Enums.UserRole.User:Knowz.Core.Enums.UserRole.SuperAdmin});
                if(!creating)
                {
                    if(plain)db.KnowledgeItems.Add(new(){Id=id,TenantId=tenant.TenantId,Title="Old title",Content="Old body"});
                    else {var uri=await local.UploadAsync(tenant.TenantId,id,new MemoryStream(oldBytes),"text/plain");db.FileRecords.Add(new(){Id=id,TenantId=tenant.TenantId,FileName="old.txt",ContentType="text/plain",SizeBytes=oldBytes.Length,BlobUri=uri});}
                }
                await db.SaveChangesAsync();
            }
            PortableImportResult result;
            await using(var db=new SelfHostedDbContext(options,tenant))
            {
                var package=new PortableExportPackage{SchemaVersion=CoreSchema.Version,SourceEdition="selfhosted",SourceTenantId=tenant.TenantId};
                if(plain)package.Data.KnowledgeItems.Add(new(){Id=id,Title="Imported title",Content="Imported body"});
                else package.Data.FileRecords.Add(new(){Id=id,FileName="new.txt",ContentType="text/plain",SizeBytes=newBytes.Length+(phase=="bad-size"?1:0),BinaryContentBase64=Convert.ToBase64String(newBytes)});
                fault.Armed=true;
                var service=new PortableImportService(db,tenant,storage,NullLogger<PortableImportService>.Instance,actor);
                if(phase=="cancel-upload"){await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.ImportAsync(package,ImportConflictStrategy.Overwrite));result=new(){Success=false};}
                else result=await service.ImportAsync(package,ImportConflictStrategy.Overwrite);
                readFault.Enabled=false;
            }
            var rejected=phase is "before-commit" or "upload-failure" or "revoke-before" or "changed-during-stage" or "cancel-upload" or "bad-size" or "inactive-membership" or "downgraded-membership" or "membership-revoked" or "membership-downgraded";
            var unconfirmed=phase is "revoke-after" or "recovery-unavailable" or "journal-substitution";
            Assert.Equal(!rejected&&!unconfirmed,result.Success);
            Assert.Equal(plain||phase is "inactive-membership" or "downgraded-membership"?0:1,storage.UploadCount);Assert.True(fault.CommitAttempts<=1,"An import must never blindly replay its COMMIT or physical upload.");
            await using(var db=new SelfHostedDbContext(options,tenant))
            {
                if(plain){Assert.Equal("Imported body",(await db.KnowledgeItems.SingleAsync()).Content);return;}
                var record=await db.FileRecords.SingleAsync();Assert.Equal(phase=="changed-during-stage"?"concurrent.txt":rejected?"old.txt":"new.txt",record.FileName);Assert.Equal(rejected?oldBytes.Length:newBytes.Length,record.SizeBytes);
                var physical=await db.ShFileObjectReferences.AsNoTracking().Where(r=>r.FileRecordId==id).Select(r=>(Guid?)r.BlobStorageId).SingleOrDefaultAsync()??id;
                var download=await local.DownloadAsync(tenant.TenantId,physical);await using(download.stream){using var copied=new MemoryStream();await download.stream.CopyToAsync(copied);Assert.Equal(rejected?oldBytes:newBytes,copied.ToArray());}
                if(!rejected)Assert.NotEqual(id,physical);
                await new SelfHostedPortabilityStageReconciler(db,local,NullLogger<SelfHostedPortabilityStageReconciler>.Instance).ReconcileAsync(DateTime.UtcNow.AddHours(2),default);
                Assert.True(await local.ExistsAsync(tenant.TenantId,physical));
                var remaining=Directory.GetFiles(directory,"*",SearchOption.AllDirectories).Where(p=>!Path.GetFileName(p).StartsWith('.')).ToArray();Assert.Single(remaining);
            }
        }
        finally{await using var drop=new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)",admin);await drop.ExecuteNonQueryAsync();if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }

    private sealed class LegacyCommitFault(string phase):DbTransactionInterceptor
    {
        internal bool Armed;internal int CommitAttempts;internal Func<Task>? AfterCommit;
        private bool Eligible(DbContext? context)=>Armed&&context!.ChangeTracker.Entries<FileRecord>().Any();
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,TransactionEventData data,InterceptionResult result,CancellationToken ct=default)
        {if(Eligible(data.Context)){CommitAttempts++;if(phase=="before-commit")throw new TimeoutException("Synthetic fault before legacy COMMIT.");}return ValueTask.FromResult(result);}
        public override async Task TransactionCommittedAsync(DbTransaction transaction,TransactionEndEventData data,CancellationToken ct=default)
        {if(Eligible(data.Context)&&phase is "after-commit" or "revoke-after" or "recovery-unavailable" or "journal-substitution"){if(AfterCommit!=null)await AfterCommit();throw new TimeoutException("Synthetic lost legacy COMMIT acknowledgement.");}}
    }
    private sealed class LegacyRecoveryReadFault:DbCommandInterceptor
    {
        internal bool Enabled;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData eventData,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {if(Enabled&&command.CommandText.Contains("ShArchiveImportJournals"))throw new IOException("Synthetic unavailable recovery database read.");return ValueTask.FromResult(result);}
    }
    private sealed class CountingLegacyStorage(IFileStorageProvider inner,bool fail):IFileStorageProvider
    {
        internal int UploadCount;internal Func<Task>? AfterUpload;
        public async Task<string> UploadAsync(Guid tenant,Guid file,Stream input,string type,CancellationToken ct=default){UploadCount++;var uri=await inner.UploadAsync(tenant,file,input,type,ct);if(AfterUpload!=null)await AfterUpload();if(fail)throw new IOException("Synthetic upload failure after physical write.");return uri;}
        public Task<(Stream stream,string contentType,string fileName)> DownloadAsync(Guid tenant,Guid file,CancellationToken ct=default)=>inner.DownloadAsync(tenant,file,ct);
        public Task<bool> DeleteAsync(Guid tenant,Guid file,CancellationToken ct=default)=>inner.DeleteAsync(tenant,file,ct);
        public Task<bool> ExistsAsync(Guid tenant,Guid file,CancellationToken ct=default)=>inner.ExistsAsync(tenant,file,ct);
        public Task<string> GenerateDownloadUrlAsync(Guid tenant,Guid file,string name,TimeSpan? expiry=null,CancellationToken ct=default)=>inner.GenerateDownloadUrlAsync(tenant,file,name,expiry,ct);
        public Task<bool> IsAvailableAsync(CancellationToken ct=default)=>inner.IsAvailableAsync(ct);
    }
}
