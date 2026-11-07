using System.Security.Cryptography;
using System.Text.Json;
using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed partial class SelfHostedGraphStoreTests
{
    private static PortableExportPackage PlainFileVaultFixture()
    {
        var vault=Guid.NewGuid();var item=Guid.NewGuid();var file=Guid.NewGuid();
        return new() { SchemaVersion=5,RequiredCapabilities=[EncryptedGraphProfile.Capability],SourceEdition="platform",SourceTenantId=Guid.NewGuid(),
            Data=new() { Vaults=[new() { Id=vault,Name="Plain file vault" }],KnowledgeItems=[new() { Id=item,Title="Ordinary owner",Content="Exact ordinary body",VaultIds=[vault] }],
                FileRecords=[new() { Id=file,OriginalFileId=file,FileName="ordinary.bin",ContentType="application/octet-stream",SizeBytes=4,BinaryContentBase64=Convert.ToBase64String(new byte[]{0,1,2,255}),
                    Attachments=[new() { KnowledgeId=item }],ExtensionData=new() { ["vaultId"]=JsonSerializer.SerializeToElement(vault),["opaqueMetadata"]=JsonSerializer.SerializeToElement(new { vaultId=vault,note="literal original identifier" }) } }] } };
    }
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlainFileVaultReferenceSurvivesSignedNativeMappingAndReturn(bool explicitTarget)
    {
        var connection=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("KNOWZ_TEST_POSTGRES"));Assert.Contains(connection.Host,new[]{"localhost","127.0.0.1"});Assert.Equal(18460,connection.Port);
        var database="kc_file_vault_"+Guid.NewGuid().ToString("N");connection.Database="postgres";connection.Pooling=false;
        await using var admin=new NpgsqlConnection(connection.ConnectionString);await admin.OpenAsync();await using(var create=new NpgsqlCommand($"CREATE DATABASE \"{database}\"",admin))await create.ExecuteNonQueryAsync();
        connection.Database=database;var options=new DbContextOptionsBuilder<SelfHostedDbContext>().UseNpgsql(connection.ConnectionString,p=>p.EnableRetryOnFailure()).Options;
        var directory=Path.Combine(Path.GetTempPath(),database);var storage=new LocalFileStorageProvider(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Storage:Local:RootPath"]=directory }).Build(),NullLogger<LocalFileStorageProvider>.Instance);
        var source=PlainFileVaultFixture();var originalFile=source.Data.FileRecords.Single().OriginalFileId;var opaque=source.Data.FileRecords.Single().ExtensionData!["opaqueMetadata"].GetRawText();
        try
        {
            using var signer=ECDsa.Create(ECCurve.NamedCurves.nistP256);using var bytes=new MemoryStream();
            var receipt=await KnowzArchiveWriter.WriteAsync(bytes,[source],null,new() { SigningKey=signer,SourceSnapshot="plain-file-vault-native" });bytes.Position=0;
            var first=await ImportReturn(bytes,receipt,source,true);await using(first.Stream)
            { var second=await ImportReturn(first.Stream,first.Receipt,first.Package,false);await second.Stream.DisposeAsync(); }
        }
        finally { await using var drop=new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)",admin);await drop.ExecuteNonQueryAsync();if(Directory.Exists(directory))Directory.Delete(directory,true); }
        async Task<(Stream Stream,ArchiveSourceReceipt Receipt,PortableExportPackage Package)> ImportReturn(Stream input,ArchiveSourceReceipt sourceReceipt,PortableExportPackage previous,bool migrate)
        {
            var tenant=Substitute.For<ITenantProvider>();tenant.TenantId.Returns(Guid.NewGuid());ArchiveImportPlan plan;
            await using(var db=new SelfHostedDbContext(options,tenant))
            {
                if(migrate)await db.Database.MigrateAsync();db.Tenants.Add(new() { Id=tenant.TenantId,Name="File vault mapping",Slug="file-vault-"+tenant.TenantId.ToString("N") });
                var user=new User { TenantId=tenant.TenantId,Username="file-vault-"+tenant.TenantId.ToString("N"),Role=Knowz.Core.Enums.UserRole.Admin,IsActive=true };db.Users.Add(user);
                var target=explicitTarget?new Vault { TenantId=tenant.TenantId,Name="Selected destination" }:null;if(target!=null)db.Vaults.Add(target);await db.SaveChangesAsync();
                var adapter=new SelfHostedArchiveImportAdapter(db,tenant,Graph(db,tenant),new VaultAccessService(db));using var coordinator=new ArchiveImportCoordinator();var actor=new ArchiveActor(tenant.TenantId,user.Id.ToString("D"));
                plan=await coordinator.CreatePlanAsync(input,actor,new() { ExpectedSignerFingerprint=sourceReceipt.SignerFingerprint,SourceReceipt=sourceReceipt,Strategy="overwrite",IdempotencyKey="plain-file-vault",TargetVaultId=target?.Id },adapter);
                Assert.Equal("complete",(await coordinator.CommitAsync(plan.PlanId,actor,adapter)).PreservationState);
            }
            await using var reopened=new SelfHostedDbContext(options,tenant);var output=await new SelfHostedArchiveExportService(reopened,tenant,Graph(reopened,tenant),new EphemeralDataProtectionProvider()).ExportAsync(new() { Type=ExportScopeType.Tenant },false,default);
            using var archive=await KnowzArchiveReader.OpenAsync(output.Stream,new() { ExpectedSignerFingerprint=output.Receipt.SignerFingerprint,LeaveOpen=true });var next=PortableGraphPackages.Combine(archive.Packages);var file=Assert.Single(next.Data.FileRecords);
            var mapped=Guid.Parse(plan.Mapping["vault:"+previous.Data.Vaults.Single().Id.ToString("D")]);Assert.Equal(mapped,file.ExtensionData!["vaultId"].GetGuid());Assert.Contains(next.Data.Vaults,v=>v.Id==mapped);
            Assert.Equal(originalFile,file.OriginalFileId);Assert.Equal(opaque,file.ExtensionData["opaqueMetadata"].GetRawText());Assert.Equal(next.Data.KnowledgeItems.Single().Id,file.Attachments.Single().KnowledgeId);
            using var binary=await archive.OpenBinaryAsync(file.BinaryEntryPath!,default);using var copy=new MemoryStream();await binary.CopyToAsync(copy);Assert.Equal(new byte[]{0,1,2,255},copy.ToArray());
            output.Stream.Position=0;return(output.Stream,output.Receipt,next);
        }
        SelfHostedGraphStore Graph(SelfHostedDbContext db,ITenantProvider tenant)=>new(db,tenant,new PortableImportService(db,tenant,storage,NullLogger<PortableImportService>.Instance),new PortableExportService(db,tenant,storage,Options.Create(new SelfHostedOptions()),NullLogger<PortableExportService>.Instance),storage);
    }
    [Theory]
    [InlineData("foreign")][InlineData("invalid")][InlineData("number")][InlineData("null")][InlineData("absent")]
    public async Task PlainFileVaultReferenceRequiresDeclaredSourceScopeWithoutGuessingOpaqueIds(string mode)
    {
        var source=PlainFileVaultFixture();var file=source.Data.FileRecords.Single();
        if(mode=="absent")file.ExtensionData!.Remove("vaultId");else file.ExtensionData!["vaultId"]=mode switch { "foreign"=>JsonSerializer.SerializeToElement(Guid.NewGuid()),"invalid"=>JsonSerializer.SerializeToElement("not-a-guid"),"number"=>JsonSerializer.SerializeToElement(1),_=>JsonSerializer.SerializeToElement((string?)null) };
        var result=await _graph.ValidateImportAsync(source,null,"trusted:file-vault-scope",null,default);
        if(mode is "null" or "absent")Assert.Null(result);else Assert.NotNull(result);
        Assert.Empty(await _db.FileRecords.ToListAsync());Assert.Empty(await _db.ShFileObjectReferences.ToListAsync());
    }
}
