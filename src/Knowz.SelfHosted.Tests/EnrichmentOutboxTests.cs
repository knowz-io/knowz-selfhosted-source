using System.Threading.Channels;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class EnrichmentOutboxTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly Channel<EnrichmentWorkItem> _channel;
    private readonly EnrichmentOutboxWriter _writer;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public EnrichmentOutboxTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);

        _channel = Channel.CreateBounded<EnrichmentWorkItem>(
            new BoundedChannelOptions(100) { SingleReader = true });

        var logger = Substitute.For<ILogger<EnrichmentOutboxWriter>>();
        _writer = new EnrichmentOutboxWriter(_db, _channel, logger);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // --- EnrichmentOutboxItem entity tests ---

    [Fact]
    public void EnrichmentOutboxItem_DefaultStatus_IsPending()
    {
        var item = new EnrichmentOutboxItem();
        Assert.Equal(EnrichmentStatus.Pending, item.Status);
    }

    [Fact]
    public void EnrichmentOutboxItem_DefaultMaxRetries_IsThree()
    {
        var item = new EnrichmentOutboxItem();
        Assert.Equal(3, item.MaxRetries);
    }

    [Fact]
    public void EnrichmentOutboxItem_DefaultId_IsNotEmpty()
    {
        var item = new EnrichmentOutboxItem();
        Assert.NotEqual(Guid.Empty, item.Id);
    }

    [Fact]
    public void EnrichmentStatus_HasFourValues()
    {
        var values = Enum.GetValues<EnrichmentStatus>();
        Assert.Equal(4, values.Length);
        Assert.Equal(0, (int)EnrichmentStatus.Pending);
        Assert.Equal(1, (int)EnrichmentStatus.Processing);
        Assert.Equal(2, (int)EnrichmentStatus.Completed);
        Assert.Equal(3, (int)EnrichmentStatus.Failed);
    }

    [Fact]
    public void EnrichmentWorkItem_RecordEquality()
    {
        var id = Guid.NewGuid();
        var a = new EnrichmentWorkItem(id, TenantId);
        var b = new EnrichmentWorkItem(id, TenantId);
        Assert.Equal(a, b);
    }

    // --- EnrichmentOutboxWriter tests ---

    [Fact]
    public async Task EnqueueAsync_CreatesOutboxItem()
    {
        var knowledgeId = Guid.NewGuid();

        await _writer.EnqueueAsync(knowledgeId, TenantId);

        var items = await _db.EnrichmentOutbox.ToListAsync();
        Assert.Single(items);
        Assert.Equal(knowledgeId, items[0].KnowledgeId);
        Assert.Equal(TenantId, items[0].TenantId);
        Assert.Equal(EnrichmentStatus.Pending, items[0].Status);
    }

    [Fact]
    public async Task EnqueueAsync_WritesToChannel()
    {
        var knowledgeId = Guid.NewGuid();

        await _writer.EnqueueAsync(knowledgeId, TenantId);

        Assert.True(_channel.Reader.TryRead(out var workItem));
        Assert.Equal(knowledgeId, workItem.KnowledgeId);
        Assert.Equal(TenantId, workItem.TenantId);
    }

    [Fact]
    public async Task EnqueueAsync_Deduplicates_WhenPendingExists()
    {
        var knowledgeId = Guid.NewGuid();

        // First enqueue
        await _writer.EnqueueAsync(knowledgeId, TenantId);
        // Drain channel
        _channel.Reader.TryRead(out _);

        // Second enqueue for same knowledge
        await _writer.EnqueueAsync(knowledgeId, TenantId);

        var items = await _db.EnrichmentOutbox.ToListAsync();
        Assert.Single(items); // Only 1 DB row
    }

    [Fact]
    public async Task EnqueueAsync_Deduplicates_WhenProcessingExists()
    {
        var knowledgeId = Guid.NewGuid();

        // Add an item already in Processing state
        _db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
        {
            KnowledgeId = knowledgeId,
            TenantId = TenantId,
            Status = EnrichmentStatus.Processing
        });
        await _db.SaveChangesAsync();

        await _writer.EnqueueAsync(knowledgeId, TenantId);

        var items = await _db.EnrichmentOutbox.ToListAsync();
        Assert.Single(items); // Still only 1 DB row
    }

    [Fact]
    public async Task EnqueueAsync_CreatesNewItem_WhenPreviousFailed()
    {
        var knowledgeId = Guid.NewGuid();

        // Add an item already in Failed state
        _db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
        {
            KnowledgeId = knowledgeId,
            TenantId = TenantId,
            Status = EnrichmentStatus.Failed
        });
        await _db.SaveChangesAsync();

        await _writer.EnqueueAsync(knowledgeId, TenantId);

        var items = await _db.EnrichmentOutbox.ToListAsync();
        Assert.Equal(2, items.Count); // Failed + new Pending
    }

    [Fact]
    public async Task EnqueueAsync_CreatesNewItem_WhenPreviousCompleted()
    {
        var knowledgeId = Guid.NewGuid();

        // Add a completed item
        _db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
        {
            KnowledgeId = knowledgeId,
            TenantId = TenantId,
            Status = EnrichmentStatus.Completed
        });
        await _db.SaveChangesAsync();

        await _writer.EnqueueAsync(knowledgeId, TenantId);

        var items = await _db.EnrichmentOutbox.ToListAsync();
        Assert.Equal(2, items.Count); // Completed + new Pending
    }

    [Fact]
    public async Task EnqueueAsync_StillWritesToChannel_WhenDeduplicated()
    {
        var knowledgeId = Guid.NewGuid();

        await _writer.EnqueueAsync(knowledgeId, TenantId);
        _channel.Reader.TryRead(out _); // drain

        await _writer.EnqueueAsync(knowledgeId, TenantId);

        // Second enqueue still wrote to channel
        Assert.True(_channel.Reader.TryRead(out var workItem));
        Assert.Equal(knowledgeId, workItem.KnowledgeId);
    }

    [Fact]
    public async Task EnqueueAsync_DifferentKnowledgeIds_BothCreated()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        await _writer.EnqueueAsync(id1, TenantId);
        await _writer.EnqueueAsync(id2, TenantId);

        var items = await _db.EnrichmentOutbox.ToListAsync();
        Assert.Equal(2, items.Count);
    }

    // --- DbContext configuration tests ---

    [Fact]
    public async Task DbContext_EnrichmentOutbox_PersistsAndReads()
    {
        var item = new EnrichmentOutboxItem
        {
            TenantId = TenantId,
            KnowledgeId = Guid.NewGuid(),
            ErrorMessage = "Test error"
        };
        _db.EnrichmentOutbox.Add(item);
        await _db.SaveChangesAsync();

        var loaded = await _db.EnrichmentOutbox.FindAsync(item.Id);
        Assert.NotNull(loaded);
        Assert.Equal("Test error", loaded.ErrorMessage);
    }

    [Fact]
    public async Task DbContext_EnrichmentOutbox_NoQueryFilter()
    {
        // Items from a different tenant should still be visible (no query filter)
        var otherTenantId = Guid.NewGuid();
        _db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
        {
            TenantId = otherTenantId,
            KnowledgeId = Guid.NewGuid()
        });
        await _db.SaveChangesAsync();

        var items = await _db.EnrichmentOutbox.ToListAsync();
        Assert.Single(items);
        Assert.Equal(otherTenantId, items[0].TenantId);
    }
}
