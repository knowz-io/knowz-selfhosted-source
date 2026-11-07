using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowz.Core.Configuration;
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
    [PostgresTheory]
    [InlineData(ImportConflictStrategy.Skip, false)]
    [InlineData(ImportConflictStrategy.Merge, false)]
    [InlineData(ImportConflictStrategy.Overwrite, false)]
    [InlineData(ImportConflictStrategy.Overwrite, true)]
    [InlineData(ImportConflictStrategy.Merge, true, true)]
    [InlineData(ImportConflictStrategy.Overwrite, true, true)]
    public async Task LegacyProtectedFileMetadataRequiresExactPreservationAndSignedReturn(ImportConflictStrategy strategy, bool includesFields, bool clearCompanion = false)
    {
        var configured=Environment.GetEnvironmentVariable("KNOWZ_TEST_POSTGRES"); Assert.False(string.IsNullOrWhiteSpace(configured));
        var cs=new NpgsqlConnectionStringBuilder(configured);Assert.True(cs.Host is "localhost" or "127.0.0.1" && cs.Port==18460);cs.Database="postgres";cs.Pooling=false;
        await using var admin=new NpgsqlConnection(cs.ConnectionString);await admin.OpenAsync();
        var name="kc_legacy_file_"+Guid.NewGuid().ToString("N");await using(var create=new NpgsqlCommand($"CREATE DATABASE \"{name}\"",admin))await create.ExecuteNonQueryAsync();
        cs.Database=name;var tenant=Substitute.For<ITenantProvider>();tenant.TenantId.Returns(Guid.NewGuid());
        var options=new DbContextOptionsBuilder<SelfHostedDbContext>().UseNpgsql(cs.ConnectionString,o=>o.EnableRetryOnFailure()).Options;
        var directory=Path.Combine(Path.GetTempPath(),name);var storage=new LocalFileStorageProvider(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Storage:Local:RootPath",directory}}).Build(),NullLogger<LocalFileStorageProvider>.Instance);
        var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var source=JsonSerializer.Deserialize<PortableExportPackage>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"Fixtures","portable-graph-v1.json")),json)!;
        var file=source.SealedFiles!.Single();var previous=Encoding.UTF8.GetBytes("Ordinary original bytes with protected name");var replacement=Encoding.UTF8.GetBytes("New exact ordinary bytes with unchanged protected name");
        source.Data.FileRecords.Add(new(){Id=file.Id,OriginalFileId=file.OriginalFileId,FileName="encrypted-file",ContentType="application/octet-stream",SizeBytes=previous.Length,BinaryContentBase64=Convert.ToBase64String(previous),EncryptedFieldsJson=file.EncryptedFieldsJson,Attachments=[new(){KnowledgeId=source.SealedItems!.Single().Id}]});
        source.SealedFiles.Clear();source.PayloadKeyReferences!.RemoveAll(r=>r.OwnerKind=="file"&&r.Role=="binary");
        Assert.Null(EncryptedGraphProfile.Validate(source));Guid id;string originalRefs;
        try
        {
            await using(var db=new SelfHostedDbContext(options,tenant))
            {
                await db.Database.MigrateAsync();db.Tenants.Add(new(){Id=tenant.TenantId,Name="Legacy protected-file fixture",Slug=name});await db.SaveChangesAsync();
                var imported=await db.Database.CreateExecutionStrategy().ExecuteAsync(()=>Graph(db).ImportAsync(source,null,"trusted:legacy-file-native",ImportConflictStrategy.Skip,null,default));Assert.True(imported.Success,imported.Error);
                id=(await db.FileRecords.SingleAsync()).Id;originalRefs=JsonSerializer.Serialize(await db.ShPayloadKeyReferences.AsNoTracking().OrderBy(r=>r.Id).ToListAsync());
            }
            await using(var db=new SelfHostedDbContext(options,tenant))
            {
                var package=new PortableExportPackage{SourceEdition="selfhosted",SourceTenantId=tenant.TenantId};
                package.Data.Topics.Add(new(){Id=Guid.NewGuid(),Name="must-not-write-before-protected-file-check"});
                package.Data.FileRecords.Add(new(){Id=id,OriginalFileId=file.OriginalFileId,FileName=includesFields&&!clearCompanion?"encrypted-file":"MUST_NOT_EXPOSE_PROTECTED_FILENAME",EncryptedFieldsJson=includesFields?file.EncryptedFieldsJson:null,ContentType="application/octet-stream",SizeBytes=replacement.Length,BinaryContentBase64=Convert.ToBase64String(replacement)});
                var result=await new PortableImportService(db,tenant,storage,NullLogger<PortableImportService>.Instance).ImportAsync(package,strategy);
                if(strategy==ImportConflictStrategy.Skip||includesFields&&!clearCompanion)Assert.True(result.Success,result.Error);
                else{Assert.False(result.Success);Assert.Contains("protected",result.Error,StringComparison.OrdinalIgnoreCase);Assert.False(await db.Topics.AnyAsync(t=>t.Id==package.Data.Topics.Single().Id));}
            }
            await using(var db=new SelfHostedDbContext(options,tenant))
            {
                var current=await db.FileRecords.SingleAsync();Assert.Equal(file.EncryptedFieldsJson,current.EncryptedFieldsJson);Assert.Equal("encrypted-file",current.FileName);Assert.Equal(file.OriginalFileId,current.OriginalFileId);
                Assert.Equal(originalRefs,JsonSerializer.Serialize(await db.ShPayloadKeyReferences.AsNoTracking().OrderBy(r=>r.Id).ToListAsync()));
                var exported=await new SelfHostedArchiveExportService(db,tenant,Graph(db),new EphemeralDataProtectionProvider()).ExportAsync(new(){Type=ExportScopeType.Tenant},false,default);
                await using(exported.Stream)
                {
                    using var archive=await KnowzArchiveReader.OpenAsync(exported.Stream,new(){ExpectedSignerFingerprint=exported.Receipt.SignerFingerprint,LeaveOpen=true});
                    var package=PortableGraphPackages.Combine(archive.Packages);var returned=package.Data.FileRecords.Single();Assert.Equal(file.EncryptedFieldsJson,returned.EncryptedFieldsJson);
                    var fields=JsonNode.Parse(returned.EncryptedFieldsJson!)!;fields["fields"]!["title"]=fields["fields"]!["fileName"]!.DeepClone();Assert.Equal("file:fileName",DecryptCurrentField(fields.ToJsonString()));
                    await using var bytes=await archive.OpenBinaryAsync(returned.BinaryEntryPath!,default);using var copied=new MemoryStream();await bytes.CopyToAsync(copied);Assert.Equal(includesFields&&!clearCompanion?replacement:previous,copied.ToArray());
                }
            }
        }
        finally{await using var drop=new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)",admin);await drop.ExecuteNonQueryAsync();if(Directory.Exists(directory))Directory.Delete(directory,true);}
        SelfHostedGraphStore Graph(SelfHostedDbContext db)=>new(db,tenant,new(db,tenant,storage,NullLogger<PortableImportService>.Instance),new(db,tenant,storage,Options.Create(new SelfHostedOptions()),NullLogger<PortableExportService>.Instance),storage);
    }
}
