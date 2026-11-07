using System.Net;
using System.Data.Common;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.API.Endpoints;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed partial class SelfHostedGraphStoreTests
{
    [PostgresTheory]
    [InlineData("success")]
    [InlineData("grant")]
    [InlineData("user")]
    [InlineData("tenant")]
    [InlineData("membership")]
    [InlineData("all-owners")]
    [InlineData("late-save")]
    [InlineData("concurrent")]
    [InlineData("late-inactive")]
    public async Task ProtectedPlainHistoryPostgresSignedRestoreIsAtomicAndRechecksLiveAuthority(string mode)
    {
        var configured = Environment.GetEnvironmentVariable("KNOWZ_TEST_POSTGRES"); Assert.False(string.IsNullOrWhiteSpace(configured));
        var connection = new NpgsqlConnectionStringBuilder(configured); Assert.True(connection.Host is "localhost" or "127.0.0.1" && connection.Port == 18460);
        connection.Database = "postgres"; connection.Pooling = false;
        await using var admin = new NpgsqlConnection(connection.ConnectionString); await admin.OpenAsync();
        var name = "kc_history_" + Guid.NewGuid().ToString("N"); await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"",admin)) await create.ExecuteNonQueryAsync();
        connection.Database = name; var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(Guid.NewGuid());
        var actorProvider = Substitute.For<IPortableActorProvider>(); actorProvider.ActorId.Returns(Guid.NewGuid());
        var interceptor = new HistoryRevocationInterceptor(connection.ConnectionString,mode);
        var race = new HistoryRaceInterceptor(connection.ConnectionString);
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>().UseNpgsql(connection.ConnectionString, o => o.EnableRetryOnFailure()).AddInterceptors(interceptor,race).Options;
        var directory = Path.Combine(Path.GetTempPath(),name); var storage = new LocalFileStorageProvider(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Storage:Local:RootPath"] = directory }).Build(),NullLogger<LocalFileStorageProvider>.Instance);
        try
        {
            await using var db = new SelfHostedDbContext(options,tenant,actorProvider); await db.Database.MigrateAsync(); db.Tenants.Add(new() { Id = tenant.TenantId,Name = "Owned history fixture",Slug = name });
            var user = new User { Id = actorProvider.ActorId!.Value,TenantId = tenant.TenantId,Username = "history-native",IsActive = true,Role = Knowz.Core.Enums.UserRole.Admin }; db.Users.Add(user); await db.SaveChangesAsync();
            var membership = new UserTenantMembership { TenantId = tenant.TenantId,UserId = user.Id,IsActive = true,Role = Knowz.Core.Enums.UserRole.Admin }; db.UserTenantMemberships.Add(membership); await db.SaveChangesAsync();
            var graph = new SelfHostedGraphStore(db,tenant,new(db,tenant,storage,NullLogger<PortableImportService>.Instance),new(db,tenant,storage,Options.Create(new SelfHostedOptions()),NullLogger<PortableExportService>.Instance),storage);
            var access = new VaultAccessService(db); var source = await FieldHistoryFixtureAsync(); using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var archive = new MemoryStream(); var sourceReceipt = await KnowzArchiveWriter.WriteAsync(archive,[source],null,new() { SigningKey = signer,SourceSnapshot = "native-history-source" }); archive.Position = 0;
            using var coordinator = new ArchiveImportCoordinator(); var adapter = new SelfHostedArchiveImportAdapter(db,tenant,graph,access); var actor = new ArchiveActor(tenant.TenantId,user.Id.ToString("D"));
            var plan = await coordinator.CreatePlanAsync(archive,actor,new() { ExpectedSignerFingerprint = sourceReceipt.SignerFingerprint,IdempotencyKey = "native-history",Strategy = "skip" },adapter); var committed = await coordinator.CommitAsync(plan.PlanId,actor,adapter); Assert.Equal(committed.RequiredPayloadCount,committed.PreservedPayloadCount);
            user.Role = Knowz.Core.Enums.UserRole.User; membership.Role = Knowz.Core.Enums.UserRole.User; await db.SaveChangesAsync();
            var item = await db.KnowledgeItems.AsNoTracking().SingleAsync(); var history = await db.KnowledgeVersions.AsNoTracking().SingleAsync(); var vault = (await db.KnowledgeVaults.SingleAsync()).VaultId;
            db.UserPermissions.Add(new UserPermissions { TenantId = tenant.TenantId,UserId = user.Id,HasAllVaultsAccess = false }); var grant = new UserVaultAccess { TenantId = tenant.TenantId,UserId = user.Id,VaultId = vault,CanRead = true,CanWrite = true }; db.UserVaultAccess.Add(grant);
            if (mode == "all-owners") { var other = new Vault { TenantId = tenant.TenantId,Name = "Other owner" }; db.Vaults.Add(other); db.KnowledgeVaults.Add(new KnowledgeVault { KnowledgeId = item.Id,VaultId = other.Id }); }
            await db.SaveChangesAsync(); var referenceCount = await db.ShPayloadKeyReferences.CountAsync();
            var lifecycle = new SelfHostedSealedStore(db,tenant,NullLogger<SelfHostedSealedStore>.Instance);
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Services.AddSingleton(db); builder.Services.AddSingleton(lifecycle); builder.Services.AddSingleton(graph); builder.Services.AddSingleton(new SelfHostedSealedFiles(db,tenant,lifecycle,storage)); builder.Services.AddSingleton<IVaultAccessService>(access);
            await using var app = builder.Build(); app.Use(async (http,next) => { http.User = new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier,user.Id.ToString()),new("tenantId",tenant.TenantId.ToString()),new(ClaimTypes.Role,"User")],"synthetic")); await next(); }); app.MapEncryptedVaultEndpoints(); await app.StartAsync(); using var client = app.GetTestClient();
            var payload = await client.GetFromJsonAsync<JsonElement>($"/api/v1/vaults/{vault}/encryption/payloads/knowledge/{item.Id}");
            var historical = await client.GetFromJsonAsync<JsonElement>($"/api/v1/vaults/{vault}/encryption/payloads/version/{history.Id}"); Assert.Equal("knowledge:title",DecryptCurrentField(PlainVersionSnapshot.ReadEncryptedFields(historical.GetProperty("record").GetProperty("snapshotJson").GetString())!));
            interceptor.Target = mode == "late-inactive" ? item.Id : mode == "grant" ? grant.Id : mode == "user" ? user.Id : mode == "membership" ? membership.Id : tenant.TenantId; interceptor.Armed = mode is not ("success" or "all-owners" or "concurrent");
            race.Target = item.Id; race.Armed = mode == "concurrent";
            using var request = new HttpRequestMessage(HttpMethod.Post,$"/api/v1/encrypted/items/{item.Id}/versions/{history.Id}/restore?vaultId={vault}"); request.Headers.TryAddWithoutValidation("If-Match",payload.GetProperty("revision").GetString());
            if (mode == "late-save") await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(request));
            else { using var response = await client.SendAsync(request); Assert.Equal(mode == "success" ? HttpStatusCode.OK : mode == "concurrent" ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden,response.StatusCode); }
            if (interceptor.Armed == false && mode is not ("success" or "all-owners" or "concurrent")) Assert.True(interceptor.Fired);
            if (mode == "concurrent") Assert.True(race.Fired);
            await using var fresh = new SelfHostedDbContext(new DbContextOptionsBuilder<SelfHostedDbContext>().UseNpgsql(connection.ConnectionString).Options,tenant);
            var actual = await fresh.KnowledgeItems.SingleAsync(); Assert.Equal(mode == "success" ? source.PlainVersions![0].Content : mode == "concurrent" ? "Concurrent winner body" : item.Content,actual.Content);
            Assert.Null(actual.PortableRecoveryState); Assert.Equal(item.EncryptedFieldsJson,actual.EncryptedFieldsJson); Assert.Equal(mode == "success" ? 2 : 1,await fresh.KnowledgeVersions.CountAsync()); Assert.Equal(mode == "success" ? referenceCount + 1 : referenceCount,await fresh.ShPayloadKeyReferences.CountAsync());
            if (mode == "success")
            {
                var freshGraph = new SelfHostedGraphStore(fresh,tenant,new(fresh,tenant,storage,NullLogger<PortableImportService>.Instance),new(fresh,tenant,storage,Options.Create(new SelfHostedOptions()),NullLogger<PortableExportService>.Instance),storage);
                var output = await new SelfHostedArchiveExportService(fresh,tenant,freshGraph,new EphemeralDataProtectionProvider()).ExportAsync(new() { Type = ExportScopeType.Vault,VaultIds = [vault] },false,default);
                await using (output.Stream) { using var returned = await KnowzArchiveReader.OpenAsync(output.Stream,new() { ExpectedSignerFingerprint = output.Receipt.SignerFingerprint }); var package = PortableGraphPackages.Combine(returned.Packages);
                    var old = package.PlainVersions!.Single(v => v.Id == history.Id); Assert.Equal(source.PlainVersions![0].SnapshotJson,old.SnapshotJson); Assert.Equal(source.PlainVersions[0].OriginalKnowledgeId,old.OriginalKnowledgeId);
                    Assert.All(package.PlainVersions,v => Assert.Equal("knowledge:title",DecryptCurrentField(PlainVersionSnapshot.ReadEncryptedFields(v.SnapshotJson)!)));
                    Assert.All(package.PayloadKeyReferences!,r => Assert.Equal(source.PayloadKeyReferences![0].OriginalBindingId,r.OriginalBindingId)); Assert.Equal(2,package.PlainVersions.Count);
                }
            }
        }
        finally { await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)",admin); await drop.ExecuteNonQueryAsync(); if (Directory.Exists(directory)) Directory.Delete(directory,true); }
    }
    private sealed class HistoryRaceInterceptor(string connectionString) : DbCommandInterceptor
    {
        public bool Armed { get; set; } public bool Fired { get; private set; } public Guid Target { get; set; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (!Armed || !command.CommandText.StartsWith("UPDATE \"KnowledgeItems\"", StringComparison.Ordinal)) return result;
            Armed = false;
            await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct);
            await using var race = new NpgsqlCommand("UPDATE \"KnowledgeItems\" SET \"Content\" = 'Concurrent winner body' WHERE \"Id\" = @id",connection);
            race.Parameters.AddWithValue("id",Target); Assert.Equal(1,await race.ExecuteNonQueryAsync(ct)); Fired = true; return result;
        }
    }
    private sealed class HistoryRevocationInterceptor(string connectionString,string mode) : SaveChangesInterceptor
    {
        public bool Armed { get; set; } public bool Fired { get; private set; } public Guid Target { get; set; }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,int result,CancellationToken ct = default)
        {
            if (!Armed) return result; Armed = false; Fired = true;
            if (mode == "late-save") throw new InvalidOperationException("Synthetic late history save failure");
            if (mode == "late-inactive")
            {
                var db = (SelfHostedDbContext)eventData.Context!;
                Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"KnowledgeItems\" SET \"PortableRecoveryState\" = 'dependency-bound', \"CreatedByUserId\" = {db.PortableActorId} WHERE \"Id\" = {Target}",ct));
                return result;
            }
            await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct);
            var sql = mode switch { "grant" => "UPDATE \"UserVaultAccess\" SET \"CanWrite\" = FALSE WHERE \"Id\" = @id", "user" => "UPDATE \"Users\" SET \"IsActive\" = FALSE WHERE \"Id\" = @id", "membership" => "UPDATE \"UserTenantMemberships\" SET \"IsActive\" = FALSE WHERE \"Id\" = @id", _ => "UPDATE \"Tenants\" SET \"IsActive\" = FALSE WHERE \"Id\" = @id" };
            await using var command = new NpgsqlCommand(sql,connection); command.Parameters.AddWithValue("id",Target); Assert.Equal(1,await command.ExecuteNonQueryAsync(ct)); return result;
        }
    }
}
