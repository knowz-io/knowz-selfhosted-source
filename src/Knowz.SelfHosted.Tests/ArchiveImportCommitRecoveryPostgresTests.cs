using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

[Collection("OwnedPostgresSerial")]
public sealed class ArchiveImportCommitRecoveryPostgresTests
{
    [PostgresTheory]
    [InlineData("before", false)]
    [InlineData("after", false)]
    [InlineData("before", true)]
    [InlineData("after", true)]
    public async Task LostResponseRecoversExactlyOnceOnlyAfterCurrentAuthorityIsRestored(string phase, bool peer)
    {
        await using var f = await Fixture.Create(peer, phase);
        var plan = await f.Plan(); f.Fault.Armed = true;
        await Assert.ThrowsAsync<ArchiveOperationException>(() => f.Coordinator.CommitAsync(plan.PlanId, f.Actor, f.Adapter));
        Assert.True(f.Fault.Injected); f.Db.ChangeTracker.Clear();
        var journal = await f.Db.ShArchiveImportJournals.AsNoTracking().SingleAsync();
        Assert.Equal(phase == "after" ? "committed" : "cancelled", journal.Status);
        await f.Db.Users.Where(u => u.Id == f.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        var denied = await Assert.ThrowsAsync<ArchiveOperationException>(() => f.Coordinator.CommitAsync(plan.PlanId, f.Actor, f.Adapter));
        Assert.Equal(403, denied.StatusCode);
        await f.Db.Users.Where(u => u.Id == f.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, true));
        if (phase == "before")
        {
            // An uncommitted stage is cancelled and requires a new upload; it must not be recovered as committed.
            await Assert.ThrowsAsync<ArchiveOperationException>(() => f.Coordinator.CommitAsync(plan.PlanId, f.Actor, f.Adapter));
            Assert.Empty(await f.Db.ShSealedKnowledge.AsNoTracking().ToListAsync());
            Assert.Empty(Directory.EnumerateFiles(f.Directory, "*", SearchOption.AllDirectories));
            return;
        }
        var recovered = await f.Coordinator.CommitAsync(plan.PlanId, f.Actor, f.Adapter);
        Assert.Equal(journal.CommitId, recovered.CommitId);
        Assert.Equal(plan.RequiredPayloadCount, recovered.PreservedPayloadCount);
        Assert.Equal("not-performed", recovered.ClientVerificationState);
        Assert.Equal(recovered.CommitId, (await f.Coordinator.CommitAsync(plan.PlanId, f.Actor, f.Adapter)).CommitId);
        Assert.Single(await f.Db.ShArchiveImportJournals.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.ShCryptoContextImportAnchors.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.ShSealedKnowledge.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.ShSealedFiles.AsNoTracking().ToListAsync());
        Assert.Single(Directory.EnumerateFiles(f.Directory, "*", SearchOption.AllDirectories));
        Assert.Equal(1, f.Uploads);
    }

    [PostgresTheory]
    [InlineData("user", "binary")]
    [InlineData("membership", "binary")]
    [InlineData("vault", "binary")]
    [InlineData("tenant", "binary")]
    [InlineData("user", "save")]
    [InlineData("membership", "save")]
    [InlineData("vault", "save")]
    [InlineData("tenant", "save")]
    [InlineData("peer", "binary")]
    [InlineData("peer", "save")]
    [InlineData("grant", "binary")]
    [InlineData("grant", "save")]
    public async Task RevocationDuringStagedReadOrFinalSaveCannotActivateAnyGraph(string authority, string point)
    {
        await using var f = await Fixture.Create(authority == "peer", "none");
        var plan = await f.Plan();
        if (point == "binary") f.OnDownload = () => f.Revoke(authority, f.Db);
        else f.Fault.OnFinalSave = context => f.Revoke(authority, context);
        var denied = await Assert.ThrowsAsync<ArchiveOperationException>(() => f.Coordinator.CommitAsync(plan.PlanId, f.Actor, f.Adapter));
        Assert.Equal(authority == "peer" ? 404 : 403, denied.StatusCode); Assert.True(f.Revoked);
        f.Db.ChangeTracker.Clear();
        Assert.Empty(await f.Db.KnowledgeItems.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.ShSealedKnowledge.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.ShSealedFiles.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.ShCryptoContextImportAnchors.AsNoTracking().ToListAsync());
        Assert.NotEqual("committed", (await f.Db.ShArchiveImportJournals.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(Directory.EnumerateFiles(f.Directory, "*", SearchOption.AllDirectories));
        Assert.Equal(1, f.Uploads); // Only the preview's private stage was uploaded, then removed.
    }

    [PostgresTheory]
    [InlineData("archive")]
    [InlineData("manifest")]
    [InlineData("mapping")]
    [InlineData("actor")]
    [InlineData("actor-id")]
    [InlineData("peer")]
    [InlineData("options")]
    [InlineData("receipt-plan")]
    [InlineData("receipt-source")]
    [InlineData("receipt-trust")]
    [InlineData("receipt-count")]
    [InlineData("receipt-commit")]
    [InlineData("receipt-client")]
    [InlineData("receipt-omission")]
    public async Task CommittedRecoveryRejectsAlteredEvidenceWithoutPublishingAgain(string mutation)
    {
        await using var f = await Fixture.Create(false, "after"); var plan = await f.Plan(); f.Fault.Armed = true;
        await Assert.ThrowsAsync<ArchiveOperationException>(() => f.Coordinator.CommitAsync(plan.PlanId, f.Actor, f.Adapter));
        Assert.True(f.Fault.Injected); f.Db.ChangeTracker.Clear();
        var journal = await f.Db.ShArchiveImportJournals.SingleAsync();
        var receipt = JsonSerializer.Deserialize<ArchiveCommitReceipt>(journal.CommitReceiptJson!)!;
        switch (mutation)
        {
            case "archive": journal.ArchiveId = Guid.NewGuid(); break;
            case "manifest": journal.ManifestSha256 = new string('f', 64); break;
            case "mapping": journal.MappingSha256 = new string('f', 64); break;
            case "actor": journal.ActorNamespace = Guid.NewGuid().ToString("D"); break;
            case "actor-id": journal.ActorId = Guid.NewGuid(); break;
            case "peer": journal.PeerBindingSha256 = new string('f', 64); break;
            case "options": journal.ImportOptionsJson = "{}"; break;
            case "receipt-plan": receipt.PlanId = Guid.NewGuid(); break;
            case "receipt-source": receipt.SourceSnapshot = "changed"; break;
            case "receipt-trust": receipt.SourceTrust = "unverified"; break;
            case "receipt-count": receipt.PreservedPayloadCount--; break;
            case "receipt-commit": receipt.CommitId = Guid.NewGuid(); break;
            case "receipt-client": receipt.ClientVerificationState = "verified"; break;
            case "receipt-omission": receipt.Omissions.Add(new() { OwnerKind = "knowledge", OwnerId = Guid.NewGuid(), Reason = "changed" }); break;
        }
        journal.CommitReceiptJson = JsonSerializer.Serialize(receipt); await f.Db.SaveChangesAsync();
        var denied = await Assert.ThrowsAsync<ArchiveOperationException>(() => f.Coordinator.CommitAsync(plan.PlanId, f.Actor, f.Adapter));
        Assert.Equal(409, denied.StatusCode); f.Db.ChangeTracker.Clear();
        Assert.Single(await f.Db.ShSealedKnowledge.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.ShSealedFiles.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.ShCryptoContextImportAnchors.AsNoTracking().ToListAsync());
        Assert.Single(Directory.EnumerateFiles(f.Directory, "*", SearchOption.AllDirectories)); Assert.Equal(1, f.Uploads);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Guid TenantId = Guid.NewGuid(), UserId = Guid.NewGuid(), VaultId = Guid.NewGuid();
        internal readonly string Directory = Path.Combine(Path.GetTempPath(), "kc-sh-archive-fault-" + Guid.NewGuid().ToString("N"));
        internal readonly ArchiveImportCoordinator Coordinator = new();
        internal readonly ECDsa Signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal readonly ArchiveFault Fault;
        internal SelfHostedDbContext Db = null!;
        internal SelfHostedArchiveImportAdapter Adapter = null!;
        internal ArchiveActor Actor = null!;
        internal PortableExportPackage Package = null!;
        internal Func<Task>? OnDownload;
        internal bool Revoked;
        internal int Uploads;
        private readonly string _database = "kc_sh_archive_" + Guid.NewGuid().ToString("N");
        private NpgsqlConnection _admin = null!;
        private bool _databaseCreated;
        private readonly bool _peer;
        private ShArchivePeerBinding? _binding;
        private Fixture(bool peer, string phase) { _peer = peer; Fault = new(phase); }
        internal static async Task<Fixture> Create(bool peer, string phase)
        {
            var f = new Fixture(peer, phase);
            try { await f.Initialize(); return f; }
            catch (Exception initializationError)
            {
                try { await f.DisposeAsync(); }
                catch (Exception cleanupError) { initializationError.Data["FixtureCleanupException"] = cleanupError; }
                throw;
            }
        }
        private async Task Initialize()
        {
            var configured = Environment.GetEnvironmentVariable("KNOWZ_TEST_POSTGRES"); Assert.False(string.IsNullOrWhiteSpace(configured));
            var connection = new NpgsqlConnectionStringBuilder(configured); Assert.True(connection.Host is "127.0.0.1" or "localhost" && connection.Port == 18460);
            connection.Database = "postgres"; connection.Pooling = false; _admin = new(connection.ConnectionString); await _admin.OpenAsync();
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{_database}\"", _admin)) await create.ExecuteNonQueryAsync();
            _databaseCreated = true;
            connection.Database = _database;
            var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(TenantId);
            Db = new(new DbContextOptionsBuilder<SelfHostedDbContext>().UseNpgsql(connection.ConnectionString).AddInterceptors(Fault).Options, tenant);
            await Db.Database.MigrateAsync();
            Db.Tenants.Add(new() { Id = TenantId, Name = "Owned archive fault fixture", Slug = _database });
            Db.Users.Add(new() { Id = UserId, TenantId = TenantId, Username = "archive-owner", Role = UserRole.Admin, IsActive = true, ApiKey = "ksh_owned-synthetic" });
            Db.UserTenantMemberships.Add(new() { UserId = UserId, TenantId = TenantId, Role = UserRole.Admin, IsActive = true });
            Db.Vaults.Add(new() { Id = VaultId, TenantId = TenantId, Name = "Owned destination" });
            Db.UserPermissions.Add(new() { UserId = UserId, TenantId = TenantId, HasAllVaultsAccess = false });
            Db.UserVaultAccess.Add(new() { UserId = UserId, TenantId = TenantId, VaultId = VaultId, CanRead = true, CanWrite = true, CanManage = true });
            await Db.SaveChangesAsync();
            var storage = new LocalFileStorageProvider(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Local:RootPath"] = Directory }).Build(), NullLogger<LocalFileStorageProvider>.Instance);
            var proxy = Substitute.For<IFileStorageProvider>();
            proxy.UploadAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(async call => { Uploads++; return await storage.UploadAsync(call.ArgAt<Guid>(0), call.ArgAt<Guid>(1), call.ArgAt<Stream>(2), call.ArgAt<string>(3), call.ArgAt<CancellationToken>(4)); });
            proxy.DownloadAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(async call =>
            { var result = await storage.DownloadAsync(call.ArgAt<Guid>(0), call.ArgAt<Guid>(1), call.ArgAt<CancellationToken>(2)); if (OnDownload != null) { var hook = OnDownload; OnDownload = null; await hook(); } return result; });
            proxy.DeleteAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call => storage.DeleteAsync(call.ArgAt<Guid>(0), call.ArgAt<Guid>(1), call.ArgAt<CancellationToken>(2)));
            proxy.ExistsAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call => storage.ExistsAsync(call.ArgAt<Guid>(0), call.ArgAt<Guid>(1), call.ArgAt<CancellationToken>(2)));
            var graph = new SelfHostedGraphStore(Db, tenant, new(Db, tenant, proxy, NullLogger<PortableImportService>.Instance), new(Db, tenant, proxy, Options.Create(new SelfHostedOptions()), NullLogger<PortableExportService>.Instance), proxy);
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web); json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            Package = JsonSerializer.Deserialize<PortableExportPackage>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portable-graph-v1.json")), json)!;
            var access = new VaultAccessService(Db); var peers = new SelfHostedArchivePeerService(Db, tenant, access, new SelfHostedPeerChallengeStore());
            Adapter = new(Db, tenant, graph, access, peers); Actor = new(TenantId, UserId.ToString("D"));
            if (_peer)
            {
                var credential = ArchivePeerProof.Fingerprint(Encoding.UTF8.GetBytes("ksh_owned-synthetic"));
                var challenge = await peers.ChallengeAsync(UserId, credential, VaultId, new() { SourceTenantId = Package.SourceTenantId, SourceVaultId = Package.Data.Vaults.Single().Id,
                    SourceBaseUrl = "https://owned-fixture.example", SignerFingerprint = ArchivePeerProof.Fingerprint(Signer.ExportSubjectPublicKeyInfo()), SignerPublicKey = Convert.ToBase64String(Signer.ExportSubjectPublicKeyInfo()) }, default);
                _binding = await peers.AcceptAsync(UserId, credential, challenge.ChallengeId, ArchivePeerProof.Sign(challenge, Signer), default);
                Adapter = Adapter.ForPeer(_binding); Actor = Adapter.PeerActor;
            }
        }
        internal async Task<ArchiveImportPlan> Plan()
        {
            using var input = new MemoryStream(); var receipt = await KnowzArchiveWriter.WriteAsync(input, [Package], null, new() { SigningKey = Signer, SourceSnapshot = "owned-sh-commit-fault" }); input.Position = 0;
            return await Coordinator.CreatePlanAsync(input, Actor, new() { TargetVaultId = VaultId, Strategy = "overwrite", ExpectedSignerFingerprint = receipt.SignerFingerprint,
                SourceReceipt = _peer ? receipt : null, IdempotencyKey = _peer ? $"sync:{_binding!.Id:D}:owned-fault" : "owned-fault" }, Adapter);
        }
        internal async Task Revoke(string authority, SelfHostedDbContext db)
        {
            Revoked = true;
            switch (authority)
            {
                case "user": await db.Users.Where(u => u.Id == UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false)); break;
                case "membership": await db.UserTenantMemberships.Where(m => m.UserId == UserId && m.TenantId == TenantId).ExecuteUpdateAsync(s => s.SetProperty(m => m.IsActive, false)); break;
                case "vault": await db.Vaults.Where(v => v.Id == VaultId).ExecuteUpdateAsync(s => s.SetProperty(v => v.IsDeleted, true)); break;
                case "tenant": await db.Tenants.Where(t => t.Id == TenantId).ExecuteUpdateAsync(s => s.SetProperty(t => t.IsActive, false)); break;
                case "peer": await db.ShArchivePeerBindings.Where(p => p.Id == _binding!.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsEnabled, false)); break;
                case "grant": await db.UserVaultAccess.Where(p => p.UserId == UserId && p.VaultId == VaultId).ExecuteUpdateAsync(s => s.SetProperty(p => p.CanWrite, false)); break;
                default: throw new ArgumentOutOfRangeException(nameof(authority));
            }
        }
        public async ValueTask DisposeAsync()
        {
            Coordinator.Dispose(); Signer.Dispose(); if (Db != null) await Db.DisposeAsync();
            if (_admin != null)
            {
                try
                {
                    if (_databaseCreated && _admin.State == System.Data.ConnectionState.Open)
                    {
                        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", _admin);
                        await drop.ExecuteNonQueryAsync();
                    }
                }
                finally { await _admin.DisposeAsync(); }
            }
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }

    private sealed class ArchiveFault(string phase) : DbTransactionInterceptor, ISaveChangesInterceptor
    {
        internal bool Armed, Injected;
        internal Func<SelfHostedDbContext, Task>? OnFinalSave;
        public ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (OnFinalSave == null || eventData.Context!.ChangeTracker.Entries<ShArchiveImportJournal>().All(e => e.Entity.Status != "committed")) return ValueTask.FromResult(result);
            return Apply(eventData, result);
        }
        private async ValueTask<int> Apply(SaveChangesCompletedEventData eventData, int result)
        { var hook = OnFinalSave!; OnFinalSave = null; await hook((SelfHostedDbContext)eventData.Context!); return result; }
        private bool Inject(string point, DbContext? db)
        {
            if (!Armed || Injected || phase != point || db?.ChangeTracker.Entries<ShArchiveImportJournal>().Any(e => e.Entity.Status == "committed") != true) return false;
            Injected = true; return true;
        }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        { if (Inject("before", eventData.Context)) throw new TimeoutException("Owned fault before archive COMMIT."); return ValueTask.FromResult(result); }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        { if (Inject("after", eventData.Context)) throw new TimeoutException("Owned lost archive COMMIT acknowledgement."); return Task.CompletedTask; }
    }
}
