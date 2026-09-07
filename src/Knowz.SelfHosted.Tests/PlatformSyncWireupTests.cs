using System.Reflection;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Application.Validators;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// Wire-up / DI / integration tests for the platform sync feature. Verifies:
///   1. All services register and resolve with the expected concrete types.
///   2. The <c>KnowzPlatformSync</c> named HttpClient has correct timeout/buffer/redirect config.
///   3. <see cref="PlatformConnectionMigrationService"/> is registered as a hosted service.
///   4. The migration service behaviour: idempotency, empty DB, legacy backfill, mixed state,
///      multi-tenant isolation, round-trip decryption.
///   5. Migration schema: PlatformSyncRuns + PlatformConnections tables, unique tenant index,
///      filtered in-progress index, and VaultSyncLinks.PlatformConnectionId FK column.
/// </summary>
public class PlatformSyncWireupTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private const string MasterPurpose = "Knowz.SelfHosted.PlatformSync";

    // ---------- 1. DI Registration Tests ----------

    [Fact]
    public void Di_IPlatformConnectionService_Resolves()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService<IPlatformConnectionService>();

        Assert.NotNull(service);
        Assert.IsType<PlatformConnectionService>(service);
    }

    [Fact]
    public void Di_IPlatformAuditLog_Resolves()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService<IPlatformAuditLog>();

        Assert.NotNull(service);
        Assert.IsType<PlatformAuditLogService>(service);
    }

    [Fact]
    public void Di_IPlatformSyncRateLimiter_Resolves_AsSingleton()
    {
        using var provider = BuildProvider();

        // Singleton: same instance across two independent scopes.
        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();
        var a = scope1.ServiceProvider.GetRequiredService<IPlatformSyncRateLimiter>();
        var b = scope2.ServiceProvider.GetRequiredService<IPlatformSyncRateLimiter>();

        Assert.NotNull(a);
        Assert.IsType<PlatformSyncRateLimiter>(a);
        Assert.Same(a, b);
    }

    [Fact]
    public void Di_IUrlValidator_Resolves_AsSingleton()
    {
        using var provider = BuildProvider();

        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();
        var a = scope1.ServiceProvider.GetRequiredService<IUrlValidator>();
        var b = scope2.ServiceProvider.GetRequiredService<IUrlValidator>();

        Assert.NotNull(a);
        Assert.IsType<PlatformUrlValidator>(a);
        Assert.Same(a, b);
    }

    [Fact]
    public void Di_IVaultSyncOrchestrator_Resolves()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService<IVaultSyncOrchestrator>();

        Assert.NotNull(service);
        Assert.IsType<VaultSyncOrchestrator>(service);
    }

    [Fact]
    public void Di_IPlatformSyncClient_Resolves()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService<IPlatformSyncClient>();

        Assert.NotNull(service);
        Assert.IsType<PlatformSyncClient>(service);
    }

    // ---------- 2. HttpClient Configuration Test ----------

    [Fact]
    public void HttpClient_KnowzPlatformSync_HasCorrectConfiguration()
    {
        using var provider = BuildProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        using var client = factory.CreateClient("KnowzPlatformSync");

        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
        Assert.Equal(50L * 1024 * 1024, client.MaxResponseContentBufferSize);
    }

    [Fact]
    public void HttpClient_KnowzPlatformSync_PrimaryHandler_DisallowsRedirects()
    {
        using var provider = BuildProvider();
        var messageHandlerFactory = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        using var topHandler = messageHandlerFactory.CreateHandler("KnowzPlatformSync");

        // The named client was configured with a primary HttpClientHandler that blocks redirects.
        // The handler pipeline wraps the primary handler; unwrap via the private InnerHandler
        // chain until we hit the HttpClientHandler and assert AllowAutoRedirect is false.
        var primary = UnwrapToPrimaryHandler(topHandler);
        var httpClientHandler = Assert.IsType<HttpClientHandler>(primary);
        Assert.False(httpClientHandler.AllowAutoRedirect);
    }

    private static HttpMessageHandler UnwrapToPrimaryHandler(HttpMessageHandler handler)
    {
        // DelegatingHandlers have an InnerHandler property (non-public on HttpMessageHandlerBuilder
        // outputs). Walk the chain until we find a non-delegating handler.
        var current = handler;
        while (current is DelegatingHandler delegating && delegating.InnerHandler is not null)
        {
            current = delegating.InnerHandler;
        }
        return current;
    }

    // ---------- 3. Hosted Service Registration ----------

    [Fact]
    public void HostedService_PlatformConnectionMigrationService_IsRegistered()
    {
        using var provider = BuildProvider();

        var hosted = provider.GetServices<IHostedService>().ToList();

        Assert.Contains(hosted, h => h is PlatformConnectionMigrationService);
    }

    // ---------- 4. PlatformConnectionMigrationService Behavior ----------

    [Fact]
    public async Task Migration_EmptyDb_NoOp()
    {
        using var harness = new MigrationHarness();

        await harness.Service.MigrateAsync(CancellationToken.None);

        Assert.Equal(0, await harness.Db.PlatformConnections.CountAsync());
        Assert.Equal(0, await harness.Db.VaultSyncLinks.CountAsync());
    }

    [Fact]
    public async Task Migration_Idempotent_NoExtraConnectionsOnSecondRun()
    {
        using var harness = new MigrationHarness();
        var vault = harness.SeedVault(TenantA);
        harness.SeedLegacyLink(vault.Id, "https://api.knowz.io", "ukz_plaintext123");

        await harness.Service.MigrateAsync(CancellationToken.None);
        var afterFirst = await harness.Db.PlatformConnections.CountAsync();
        var firstConnectionId = (await harness.Db.PlatformConnections.SingleAsync()).Id;

        await harness.Service.MigrateAsync(CancellationToken.None);
        var afterSecond = await harness.Db.PlatformConnections.CountAsync();

        Assert.Equal(1, afterFirst);
        Assert.Equal(1, afterSecond);
        Assert.Equal(firstConnectionId, (await harness.Db.PlatformConnections.SingleAsync()).Id);
    }

    [Fact]
    public async Task Migration_LegacyData_EncryptsAndSetsFk_AndRoundTripsPlaintext()
    {
        using var harness = new MigrationHarness();
        var vault = harness.SeedVault(TenantA);
        const string plaintext = "ukz_LegacyPlainKey7890";
        var link = harness.SeedLegacyLink(vault.Id, "https://api.knowz.io/", plaintext);

        await harness.Service.MigrateAsync(CancellationToken.None);

        var connection = await harness.Db.PlatformConnections.AsNoTracking().SingleAsync();
        Assert.Equal(TenantA, connection.TenantId);
        Assert.Equal("https://api.knowz.io", connection.PlatformApiUrl); // trailing slash stripped
        Assert.NotEqual(plaintext, connection.ApiKeyProtected);
        Assert.Equal("7890", connection.ApiKeyLast4);

        var updatedLink = await harness.Db.VaultSyncLinks.AsNoTracking().SingleAsync(l => l.Id == link.Id);
        Assert.Equal(connection.Id, updatedLink.PlatformConnectionId);

        // Round-trip: the ciphertext must decrypt back to plaintext under the same per-tenant purpose.
        var protector = harness.Dpp
            .CreateProtector(MasterPurpose)
            .CreateProtector($"{MasterPurpose}.{TenantA}");
        Assert.Equal(plaintext, protector.Unprotect(connection.ApiKeyProtected));
    }

    [Fact]
    public async Task Migration_MixedState_OnlyUnmigratedLinksProcessed()
    {
        using var harness = new MigrationHarness();
        var vault = harness.SeedVault(TenantA);

        // Already-migrated link: has a PlatformConnectionId already.
        var existingConnection = new PlatformConnection
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            PlatformApiUrl = "https://api.knowz.io",
            ApiKeyProtected = "preexisting-ciphertext",
            ApiKeyLast4 = "0000",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        harness.Db.PlatformConnections.Add(existingConnection);
        harness.Db.VaultSyncLinks.Add(new VaultSyncLink
        {
            LocalVaultId = vault.Id,
            RemoteVaultId = Guid.NewGuid(),
            PlatformConnectionId = existingConnection.Id,
        });

        // Second vault with an unmigrated legacy link, same tenant — should reuse the existing connection.
        var vault2 = harness.SeedVault(TenantA);
        var legacyLink = harness.SeedLegacyLink(vault2.Id, "https://api.knowz.io", "ukz_newPlaintextXYZ1");
        await harness.Db.SaveChangesAsync();

        await harness.Service.MigrateAsync(CancellationToken.None);

        // Only one PlatformConnection — the existing one was reused (find-or-create per tenant).
        Assert.Equal(1, await harness.Db.PlatformConnections.CountAsync());

        var reloaded = await harness.Db.VaultSyncLinks.AsNoTracking().SingleAsync(l => l.Id == legacyLink.Id);
        Assert.Equal(existingConnection.Id, reloaded.PlatformConnectionId);

        // Existing connection ciphertext was not rewritten.
        var connection = await harness.Db.PlatformConnections.AsNoTracking().SingleAsync();
        Assert.Equal("preexisting-ciphertext", connection.ApiKeyProtected);
    }

    [Fact]
    public async Task Migration_MultipleTenants_EachGetsOwnConnection()
    {
        using var harness = new MigrationHarness();
        var vaultA = harness.SeedVault(TenantA);
        var vaultB = harness.SeedVault(TenantB);
        harness.SeedLegacyLink(vaultA.Id, "https://api.knowz.io", "ukz_tenantA_key");
        harness.SeedLegacyLink(vaultB.Id, "https://api.knowz.io", "ukz_tenantB_key");

        await harness.Service.MigrateAsync(CancellationToken.None);

        var connections = await harness.Db.PlatformConnections.ToListAsync();
        Assert.Equal(2, connections.Count);
        Assert.Contains(connections, c => c.TenantId == TenantA);
        Assert.Contains(connections, c => c.TenantId == TenantB);

        var tenantA = connections.Single(c => c.TenantId == TenantA);
        var tenantB = connections.Single(c => c.TenantId == TenantB);
        Assert.NotEqual(tenantA.ApiKeyProtected, tenantB.ApiKeyProtected);

        // Each decrypts under its own per-tenant protector.
        var protectorA = harness.Dpp.CreateProtector(MasterPurpose)
            .CreateProtector($"{MasterPurpose}.{TenantA}");
        var protectorB = harness.Dpp.CreateProtector(MasterPurpose)
            .CreateProtector($"{MasterPurpose}.{TenantB}");
        Assert.Equal("ukz_tenantA_key", protectorA.Unprotect(tenantA.ApiKeyProtected));
        Assert.Equal("ukz_tenantB_key", protectorB.Unprotect(tenantB.ApiKeyProtected));
    }

    // ---------- 5. Migration Schema Tests ----------

    [Fact]
    public void Schema_PlatformSyncRunsTable_Exists_WithExpectedColumns()
    {
        using var harness = new MigrationHarness();
        var entity = harness.Db.Model.FindEntityType(typeof(PlatformSyncRun));

        Assert.NotNull(entity);
        var columns = entity!.GetProperties().Select(p => p.Name).ToHashSet();
        Assert.Contains(nameof(PlatformSyncRun.Id), columns);
        Assert.Contains(nameof(PlatformSyncRun.TenantId), columns);
        Assert.Contains(nameof(PlatformSyncRun.VaultSyncLinkId), columns);
        Assert.Contains(nameof(PlatformSyncRun.UserId), columns);
        Assert.Contains(nameof(PlatformSyncRun.Operation), columns);
        Assert.Contains(nameof(PlatformSyncRun.Direction), columns);
        Assert.Contains(nameof(PlatformSyncRun.Status), columns);
        Assert.Contains(nameof(PlatformSyncRun.StartedAt), columns);
        Assert.Contains(nameof(PlatformSyncRun.CompletedAt), columns);
        Assert.Contains(nameof(PlatformSyncRun.ErrorMessage), columns);
    }

    [Fact]
    public void Schema_PlatformConnectionsTable_HasUniqueIndexOnTenantId()
    {
        using var harness = new MigrationHarness();
        var entity = harness.Db.Model.FindEntityType(typeof(PlatformConnection));

        Assert.NotNull(entity);
        var tenantIdIndex = entity!
            .GetIndexes()
            .SingleOrDefault(ix => ix.Properties.Count == 1
                && ix.Properties[0].Name == nameof(PlatformConnection.TenantId));
        Assert.NotNull(tenantIdIndex);
        Assert.True(tenantIdIndex!.IsUnique);
    }

    [Fact]
    public void Schema_VaultSyncLinks_HasPlatformConnectionIdColumn()
    {
        using var harness = new MigrationHarness();
        var entity = harness.Db.Model.FindEntityType(typeof(VaultSyncLink));

        Assert.NotNull(entity);
        var prop = entity!.FindProperty(nameof(VaultSyncLink.PlatformConnectionId));
        Assert.NotNull(prop);
        Assert.True(prop!.IsNullable);
        Assert.Equal(typeof(Guid?), prop.ClrType);
    }

    [Fact]
    public void Schema_PlatformSyncRuns_HasFilteredInProgressIndex()
    {
        using var harness = new MigrationHarness();
        var entity = harness.Db.Model.FindEntityType(typeof(PlatformSyncRun));

        Assert.NotNull(entity);
        var filteredIndex = entity!.GetIndexes()
            .SingleOrDefault(ix => ix.GetDatabaseName() == "IX_PlatformSyncRuns_TenantId_InProgress");
        Assert.NotNull(filteredIndex);
        Assert.Equal("\"CompletedAt\" IS NULL", filteredIndex!.GetFilter());
    }

    // ---------- Helpers ----------

    /// <summary>
    /// Builds a service provider that mirrors the production wiring in
    /// <c>Program.cs</c> + <c>ApplicationServiceExtensions</c> for the platform sync services,
    /// using an in-memory DbContext and a stubbed file storage provider.
    /// </summary>
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Tenant provider (stubbed).
        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantA);
        services.AddSingleton(tenantProvider);

        // IHostEnvironment needed by PlatformUrlValidator.
        services.AddSingleton<IHostEnvironment>(new StubHostEnvironment());

        // DataProtection — ephemeral keyring is fine for tests.
        services.AddDataProtection();

        // DbContext — in-memory, scoped, mirrors Program.cs pattern.
        var dbName = Guid.NewGuid().ToString();
        services.AddSingleton(sp =>
        {
            var b = new DbContextOptionsBuilder<SelfHostedDbContext>();
            b.UseInMemoryDatabase(dbName);
            return b.Options;
        });
        services.AddScoped<SelfHostedDbContext>(sp =>
        {
            var options = sp.GetRequiredService<DbContextOptions<SelfHostedDbContext>>();
            return new SelfHostedDbContext(options, sp.GetRequiredService<ITenantProvider>());
        });

        // Named HttpClient — EXACT mirror of Program.cs:119-127.
        services.AddHttpClient("KnowzPlatformSync", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.MaxResponseContentBufferSize = 50 * 1024 * 1024;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Knowz-SelfHosted/1.0");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false
        });

        // File storage — stub, needed transitively by FileSyncService → VaultSyncOrchestrator.
        services.AddSingleton(Substitute.For<Knowz.SelfHosted.Infrastructure.Interfaces.IFileStorageProvider>());

        // Platform sync service registrations — EXACT mirror of ApplicationServiceExtensions.
        services.AddScoped<IVaultSyncOrchestrator, VaultSyncOrchestrator>();
        services.AddScoped<IPlatformSyncClient, PlatformSyncClient>();
        // One credential resolver + named-client factory for entity sync AND file sync
        // (NodeID FIX_SelfHostedFileSyncAuth).
        services.AddScoped<PlatformSyncCredentialResolver>();
        services.AddScoped<IPlatformAuditLog, PlatformAuditLogService>();
        services.AddScoped<IPlatformConnectionService, PlatformConnectionService>();
        services.AddSingleton<IUrlValidator, PlatformUrlValidator>();
        services.AddSingleton<IPlatformSyncRateLimiter, PlatformSyncRateLimiter>();
        services.AddScoped<VaultScopedExportService>();
        services.AddScoped<FileSyncService>();

        // Hosted service — mirrors Program.cs:88.
        services.AddHostedService<PlatformConnectionMigrationService>();

        return services.BuildServiceProvider();
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Knowz.SelfHosted.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>
    /// Test harness for <see cref="PlatformConnectionMigrationService"/> — owns an in-memory
    /// SelfHostedDbContext, a real ephemeral DataProtection provider, and a scope factory
    /// that always hands back the same scoped context so the service can resolve its deps.
    /// </summary>
    private sealed class MigrationHarness : IDisposable
    {
        public SelfHostedDbContext Db { get; }
        public IDataProtectionProvider Dpp { get; }
        public PlatformConnectionMigrationService Service { get; }

        private readonly ServiceProvider _root;

        public MigrationHarness()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            var tenantProvider = Substitute.For<ITenantProvider>();
            tenantProvider.TenantId.Returns(TenantA);
            services.AddSingleton(tenantProvider);

            services.AddDataProtection();

            var dbName = Guid.NewGuid().ToString();
            services.AddSingleton(sp =>
            {
                var b = new DbContextOptionsBuilder<SelfHostedDbContext>();
                b.UseInMemoryDatabase(dbName);
                return b.Options;
            });
            services.AddScoped<SelfHostedDbContext>(sp =>
            {
                var options = sp.GetRequiredService<DbContextOptions<SelfHostedDbContext>>();
                return new SelfHostedDbContext(options, sp.GetRequiredService<ITenantProvider>());
            });

            _root = services.BuildServiceProvider();

            // Long-lived scope for assertions — the migration service creates its own inner scopes
            // via IServiceScopeFactory, so it won't conflict with reads on this handle.
            var scope = _root.CreateScope();
            Db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            Dpp = _root.GetRequiredService<IDataProtectionProvider>();

            Service = new PlatformConnectionMigrationService(
                _root.GetRequiredService<IServiceScopeFactory>(),
                _root.GetRequiredService<ILogger<PlatformConnectionMigrationService>>());
        }

        public Vault SeedVault(Guid tenantId)
        {
            var vault = new Vault
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = $"Vault-{Guid.NewGuid():N}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            Db.Vaults.Add(vault);
            Db.SaveChanges();
            return vault;
        }

        public VaultSyncLink SeedLegacyLink(Guid localVaultId, string url, string plaintextKey)
        {
#pragma warning disable CS0618 // Intentional: exercising legacy columns under test
            var link = new VaultSyncLink
            {
                Id = Guid.NewGuid(),
                LocalVaultId = localVaultId,
                RemoteVaultId = Guid.NewGuid(),
                PlatformConnectionId = null,
                PlatformApiUrl = url,
                ApiKeyEncrypted = plaintextKey,
            };
#pragma warning restore CS0618
            Db.VaultSyncLinks.Add(link);
            Db.SaveChanges();
            return link;
        }

        public void Dispose()
        {
            Db.Database.EnsureDeleted();
            Db.Dispose();
            _root.Dispose();
        }
    }
}
