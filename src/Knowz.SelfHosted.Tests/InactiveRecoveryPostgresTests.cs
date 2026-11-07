using System.Security.Cryptography;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;
public sealed partial class SelfHostedGraphStoreTests
{
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealPostgresInactiveShelfFreshUpgradeRestartOwnerFences(bool upgrade)
    {
        var connection = Environment.GetEnvironmentVariable("KNOWZ_TEST_POSTGRES"); Assert.False(string.IsNullOrEmpty(connection));
        var database = "kc_shelf_" + Guid.NewGuid().ToString("N");
        await using var server = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection) { Database = "postgres", Pooling = false }.ConnectionString);
        await server.OpenAsync(); await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", server)) await create.ExecuteNonQueryAsync();
        var uncertainCommit = new CommitResponseLost(); var transientWrite = new ImportWriteTransient();
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>().UseNpgsql(new NpgsqlConnectionStringBuilder(connection) { Database = database, Pooling = false }.ConnectionString, pg => pg.EnableRetryOnFailure()).AddInterceptors(uncertainCommit, transientWrite).Options;
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant); var owner = Guid.NewGuid(); var actor = new ShelfActor { ActorId = owner };
        var (source, _, _) = Fixture(); source.RequiredCapabilities!.Add(SuspendedReleaseProfile.Capability);
        source.SuspendedReleasePolicies = [new() { KnowledgeId = source.SealedItems![0].Id, OriginalKnowledgeId = source.SealedItems[0].OriginalItemId,
            OriginalLockId = Guid.NewGuid(), OriginalOwnerUserId = Guid.NewGuid(), SourceTenantId = source.SourceTenantId, OriginalIsSealed = true }];
        Guid itemId, planId; ArchiveCommitReceipt originalReceipt;
        try
        {
            await using (var db = new SelfHostedDbContext(options, tenant, actor))
            {
                if (upgrade)
                {
                    await db.GetService<IMigrator>().MigrateAsync("20261006155550_AddCustodyPeerFencesAndFileHistory");
                    db.Vaults.Add(new() { TenantId = _tenant, Name = "Ordinary pre-upgrade vault" }); await db.SaveChangesAsync();
                }
                await db.Database.MigrateAsync(); Assert.Empty(await db.Database.GetPendingMigrationsAsync());
                db.Tenants.Add(new() { Id = _tenant, Name = "Recovery synthetic tenant", Slug = database });
                db.Users.Add(new() { Id = owner, TenantId = _tenant, Username = "recovery-owner", IsActive = true, Role = UserRole.Admin }); await db.SaveChangesAsync();
                var graph = Graph(db); var adapter = new SelfHostedArchiveImportAdapter(db, tenant, graph, new VaultAccessService(db)); using var coordinator = new ArchiveImportCoordinator(); using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                using var input = new MemoryStream(); var receipt = await KnowzArchiveWriter.WriteAsync(input, [source], null, new() { SigningKey = signer, SourceSnapshot = "inactive-shelf" }); input.Position = 0;
                var identity = new ArchiveActor(_tenant, owner.ToString("D")); var plan = await coordinator.CreatePlanAsync(input, identity, new() { ExpectedSignerFingerprint = receipt.SignerFingerprint, IdempotencyKey = "inactive-shelf" }, adapter);
                uncertainCommit.Armed = upgrade; transientWrite.Armed = !upgrade;
                var committed = await coordinator.CommitAsync(plan.PlanId, identity, adapter); Assert.Equal(committed.RequiredPayloadCount, committed.PreservedPayloadCount); Assert.Equal(upgrade, uncertainCommit.Injected); Assert.Equal(!upgrade, transientWrite.Injected);
                originalReceipt = committed; planId = plan.PlanId; itemId = Guid.Parse(plan.Mapping[$"knowledge:{source.SealedItems![0].Id:D}"]);
                Assert.Equal(planId, (await db.Set<ShInactiveRecoveryRecord>().SingleAsync()).ImportPlanId);
            }
            await using (var db = new SelfHostedDbContext(options, tenant, actor))
            {
                var exported = await Graph(db).ExportAsync(new() { Type = ExportScopeType.Tenant }, false, default);
                Assert.Equal(source.SealedItems![0].Ciphertext, Assert.Single(exported.SealedItems!).Ciphertext); Assert.Single(exported.SuspendedReleasePolicies!);
                Assert.Equal("committed", (await db.ShArchiveImportJournals.SingleAsync(j => j.PlanId == planId)).Status);
                var restartedAdapter = new SelfHostedArchiveImportAdapter(db, tenant, Graph(db), new VaultAccessService(db));
                var receipt = await restartedAdapter.GetCommittedReceiptAsync(planId, new(_tenant, owner.ToString("D")), default);
                Assert.NotNull(receipt); Assert.Equal(originalReceipt.CommitId, receipt.CommitId); Assert.Equal(originalReceipt.CommittedAt, receipt.CommittedAt); Assert.Equal(originalReceipt.ManifestSha256, receipt.ManifestSha256); Assert.Equal(originalReceipt.RequiredPayloadCount, receipt.PreservedPayloadCount);
                Assert.Null(await restartedAdapter.GetCommittedReceiptAsync(planId, new(_tenant, Guid.NewGuid().ToString("D")), default));
                var ownerUser = await db.Users.SingleAsync(u => u.Id == owner); ownerUser.IsActive = false; await db.SaveChangesAsync();
                var denied = await Assert.ThrowsAsync<ArchiveOperationException>(() => restartedAdapter.GetCommittedReceiptAsync(planId, new(_tenant, owner.ToString("D")), default)); Assert.Equal(403, denied.StatusCode);
                ownerUser.IsActive = true; await db.SaveChangesAsync();

                var item = await db.KnowledgeItems.SingleAsync(k => k.Id == itemId); item.Content = "forbidden edit";
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync()); db.ChangeTracker.Clear();
                actor.ActorId = Guid.NewGuid(); Assert.False(await db.KnowledgeItems.AnyAsync(k => k.Id == itemId));
                var other = await Graph(db).ExportAsync(new() { Type = ExportScopeType.Tenant }, false, default); Assert.Empty(other.SealedFiles!); Assert.Empty(other.SuspendedReleasePolicies ?? []);
                actor.ActorId = null; Assert.False(await db.KnowledgeItems.AnyAsync(k => k.Id == itemId)); Assert.NotEmpty(await db.KnowledgeItems.ToListAsync());
            }
        }
        finally { await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", server); await drop.ExecuteNonQueryAsync(); }
        SelfHostedGraphStore Graph(SelfHostedDbContext db) => new(db, tenant, new(db, tenant, _storage, NullLogger<PortableImportService>.Instance),
            new(db, tenant, _storage, Options.Create(new SelfHostedOptions()), NullLogger<PortableExportService>.Instance), _storage);
    }
    private sealed class CommitResponseLost : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool Injected { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Armed && !Injected) { Injected = true; throw new TimeoutException("Synthetic response lost after the database committed."); }
            return Task.CompletedTask;
        }
    }

    private sealed class ImportWriteTransient : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Injected { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && !Injected && eventData.Context!.ChangeTracker.Entries<Knowledge>().Any(e => e.State == EntityState.Added))
            { Injected = true; throw new TimeoutException("Synthetic transient database import write."); }
            return ValueTask.FromResult(result);
        }
    }

}
