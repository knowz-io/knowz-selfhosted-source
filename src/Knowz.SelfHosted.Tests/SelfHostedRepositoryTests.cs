using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.Specifications;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class SelfHostedRepositoryTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly ISelfHostedRepository<Knowledge> _knowledgeRepo;
    private readonly ISelfHostedRepository<Tag> _tagRepo;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public SelfHostedRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = NSubstitute.Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);

        _knowledgeRepo = new SelfHostedRepository<Knowledge>(_db);
        _tagRepo = new SelfHostedRepository<Tag>(_db);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsEntity_WhenExists()
    {
        var item = new Knowledge { TenantId = TenantId, Title = "Test", Content = "Content" };
        _db.KnowledgeItems.Add(item);
        await _db.SaveChangesAsync();

        var result = await _knowledgeRepo.GetByIdAsync(item.Id);

        Assert.NotNull(result);
        Assert.Equal("Test", result.Title);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _knowledgeRepo.GetByIdAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task AddAsync_SetsCreatedAtAndUpdatedAt()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);

        var item = new Knowledge { TenantId = TenantId, Title = "New", Content = "Content" };
        await _knowledgeRepo.AddAsync(item);
        await _knowledgeRepo.SaveChangesAsync();

        var after = DateTime.UtcNow.AddSeconds(1);

        var saved = await _db.KnowledgeItems.FindAsync(item.Id);
        Assert.NotNull(saved);
        Assert.InRange(saved.CreatedAt, before, after);
        Assert.InRange(saved.UpdatedAt, before, after);
    }

    [Fact]
    public async Task UpdateAsync_SetsUpdatedAt()
    {
        var item = new Knowledge { TenantId = TenantId, Title = "Original", Content = "Content" };
        _db.KnowledgeItems.Add(item);
        await _db.SaveChangesAsync();

        var originalUpdatedAt = item.UpdatedAt;

        // Small delay to ensure timestamp differs
        await Task.Delay(10);

        item.Title = "Modified";
        await _knowledgeRepo.UpdateAsync(item);
        await _knowledgeRepo.SaveChangesAsync();

        var saved = await _db.KnowledgeItems.FindAsync(item.Id);
        Assert.NotNull(saved);
        Assert.Equal("Modified", saved.Title);
        Assert.True(saved.UpdatedAt >= originalUpdatedAt);
    }

    [Fact]
    public async Task SoftDeleteAsync_SetsIsDeletedAndUpdatedAt()
    {
        var item = new Knowledge { TenantId = TenantId, Title = "To Delete", Content = "Content" };
        _db.KnowledgeItems.Add(item);
        await _db.SaveChangesAsync();

        Assert.False(item.IsDeleted);

        await _knowledgeRepo.SoftDeleteAsync(item);
        await _knowledgeRepo.SaveChangesAsync();

        var deleted = await _db.KnowledgeItems
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(k => k.Id == item.Id);
        Assert.NotNull(deleted);
        Assert.True(deleted.IsDeleted);
    }

    [Fact]
    public async Task ListAsync_AppliesSpecification()
    {
        _db.Tags.AddRange(
            new Tag { TenantId = TenantId, Name = "alpha" },
            new Tag { TenantId = TenantId, Name = "beta" },
            new Tag { TenantId = TenantId, Name = "gamma" });
        await _db.SaveChangesAsync();

        var spec = new TagsByNamesSpec(new[] { "alpha", "gamma" });
        var result = await _tagRepo.ListAsync(spec);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, t => t.Name == "alpha");
        Assert.Contains(result, t => t.Name == "gamma");
        Assert.DoesNotContain(result, t => t.Name == "beta");
    }

    [Fact]
    public async Task FirstOrDefaultAsync_ReturnsFirst_Matching()
    {
        var item = new Knowledge { TenantId = TenantId, Title = "Target", Content = "Content" };
        _db.KnowledgeItems.Add(item);
        await _db.SaveChangesAsync();

        var spec = new KnowledgeByIdWithRelationsSpec(item.Id);
        var result = await _knowledgeRepo.FirstOrDefaultAsync(spec);

        Assert.NotNull(result);
        Assert.Equal("Target", result.Title);
    }

    [Fact]
    public async Task FirstOrDefaultAsync_ReturnsNull_WhenNoMatch()
    {
        var spec = new KnowledgeByIdWithRelationsSpec(Guid.NewGuid());
        var result = await _knowledgeRepo.FirstOrDefaultAsync(spec);

        Assert.Null(result);
    }

    [Fact]
    public async Task CountAsync_ReturnsCorrectCount()
    {
        _db.Tags.AddRange(
            new Tag { TenantId = TenantId, Name = "one" },
            new Tag { TenantId = TenantId, Name = "two" },
            new Tag { TenantId = TenantId, Name = "three" });
        await _db.SaveChangesAsync();

        var spec = new TagsByNamesSpec(new[] { "one", "three" });
        var count = await _tagRepo.CountAsync(spec);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task SaveChangesAsync_PersistsChanges()
    {
        var item = new Knowledge { TenantId = TenantId, Title = "Persist Test", Content = "Content" };
        await _knowledgeRepo.AddAsync(item);

        // Before SaveChanges, item should not be findable via a new query
        var beforeSave = await _db.KnowledgeItems.AsNoTracking().CountAsync();
        Assert.Equal(0, beforeSave);

        await _knowledgeRepo.SaveChangesAsync();

        var afterSave = await _db.KnowledgeItems.AsNoTracking().CountAsync();
        Assert.Equal(1, afterSave);
    }
}
