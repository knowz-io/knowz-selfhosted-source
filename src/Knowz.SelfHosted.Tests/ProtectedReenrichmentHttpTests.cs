using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.API.Endpoints;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Npgsql;

namespace Knowz.SelfHosted.Tests;

public sealed class ProtectedReenrichmentHttpTests : IAsyncDisposable
{
    private readonly Guid tenantId = Guid.NewGuid(), actorId = Guid.NewGuid();
    private readonly ITenantProvider tenant = Substitute.For<ITenantProvider>();
    private readonly Channel<EnrichmentWorkItem> queue = Channel.CreateUnbounded<EnrichmentWorkItem>();
    private readonly SelfHostedDbContext db;
    private WebApplication? app;
    private sealed class Actor(Guid id) : IPortableActorProvider { public Guid? ActorId => id; }

    public ProtectedReenrichmentHttpTests() : this(null) { }

    private ProtectedReenrichmentHttpTests(DbContextOptions<SelfHostedDbContext>? options)
    {
        tenant.TenantId.Returns(tenantId);
        db = new(options ?? new DbContextOptionsBuilder<SelfHostedDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant, new Actor(actorId));
        if (db.Database.IsRelational()) db.Database.Migrate();
        db.Users.Add(new() { Id = actorId, TenantId = tenantId, Username = "reenrichment-fixture", IsActive = true });
        db.Tenants.Add(new() { Id = tenantId, Name = "reenrichment-fixture", Slug = "reenrichment-fixture", IsActive = true });
        db.SaveChanges();
    }

    private async Task<Guid> SeedAsync(string state, Guid? scope = null)
    {
        using var importing = db.BeginInactiveRecoveryImport();
        var id = Guid.NewGuid(); var ownerTenant = scope ?? tenantId;
        db.KnowledgeItems.Add(new() { Id = id, TenantId = ownerTenant, CreatedByUserId = actorId, Title = "Synthetic item", Content = "Synthetic content", IsIndexed = false,
            PortableRecoveryState = state is "source-provider-required" or "suspended-release" ? state : null });
        if (state == "sealed") db.ShSealedKnowledge.Add(new() { Id = Guid.NewGuid(), TenantId = ownerTenant, KnowledgeId = id, OriginalItemId = id, Ciphertext = "synthetic sealed body" });
        await db.SaveChangesAsync(); return id;
    }

    private async Task<HttpClient> ClientAsync()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(db); builder.Services.AddSingleton(tenant);
        builder.Services.AddSingleton<IEnrichmentOutboxWriter>(new EnrichmentOutboxWriter(db, queue, NullLogger<EnrichmentOutboxWriter>.Instance));
        builder.Services.AddSingleton<KnowledgeService>(_ => throw new InvalidOperationException("Unused service"));
        builder.Services.AddSingleton<IVaultAccessService>(new VaultAccessService(db));
        builder.Services.AddSingleton<FileStorageService>(_ => throw new InvalidOperationException("Unused service"));
        builder.Services.AddSingleton<VaultService>(_ => throw new InvalidOperationException("Unused service"));
        builder.Services.AddSingleton(Substitute.For<IContentAmendmentService>());
        app = builder.Build();
        app.Use(async (http, next) => { http.User = new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, actorId.ToString()), new("tenantId", tenantId.ToString()), new(ClaimTypes.Role, "Admin")], "fixture")); await next(); });
        app.MapKnowledgeEndpoints(); await app.StartAsync(); return app.GetTestClient();
    }

    [Theory]
    [InlineData("source-provider-required")]
    [InlineData("suspended-release")]
    [InlineData("sealed")]
    public async Task ManualReenrichmentReportsLockedForProtectedOwner(string state)
    {
        var id = await SeedAsync(state); using var client = await ClientAsync();
        using var response = await client.PostAsync($"/api/v1/knowledge/{id}/re-enrich", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.NotEmpty((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!);
        Assert.Empty(await db.EnrichmentOutbox.ToListAsync()); Assert.False(queue.Reader.TryRead(out _));
    }

    [Fact]
    public async Task OrdinaryOwnerStillQueuesRealOutboxWork()
    {
        var id = await SeedAsync("ordinary"); using var client = await ClientAsync();
        using var response = await client.PostAsync($"/api/v1/knowledge/{id}/re-enrich", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(id, (await db.EnrichmentOutbox.SingleAsync()).KnowledgeId);
        Assert.True(queue.Reader.TryRead(out var queued)); Assert.Equal(id, queued.KnowledgeId);
    }

    [Fact]
    public async Task ForeignOwnerCannotBeQueued()
    {
        var id = await SeedAsync("ordinary", Guid.NewGuid()); using var client = await ClientAsync();
        using var response = await client.PostAsync($"/api/v1/knowledge/{id}/re-enrich", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await db.EnrichmentOutbox.ToListAsync()); Assert.False(queue.Reader.TryRead(out _));
    }

    [Fact]
    public async Task BulkCountsOnlyEligibleLocalOwnersAndQueuesExactlyThose()
    {
        var plain = await SeedAsync("ordinary");
        await SeedAsync("source-provider-required"); await SeedAsync("suspended-release"); await SeedAsync("sealed"); await SeedAsync("ordinary", Guid.NewGuid());
        using var client = await ClientAsync(); using var response = await client.PostAsync("/api/v1/knowledge/re-enrich-all", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, result.GetProperty("totalFound").GetInt32()); Assert.Equal(1, result.GetProperty("enqueued").GetInt32());
        Assert.Equal(plain, (await db.EnrichmentOutbox.SingleAsync()).KnowledgeId);
        Assert.True(queue.Reader.TryRead(out var queued)); Assert.Equal(plain, queued.KnowledgeId); Assert.False(queue.Reader.TryRead(out _));
    }

    [PostgresTheory]
    [InlineData("source-provider-required")]
    [InlineData("suspended-release")]
    public async Task NativeHttpPolicyRejectsInactiveOwnerAndCountsActualOrdinaryWork(string state)
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("KNOWZ_TEST_POSTGRES"));
        Assert.Contains(connection.Host, new[] { "127.0.0.1", "localhost" }); Assert.Equal(18460, connection.Port);
        var database = "kc_reenrich_" + Guid.NewGuid().ToString("N");
        connection.Database = "postgres"; connection.Pooling = false;
        await using var admin = new NpgsqlConnection(connection.ConnectionString); await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            connection.Database = database;
            await using var fixture = new ProtectedReenrichmentHttpTests(new DbContextOptionsBuilder<SelfHostedDbContext>().UseNpgsql(connection.ConnectionString, pg => pg.EnableRetryOnFailure()).Options);
            var protectedId = await fixture.SeedAsync(state); var plainId = await fixture.SeedAsync("ordinary");
            using var client = await fixture.ClientAsync();
            using var blocked = await client.PostAsync($"/api/v1/knowledge/{protectedId}/re-enrich", null);
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode); Assert.Empty(await fixture.db.EnrichmentOutbox.ToListAsync());
            using var bulk = await client.PostAsync("/api/v1/knowledge/re-enrich-all", null); bulk.EnsureSuccessStatusCode();
            var result = await bulk.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, result.GetProperty("enqueued").GetInt32()); Assert.Equal(1, result.GetProperty("totalFound").GetInt32());
            Assert.Equal(plainId, (await fixture.db.EnrichmentOutbox.SingleAsync()).KnowledgeId);
            Assert.True(fixture.queue.Reader.TryRead(out var work)); Assert.Equal(plainId, work.KnowledgeId); Assert.False(fixture.queue.Reader.TryRead(out _));
        }
        finally { await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin); await drop.ExecuteNonQueryAsync(); }
    }

    public async ValueTask DisposeAsync() { if (app != null) await app.DisposeAsync(); await db.DisposeAsync(); }
}
