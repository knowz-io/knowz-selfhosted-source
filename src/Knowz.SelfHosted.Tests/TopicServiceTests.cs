using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class TopicServiceTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly TopicService _svc;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public TopicServiceTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);

        var topicRepo = new SelfHostedRepository<Topic>(_db);
        var logger = Substitute.For<ILogger<TopicService>>();

        _svc = new TopicService(topicRepo, _db, logger);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public async Task ListTopicsAsync_ReturnsTopicListResponse()
    {
        _db.Topics.AddRange(
            new Topic { TenantId = TenantId, Name = "Alpha" },
            new Topic { TenantId = TenantId, Name = "Beta" });
        await _db.SaveChangesAsync();

        var result = await _svc.ListTopicsAsync(100, CancellationToken.None);

        Assert.IsType<TopicListResponse>(result);
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.Topics.Count);
    }

    [Fact]
    public async Task GetTopicDetailsAsync_ReturnsDetails_WhenExists()
    {
        var topic = new Topic { TenantId = TenantId, Name = "Science", Description = "Science topics" };
        _db.Topics.Add(topic);

        var item = new Knowledge { TenantId = TenantId, Title = "Physics", Content = "Content", TopicId = topic.Id };
        _db.KnowledgeItems.Add(item);
        await _db.SaveChangesAsync();

        var result = await _svc.GetTopicDetailsAsync(topic.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.IsType<TopicDetailResponse>(result);
        Assert.Equal("Science", result.Name);
        Assert.Single(result.KnowledgeItems);
    }

    [Fact]
    public async Task GetTopicDetailsAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _svc.GetTopicDetailsAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ListTopicsAsync_RespectsLimit()
    {
        _db.Topics.AddRange(
            new Topic { TenantId = TenantId, Name = "A" },
            new Topic { TenantId = TenantId, Name = "B" },
            new Topic { TenantId = TenantId, Name = "C" },
            new Topic { TenantId = TenantId, Name = "D" },
            new Topic { TenantId = TenantId, Name = "E" });
        await _db.SaveChangesAsync();

        var result = await _svc.ListTopicsAsync(3, CancellationToken.None);

        Assert.Equal(3, result.Topics.Count);
        Assert.Equal(3, result.TotalCount);
    }
}
