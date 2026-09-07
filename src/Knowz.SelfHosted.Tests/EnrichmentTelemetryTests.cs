using System.Threading.Channels;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// VERIFY (SH_ENTERPRISE_RUNTIME_RESILIENCE §3 4.x):
/// 4.1 — AiProcessingAttempts increments on each ProcessWorkItemAsync call.
/// 4.2 — EnrichmentActivityLog row written per attempt, with FinishedAt + Status.
/// 4.3 — Tenant scope applies to activity-log queries (query filter present).
/// </summary>
public class EnrichmentTelemetryTests : IDisposable
{
    private readonly Channel<EnrichmentWorkItem> _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ServiceProvider _serviceProvider;
    private readonly ITextEnrichmentService _enrichmentService;
    private readonly ISearchService _searchService;
    private readonly IOpenAIService _openAIService;
    private readonly EnrichmentBackgroundService _svc;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public EnrichmentTelemetryTests()
    {
        _channel = Channel.CreateBounded<EnrichmentWorkItem>(
            new BoundedChannelOptions(100) { SingleReader = true });

        var dbName = Guid.NewGuid().ToString();

        _enrichmentService = Substitute.For<ITextEnrichmentService>();
        _searchService = Substitute.For<ISearchService>();
        _openAIService = Substitute.For<IOpenAIService>();
        _openAIService.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f, 0.2f });

        var services = new ServiceCollection();
        services.AddScoped<ITenantProvider>(_ =>
        {
            var tp = Substitute.For<ITenantProvider>();
            tp.TenantId.Returns(_ => TenantContext.CurrentTenantId ?? TenantId);
            return tp;
        });
        services.AddDbContext<SelfHostedDbContext>(opts => opts.UseInMemoryDatabase(dbName));
        services.AddScoped<ITextEnrichmentService>(_ => _enrichmentService);
        services.AddScoped<ISearchService>(_ => _searchService);
        services.AddScoped<IOpenAIService>(_ => _openAIService);
        services.AddScoped<ISelfHostedChunkingService, SelfHostedChunkingService>();

        _serviceProvider = services.BuildServiceProvider();
        _scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        _svc = new EnrichmentBackgroundService(
            _channel, _scopeFactory, Substitute.For<ILogger<EnrichmentBackgroundService>>());
    }

    public void Dispose() => _serviceProvider.Dispose();

    private async Task SeedAsync(Guid knowledgeId, EnrichmentStatus outboxStatus, int attempts = 0)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        TenantContext.CurrentTenantId = TenantId;
        try
        {
            db.KnowledgeItems.Add(new Knowledge
            {
                Id = knowledgeId,
                TenantId = TenantId,
                Title = "Real Title",
                Content = "Some content"
            });
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = knowledgeId,
                Status = outboxStatus,
                AiProcessingAttempts = attempts,
            });
            await db.SaveChangesAsync();
        }
        finally
        {
            TenantContext.CurrentTenantId = null;
        }
    }

    [Fact]
    public async Task AttemptsIncrementOnEachProcess()
    {
        var knowledgeId = Guid.Parse("11111111-aaaa-bbbb-cccc-111111111111");
        await SeedAsync(knowledgeId, EnrichmentStatus.Pending);

        _enrichmentService.SummarizeAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns("A summary");
        _enrichmentService.ExtractTagsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(new List<string>());

        await _svc.ProcessWorkItemAsync(new EnrichmentWorkItem(knowledgeId, TenantId), CancellationToken.None);

        // Reset outbox back to Pending and reprocess
        using (var resetScope = _scopeFactory.CreateScope())
        {
            var db = resetScope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            var item = await db.EnrichmentOutbox.FirstAsync();
            item.Status = EnrichmentStatus.Pending;
            await db.SaveChangesAsync();
        }

        await _svc.ProcessWorkItemAsync(new EnrichmentWorkItem(knowledgeId, TenantId), CancellationToken.None);

        using var verifyScope = _scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        var outbox = await verifyDb.EnrichmentOutbox.FirstAsync();
        Assert.Equal(2, outbox.AiProcessingAttempts);
    }

    [Fact]
    public async Task ActivityLogWrittenPerAttempt_OnSuccess()
    {
        var knowledgeId = Guid.Parse("22222222-aaaa-bbbb-cccc-222222222222");
        await SeedAsync(knowledgeId, EnrichmentStatus.Pending);

        _enrichmentService.SummarizeAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns("A summary");
        _enrichmentService.ExtractTagsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(new List<string>());

        await _svc.ProcessWorkItemAsync(new EnrichmentWorkItem(knowledgeId, TenantId), CancellationToken.None);

        using var verifyScope = _scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        TenantContext.CurrentTenantId = TenantId;
        var log = await verifyDb.EnrichmentActivityLogs.FirstAsync();
        TenantContext.CurrentTenantId = null;

        Assert.Equal(knowledgeId, log.KnowledgeId);
        Assert.Equal(EnrichmentStatus.Completed, log.Status);
        Assert.Equal(1, log.AttemptNumber);
        Assert.NotNull(log.FinishedAt);
        Assert.Null(log.ErrorMessage);
    }

    [Fact]
    public async Task ActivityLogWrittenPerAttempt_OnFailure()
    {
        var knowledgeId = Guid.Parse("33333333-aaaa-bbbb-cccc-333333333333");
        await SeedAsync(knowledgeId, EnrichmentStatus.Pending);

        _enrichmentService.SummarizeAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns<string?>(_ => throw new Exception("AI blew up"));
        _enrichmentService.ExtractTagsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(new List<string>());

        await _svc.ProcessWorkItemAsync(new EnrichmentWorkItem(knowledgeId, TenantId), CancellationToken.None);

        using var verifyScope = _scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        TenantContext.CurrentTenantId = TenantId;
        var log = await verifyDb.EnrichmentActivityLogs.FirstAsync();
        TenantContext.CurrentTenantId = null;

        Assert.Equal(EnrichmentStatus.Failed, log.Status);
        Assert.NotNull(log.FinishedAt);
        Assert.Contains("AI blew up", log.ErrorMessage);
    }

    [Fact]
    public async Task ActivityLog_TenantScoped_ExcludesOtherTenants()
    {
        var knowledgeId = Guid.Parse("44444444-aaaa-bbbb-cccc-444444444444");
        var otherTenant = Guid.NewGuid();

        // Seed activity log for THIS tenant
        await SeedAsync(knowledgeId, EnrichmentStatus.Pending);
        _enrichmentService.SummarizeAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns("A summary");
        _enrichmentService.ExtractTagsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(new List<string>());
        await _svc.ProcessWorkItemAsync(new EnrichmentWorkItem(knowledgeId, TenantId), CancellationToken.None);

        // Manually inject a log row for a DIFFERENT tenant
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            db.EnrichmentActivityLogs.Add(new EnrichmentActivityLog
            {
                TenantId = otherTenant,
                KnowledgeId = Guid.NewGuid(),
                AttemptNumber = 1,
                StartedAt = DateTime.UtcNow,
                Status = EnrichmentStatus.Completed,
            });
            await db.SaveChangesAsync();
        }

        // Query with THIS tenant context — should only see 1 row
        using var verifyScope = _scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        TenantContext.CurrentTenantId = TenantId;
        var logs = await verifyDb.EnrichmentActivityLogs.ToListAsync();
        TenantContext.CurrentTenantId = null;

        Assert.Single(logs);
        Assert.Equal(TenantId, logs[0].TenantId);
    }
}
