using System.Threading.Channels;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class KnowledgeServiceEnrichmentTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly ISearchService _searchService;
    private readonly IOpenAIService _openAIService;
    private readonly IEnrichmentOutboxWriter _enrichmentWriter;
    private readonly KnowledgeService _svc;
    private readonly Channel<EnrichmentWorkItem> _channel;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public KnowledgeServiceEnrichmentTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);

        _searchService = Substitute.For<ISearchService>();
        _openAIService = Substitute.For<IOpenAIService>();
        _openAIService.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f, 0.2f, 0.3f });

        _channel = Channel.CreateBounded<EnrichmentWorkItem>(
            new BoundedChannelOptions(100) { SingleReader = true });
        _enrichmentWriter = new EnrichmentOutboxWriter(
            _db, _channel, Substitute.For<ILogger<EnrichmentOutboxWriter>>());

        var logger = Substitute.For<ILogger<KnowledgeService>>();
        var knowledgeRepo = new SelfHostedRepository<Knowledge>(_db);
        var tagRepo = new SelfHostedRepository<Tag>(_db);

        var chunkingService = new SelfHostedChunkingService();
        _svc = new KnowledgeService(
            knowledgeRepo, tagRepo, _db, _searchService, _openAIService,
            chunkingService, tenantProvider, logger, _enrichmentWriter);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public async Task CreateKnowledgeAsync_EnqueuesEnrichment()
    {
        var result = await _svc.CreateKnowledgeAsync(
            "Content for enrichment", "Title", "Note", null,
            new List<string>(), null, CancellationToken.None);

        Assert.NotNull(result);

        // Verify outbox item was created
        var outboxItems = await _db.EnrichmentOutbox.ToListAsync();
        Assert.Single(outboxItems);
        Assert.Equal(result.Id, outboxItems[0].KnowledgeId);
        Assert.Equal(TenantId, outboxItems[0].TenantId);

        // Verify channel item
        Assert.True(_channel.Reader.TryRead(out var workItem));
        Assert.Equal(result.Id, workItem.KnowledgeId);
    }

    [Fact]
    public async Task UpdateKnowledgeAsync_EnqueuesEnrichment_WhenContentChanged()
    {
        // Create a knowledge item first
        var item = new Knowledge
        {
            TenantId = TenantId,
            Title = "Original",
            Content = "Original content"
        };
        _db.KnowledgeItems.Add(item);
        await _db.SaveChangesAsync();

        _channel.Reader.TryRead(out _); // Drain any prior items

        var result = await _svc.UpdateKnowledgeAsync(
            item.Id, null, "Updated content", null, null, null, CancellationToken.None);

        Assert.NotNull(result);

        // Verify outbox item
        var outboxItems = await _db.EnrichmentOutbox.ToListAsync();
        Assert.Single(outboxItems);
        Assert.Equal(item.Id, outboxItems[0].KnowledgeId);
    }

    [Fact]
    public async Task UpdateKnowledgeAsync_EnqueuesEnrichment_WhenTitleChanged()
    {
        var item = new Knowledge
        {
            TenantId = TenantId,
            Title = "Original",
            Content = "Content"
        };
        _db.KnowledgeItems.Add(item);
        await _db.SaveChangesAsync();

        var result = await _svc.UpdateKnowledgeAsync(
            item.Id, "New Title", null, null, null, null, CancellationToken.None);

        Assert.NotNull(result);

        var outboxItems = await _db.EnrichmentOutbox.ToListAsync();
        Assert.Single(outboxItems);
    }

    [Fact]
    public async Task UpdateKnowledgeAsync_DoesNotEnqueue_WhenOnlySourceChanged()
    {
        var item = new Knowledge
        {
            TenantId = TenantId,
            Title = "Title",
            Content = "Content"
        };
        _db.KnowledgeItems.Add(item);
        await _db.SaveChangesAsync();

        var result = await _svc.UpdateKnowledgeAsync(
            item.Id, null, null, "new-source", null, null, CancellationToken.None);

        Assert.NotNull(result);

        var outboxItems = await _db.EnrichmentOutbox.ToListAsync();
        Assert.Empty(outboxItems); // No enrichment needed
    }

    [Fact]
    public async Task CreateKnowledgeAsync_ContinuesOnEnrichmentFailure()
    {
        // Use a mock writer that throws
        var failingWriter = Substitute.For<IEnrichmentOutboxWriter>();
        failingWriter.EnqueueAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new Exception("Outbox failed")));

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        var logger = Substitute.For<ILogger<KnowledgeService>>();
        var knowledgeRepo = new SelfHostedRepository<Knowledge>(_db);
        var tagRepo = new SelfHostedRepository<Tag>(_db);

        var chunkingService2 = new SelfHostedChunkingService();
        var svc = new KnowledgeService(
            knowledgeRepo, tagRepo, _db, _searchService, _openAIService,
            chunkingService2, tenantProvider, logger, failingWriter);

        // Should not throw — enrichment failure is non-critical
        var result = await svc.CreateKnowledgeAsync(
            "Content", "Title", "Note", null,
            new List<string>(), null, CancellationToken.None);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task KnowledgeService_WorksWithoutEnrichmentWriter()
    {
        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        var logger = Substitute.For<ILogger<KnowledgeService>>();
        var knowledgeRepo = new SelfHostedRepository<Knowledge>(_db);
        var tagRepo = new SelfHostedRepository<Tag>(_db);

        // No enrichment writer (null)
        var chunkingService3 = new SelfHostedChunkingService();
        var svc = new KnowledgeService(
            knowledgeRepo, tagRepo, _db, _searchService, _openAIService,
            chunkingService3, tenantProvider, logger);

        var result = await svc.CreateKnowledgeAsync(
            "Content", "Title", "Note", null,
            new List<string>(), null, CancellationToken.None);

        Assert.NotNull(result);

        // No outbox items (enrichment not configured)
        var outboxItems = await _db.EnrichmentOutbox.ToListAsync();
        Assert.Empty(outboxItems);
    }
}
