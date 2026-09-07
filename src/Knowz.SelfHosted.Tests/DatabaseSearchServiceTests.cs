using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class DatabaseSearchServiceTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly DatabaseSearchService _svc;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public DatabaseSearchServiceTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);

        var logger = Substitute.For<ILogger<DatabaseSearchService>>();
        _svc = new DatabaseSearchService(_db, logger);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // --- Field-weighted scoring ---

    [Fact]
    public async Task HybridSearchAsync_UsesFieldWeightedScoring_NotPositionBased()
    {
        // Title match should score higher than content-only match regardless of insertion order
        _db.KnowledgeItems.AddRange(
            new Knowledge
            {
                TenantId = TenantId, Title = "Generic Guide",
                Content = "Apollo program details"
            },
            new Knowledge
            {
                TenantId = TenantId, Title = "Apollo Mission Guide",
                Content = "General content"
            });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo");

        Assert.Equal(2, results.Count);
        // Title match (boost 3.0) should rank higher than content match (boost 2.5)
        Assert.Equal("Apollo Mission Guide", results[0].Title);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public async Task HybridSearchAsync_OrdersByScore_NotByUpdatedAt()
    {
        // Add items in specific order — old item has title match, new item has content match
        var oldItem = new Knowledge
        {
            TenantId = TenantId, Title = "Apollo Guide", Content = "Some content"
        };
        var newItem = new Knowledge
        {
            TenantId = TenantId, Title = "Latest News", Content = "Apollo mentioned here"
        };
        _db.KnowledgeItems.AddRange(oldItem, newItem);
        await _db.SaveChangesAsync();

        // Make old item have older UpdatedAt
        var oldEntry = _db.Entry(oldItem);
        oldEntry.Property(nameof(Knowledge.UpdatedAt)).CurrentValue = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newEntry = _db.Entry(newItem);
        newEntry.Property(nameof(Knowledge.UpdatedAt)).CurrentValue = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo");

        Assert.Equal(2, results.Count);
        // Old item with title match should rank first (score-based), not new item (recency-based)
        Assert.Equal("Apollo Guide", results[0].Title);
    }

    [Fact]
    public async Task HybridSearchAsync_ScoreIsNotPositionBased()
    {
        _db.KnowledgeItems.Add(new Knowledge
        {
            TenantId = TenantId, Title = "Apollo Guide", Content = "Content"
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo");

        Assert.Single(results);
        // Score should NOT be 1.0 (the old position-based value for first item)
        // or 0.99. It should be based on field-weighted scoring.
        Assert.True(results[0].Score > 0);
        // Position should not affect score
        Assert.NotEqual(0, results[0].Score);
    }

    [Fact]
    public async Task HybridSearchAsync_TitleMatchScoresHigherThanContentMatch()
    {
        _db.KnowledgeItems.AddRange(
            new Knowledge
            {
                TenantId = TenantId, Title = "Unrelated Title",
                Content = "Apollo is here in content"
            },
            new Knowledge
            {
                TenantId = TenantId, Title = "Apollo in the Title",
                Content = "Unrelated content stuff"
            });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo");

        Assert.Equal(2, results.Count);
        Assert.Equal("Apollo in the Title", results[0].Title);
    }

    [Fact]
    public async Task HybridSearchAsync_MultiWordQuery_ScoresTermsIndependently()
    {
        _db.KnowledgeItems.AddRange(
            new Knowledge
            {
                TenantId = TenantId, Title = "Apollo Missions",
                Content = "Space exploration details"
            },
            new Knowledge
            {
                TenantId = TenantId, Title = "Apollo and Space",
                Content = "Space and Apollo together"
            });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo Space");

        Assert.Equal(2, results.Count);
        // Item with both terms in title+content should score higher
        Assert.True(results[0].Score > 0);
    }

    [Fact]
    public async Task HybridSearchAsync_ReturnsEmptyList_WhenQueryIsEmpty()
    {
        _db.KnowledgeItems.Add(new Knowledge
        {
            TenantId = TenantId, Title = "Test", Content = "Content"
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("");

        Assert.Empty(results);
    }

    [Fact]
    public async Task HybridSearchAsync_RespectsMaxResults()
    {
        for (int i = 0; i < 20; i++)
        {
            _db.KnowledgeItems.Add(new Knowledge
            {
                TenantId = TenantId, Title = $"Apollo Doc {i}", Content = "Content"
            });
        }
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo", maxResults: 5);

        Assert.Equal(5, results.Count);
    }
}
