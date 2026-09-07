using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class KnowledgeServiceTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly ISearchService _searchService;
    private readonly IOpenAIService _openAIService;
    private readonly KnowledgeService _svc;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public KnowledgeServiceTests()
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

        var logger = Substitute.For<ILogger<KnowledgeService>>();

        var knowledgeRepo = new SelfHostedRepository<Knowledge>(_db);
        var tagRepo = new SelfHostedRepository<Tag>(_db);

        var chunkingService = new SelfHostedChunkingService();
        _svc = new KnowledgeService(knowledgeRepo, tagRepo, _db, _searchService, _openAIService, chunkingService, tenantProvider, logger);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // --- FIX_Knowledge404: GET returns null when not found ---

    [Fact]
    public async Task GetKnowledgeItemAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _svc.GetKnowledgeItemAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetKnowledgeItemAsync_ReturnsItem_WhenExists()
    {
        var item = new Knowledge
        {
            TenantId = TenantId,
            Title = "Test",
            Content = "Content"
        };
        _db.KnowledgeItems.Add(item);
        await _db.SaveChangesAsync();

        var result = await _svc.GetKnowledgeItemAsync(item.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.IsType<KnowledgeItemResponse>(result);
    }

    // --- FIX_Knowledge404: PUT returns null when not found ---

    [Fact]
    public async Task UpdateKnowledgeAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _svc.UpdateKnowledgeAsync(
            Guid.NewGuid(), "New Title", null, null, null, null, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateKnowledgeAsync_ReturnsObject_WhenExists()
    {
        var item = new Knowledge
        {
            TenantId = TenantId,
            Title = "Old",
            Content = "Content"
        };
        _db.KnowledgeItems.Add(item);
        await _db.SaveChangesAsync();

        var result = await _svc.UpdateKnowledgeAsync(
            item.Id, "New Title", null, null, null, null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.IsType<UpdateKnowledgeResult>(result);
    }

    // --- FIX_DeleteViaService: DELETE returns null when not found ---

    [Fact]
    public async Task DeleteKnowledgeAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _svc.DeleteKnowledgeAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteKnowledgeAsync_SoftDeletes_WhenExists()
    {
        var item = new Knowledge
        {
            TenantId = TenantId,
            Title = "To Delete",
            Content = "Content"
        };
        _db.KnowledgeItems.Add(item);
        await _db.SaveChangesAsync();

        var result = await _svc.DeleteKnowledgeAsync(item.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.IsType<DeleteResult>(result);

        // Verify soft delete - need to bypass query filter
        var deleted = await _db.KnowledgeItems
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(k => k.Id == item.Id);
        Assert.NotNull(deleted);
        Assert.True(deleted.IsDeleted);
    }

    [Fact]
    public async Task DeleteKnowledgeAsync_CallsSearchService()
    {
        var item = new Knowledge
        {
            TenantId = TenantId,
            Title = "To Delete",
            Content = "Content"
        };
        _db.KnowledgeItems.Add(item);
        await _db.SaveChangesAsync();

        await _svc.DeleteKnowledgeAsync(item.Id, CancellationToken.None);

        await _searchService.Received(1).DeleteDocumentAsync(item.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteKnowledgeAsync_DoesNotThrow_WhenSearchFails()
    {
        var item = new Knowledge
        {
            TenantId = TenantId,
            Title = "To Delete",
            Content = "Content"
        };
        _db.KnowledgeItems.Add(item);
        await _db.SaveChangesAsync();

        _searchService.DeleteDocumentAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new Exception("Search unavailable")));

        var result = await _svc.DeleteKnowledgeAsync(item.Id, CancellationToken.None);

        Assert.NotNull(result);
    }

    // --- PERF_BatchTagQuery: Tag creation uses batch query ---

    [Fact]
    public async Task CreateKnowledgeAsync_BatchesTags_ExistingAndNew()
    {
        // Seed an existing tag
        var existingTag = new Tag { TenantId = TenantId, Name = "existing-tag" };
        _db.Tags.Add(existingTag);
        await _db.SaveChangesAsync();

        var tagNames = new List<string> { "existing-tag", "new-tag-1", "new-tag-2" };

        var result = await _svc.CreateKnowledgeAsync(
            "content", "title", "Note", null, tagNames, null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.IsType<CreateKnowledgeResult>(result);

        // Verify all 3 tags exist
        var allTags = await _db.Tags.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(3, allTags.Count);
        Assert.Contains(allTags, t => t.Name == "existing-tag" && t.Id == existingTag.Id);
        Assert.Contains(allTags, t => t.Name == "new-tag-1");
        Assert.Contains(allTags, t => t.Name == "new-tag-2");
    }

    [Fact]
    public async Task UpdateKnowledgeAsync_BatchesTags_ExistingAndNew()
    {
        // Seed existing tag + knowledge item
        var existingTag = new Tag { TenantId = TenantId, Name = "keep-tag" };
        _db.Tags.Add(existingTag);

        var item = new Knowledge
        {
            TenantId = TenantId,
            Title = "Item",
            Content = "Content"
        };
        _db.KnowledgeItems.Add(item);
        await _db.SaveChangesAsync();

        var tagNames = new List<string> { "keep-tag", "brand-new" };

        var result = await _svc.UpdateKnowledgeAsync(
            item.Id, null, null, null, tagNames, null, CancellationToken.None);

        Assert.NotNull(result);

        // Verify tags
        var itemWithTags = await _db.KnowledgeItems
            .Include(k => k.Tags)
            .FirstAsync(k => k.Id == item.Id);
        Assert.Equal(2, itemWithTags.Tags.Count);
        Assert.Contains(itemWithTags.Tags, t => t.Name == "keep-tag" && t.Id == existingTag.Id);
        Assert.Contains(itemWithTags.Tags, t => t.Name == "brand-new");
    }

    [Fact]
    public async Task CreateKnowledgeAsync_ReusesExistingTag_NotDuplicate()
    {
        var existingTag = new Tag { TenantId = TenantId, Name = "shared-tag" };
        _db.Tags.Add(existingTag);
        await _db.SaveChangesAsync();

        await _svc.CreateKnowledgeAsync(
            "content", "title", "Note", null,
            new List<string> { "shared-tag" }, null, CancellationToken.None);

        // Should still be exactly 1 tag with this name
        var tags = await _db.Tags.IgnoreQueryFilters().Where(t => t.Name == "shared-tag").ToListAsync();
        Assert.Single(tags);
        Assert.Equal(existingTag.Id, tags[0].Id);
    }

    // --- BriefSummary: Exposed in KnowledgeItemResponse ---

    [Fact]
    public async Task GetKnowledgeItemAsync_ReturnsBriefSummary_WhenSet()
    {
        var item = new Knowledge
        {
            TenantId = TenantId,
            Title = "Test",
            Content = "Content",
            BriefSummary = "A brief summary of the content"
        };
        _db.KnowledgeItems.Add(item);
        await _db.SaveChangesAsync();

        var result = await _svc.GetKnowledgeItemAsync(item.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("A brief summary of the content", result!.BriefSummary);
    }

    [Fact]
    public async Task GetKnowledgeItemAsync_ReturnsNullBriefSummary_WhenNotSet()
    {
        var item = new Knowledge
        {
            TenantId = TenantId,
            Title = "Test",
            Content = "Content"
        };
        _db.KnowledgeItems.Add(item);
        await _db.SaveChangesAsync();

        var result = await _svc.GetKnowledgeItemAsync(item.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Null(result!.BriefSummary);
    }
}
