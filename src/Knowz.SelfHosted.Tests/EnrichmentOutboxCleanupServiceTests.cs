using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class EnrichmentOutboxCleanupServiceTests : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ServiceProvider _serviceProvider;
    private readonly EnrichmentOutboxCleanupService _svc;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public EnrichmentOutboxCleanupServiceTests()
    {
        var dbName = Guid.NewGuid().ToString();

        var services = new ServiceCollection();
        services.AddScoped<ITenantProvider>(sp =>
        {
            var tp = Substitute.For<ITenantProvider>();
            tp.TenantId.Returns(TenantId);
            return tp;
        });
        services.AddDbContext<SelfHostedDbContext>(opts =>
            opts.UseInMemoryDatabase(dbName));

        _serviceProvider = services.BuildServiceProvider();
        _scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var logger = Substitute.For<ILogger<EnrichmentOutboxCleanupService>>();
        _svc = new EnrichmentOutboxCleanupService(_scopeFactory, logger);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
    }

    [Fact]
    public async Task CleanupAsync_DeletesCompletedItemsOlderThan30Days()
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = Guid.NewGuid(),
                Status = EnrichmentStatus.Completed,
                ProcessedAt = DateTime.UtcNow.AddDays(-31)
            });
            await db.SaveChangesAsync();
        }

        var deleted = await _svc.CleanupAsync(CancellationToken.None);

        Assert.Equal(1, deleted);

        using var verifyScope = _scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        Assert.Empty(await verifyDb.EnrichmentOutbox.ToListAsync());
    }

    [Fact]
    public async Task CleanupAsync_DeletesFailedItemsOlderThan30Days()
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = Guid.NewGuid(),
                Status = EnrichmentStatus.Failed,
                ProcessedAt = DateTime.UtcNow.AddDays(-45)
            });
            await db.SaveChangesAsync();
        }

        var deleted = await _svc.CleanupAsync(CancellationToken.None);

        Assert.Equal(1, deleted);
    }

    [Fact]
    public async Task CleanupAsync_KeepsRecentCompletedItems()
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = Guid.NewGuid(),
                Status = EnrichmentStatus.Completed,
                ProcessedAt = DateTime.UtcNow.AddDays(-7) // recent, should be kept
            });
            await db.SaveChangesAsync();
        }

        var deleted = await _svc.CleanupAsync(CancellationToken.None);

        Assert.Equal(0, deleted);

        using var verifyScope = _scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        Assert.Single(await verifyDb.EnrichmentOutbox.ToListAsync());
    }

    [Fact]
    public async Task CleanupAsync_KeepsPendingAndProcessingItems()
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = Guid.NewGuid(),
                Status = EnrichmentStatus.Pending,
                CreatedAt = DateTime.UtcNow.AddDays(-60) // old but still pending
            });
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = Guid.NewGuid(),
                Status = EnrichmentStatus.Processing,
                CreatedAt = DateTime.UtcNow.AddDays(-60) // old but still processing
            });
            await db.SaveChangesAsync();
        }

        var deleted = await _svc.CleanupAsync(CancellationToken.None);

        Assert.Equal(0, deleted);

        using var verifyScope = _scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        Assert.Equal(2, await verifyDb.EnrichmentOutbox.CountAsync());
    }

    [Fact]
    public async Task CleanupAsync_KeepsTerminalItemsWithNullProcessedAt()
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = Guid.NewGuid(),
                Status = EnrichmentStatus.Completed,
                ProcessedAt = null // edge case: completed but no ProcessedAt
            });
            await db.SaveChangesAsync();
        }

        var deleted = await _svc.CleanupAsync(CancellationToken.None);

        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task CleanupAsync_DeletesMultipleOldItems()
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            for (int i = 0; i < 5; i++)
            {
                db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
                {
                    TenantId = TenantId,
                    KnowledgeId = Guid.NewGuid(),
                    Status = i % 2 == 0 ? EnrichmentStatus.Completed : EnrichmentStatus.Failed,
                    ProcessedAt = DateTime.UtcNow.AddDays(-40)
                });
            }
            // Add one recent item that should be kept
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = Guid.NewGuid(),
                Status = EnrichmentStatus.Completed,
                ProcessedAt = DateTime.UtcNow.AddDays(-5)
            });
            await db.SaveChangesAsync();
        }

        var deleted = await _svc.CleanupAsync(CancellationToken.None);

        Assert.Equal(5, deleted);

        using var verifyScope = _scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        Assert.Single(await verifyDb.EnrichmentOutbox.ToListAsync()); // Only recent one kept
    }
}
