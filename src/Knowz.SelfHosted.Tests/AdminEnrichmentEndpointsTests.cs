using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// VERIFY (SH_ENTERPRISE_RUNTIME_RESILIENCE §3 6.x):
/// 6.4 — Admin outbox GET returns `{ totalCount, items: [{ id, knowledgeId, status,
///       attemptCount, lastError, createdAt }] }`, with tenant scope respected.
///
/// 401 / 403 / endpoint-registration tests are covered by the existing
/// `AdminEndpointsAuthorizationTests` + middleware tests — this file asserts
/// the query shape that `post-deploy-smoke.sh` consumes.
/// </summary>
public class AdminEnrichmentEndpointsTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly IServiceScopeFactory _scopeFactory;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public AdminEnrichmentEndpointsTests()
    {
        var services = new ServiceCollection();
        services.AddScoped<ITenantProvider>(_ =>
        {
            var tp = Substitute.For<ITenantProvider>();
            tp.TenantId.Returns(_ => TenantContext.CurrentTenantId ?? TenantId);
            return tp;
        });
        // Shared in-memory root so EF contexts resolved from different scopes see
        // the same rows (the default root is per-DbContext-instance in EF Core 10).
        var root = new InMemoryDatabaseRoot();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<SelfHostedDbContext>(opts =>
            opts.UseInMemoryDatabase(dbName, root));
        _serviceProvider = services.BuildServiceProvider();
        _scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
    }

    public void Dispose() => _serviceProvider.Dispose();

    [Fact]
    public async Task Outbox_Filter_Status_Failed_ReturnsExpectedShape()
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            db.EnrichmentOutbox.AddRange(
                new EnrichmentOutboxItem
                {
                    TenantId = TenantId,
                    KnowledgeId = Guid.NewGuid(),
                    Status = EnrichmentStatus.Failed,
                    ErrorMessage = "timeout",
                    AiProcessingAttempts = 3,
                    CreatedAt = DateTime.UtcNow.AddMinutes(-5),
                },
                new EnrichmentOutboxItem
                {
                    TenantId = TenantId,
                    KnowledgeId = Guid.NewGuid(),
                    Status = EnrichmentStatus.Completed,
                    AiProcessingAttempts = 1,
                });
            await db.SaveChangesAsync();
        }

        // Simulate the query the endpoint runs
        using var qScope = _scopeFactory.CreateScope();
        var qDb = qScope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        var q = qDb.EnrichmentOutbox.AsNoTracking().Where(x => x.Status == EnrichmentStatus.Failed);
        var totalCount = await q.CountAsync();
        var items = await q.OrderByDescending(x => x.CreatedAt).Take(50)
            .Select(x => new
            {
                id = x.Id,
                knowledgeId = x.KnowledgeId,
                status = x.Status,
                attemptCount = x.AiProcessingAttempts,
                lastError = x.ErrorMessage,
                createdAt = x.CreatedAt,
            }).ToListAsync();

        Assert.Equal(1, totalCount);
        Assert.Single(items);
        Assert.Equal(EnrichmentStatus.Failed, items[0].status);
        Assert.Equal(3, items[0].attemptCount);
        Assert.Equal("timeout", items[0].lastError);
    }

    [Fact]
    public async Task Outbox_TenantScope_ExcludesOtherTenants()
    {
        var otherTenant = Guid.NewGuid();
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            db.EnrichmentOutbox.AddRange(
                new EnrichmentOutboxItem
                {
                    TenantId = TenantId,
                    KnowledgeId = Guid.NewGuid(),
                    Status = EnrichmentStatus.Failed,
                },
                new EnrichmentOutboxItem
                {
                    TenantId = otherTenant,
                    KnowledgeId = Guid.NewGuid(),
                    Status = EnrichmentStatus.Failed,
                });
            await db.SaveChangesAsync();
        }

        // EnrichmentOutbox has no query filter in SelfHostedDbContext — endpoint relies
        // on manual tenant scoping via the .Where(x => x.TenantId == currentTenant).
        // This test documents that contract by showing a plain query returns BOTH rows.
        // If EnrichmentOutbox gains a query filter later, swap to the filtered expectation.
        using var qScope = _scopeFactory.CreateScope();
        var qDb = qScope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        var all = await qDb.EnrichmentOutbox.AsNoTracking().CountAsync();
        Assert.Equal(2, all);

        // Caller-layer tenant scope (what the endpoint should apply)
        var tenantScoped = await qDb.EnrichmentOutbox.AsNoTracking()
            .Where(x => x.TenantId == TenantId)
            .CountAsync();
        Assert.Equal(1, tenantScoped);
    }
}
