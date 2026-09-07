using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class InboxServiceTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly InboxService _svc;
    private readonly KnowledgeService _knowledgeSvc;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public InboxServiceTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);

        _db = new SelfHostedDbContext(options, tenantProvider);

        var inboxRepo = new SelfHostedRepository<InboxItem>(_db);
        var knowledgeRepo = new SelfHostedRepository<Knowledge>(_db);
        var tagRepo = new SelfHostedRepository<Tag>(_db);
        var searchService = Substitute.For<ISearchService>();
        var openAIService = Substitute.For<IOpenAIService>();
        openAIService.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f, 0.2f, 0.3f });

        var knowledgeLogger = Substitute.For<ILogger<KnowledgeService>>();
        var chunkingService = new SelfHostedChunkingService();
        _knowledgeSvc = new KnowledgeService(
            knowledgeRepo, tagRepo, _db, searchService, openAIService, chunkingService, tenantProvider, knowledgeLogger);

        var configuration = new ConfigurationBuilder().Build();
        var logger = Substitute.For<ILogger<InboxService>>();
        _svc = new InboxService(inboxRepo, _db, _knowledgeSvc, tenantProvider, configuration, logger);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // --- Existing: Create ---

    [Fact]
    public async Task CreateInboxItemAsync_ReturnsInboxItemResult()
    {
        var result = await _svc.CreateInboxItemAsync("Test inbox body", null, CancellationToken.None);

        Assert.IsType<InboxItemResult>(result);
        Assert.True(result.Created);
        Assert.NotEqual(Guid.Empty, result.Id);
    }

    [Fact]
    public async Task CreateInboxItemAsync_PersistsItem()
    {
        var result = await _svc.CreateInboxItemAsync("Persisted body", null, CancellationToken.None);

        var saved = await _db.InboxItems.FindAsync(result.Id);
        Assert.NotNull(saved);
        Assert.Equal("Persisted body", saved.Body);
    }

    [Fact]
    public async Task CreateInboxItemAsync_SetsTenantId()
    {
        var result = await _svc.CreateInboxItemAsync("Tenant test", null, CancellationToken.None);

        var saved = await _db.InboxItems.FindAsync(result.Id);
        Assert.NotNull(saved);
        Assert.Equal(TenantId, saved.TenantId);
    }

    // --- List (paginated) ---

    [Fact]
    public async Task Should_ReturnPaginatedList_WhenItemsExist()
    {
        await SeedItems(5);

        var result = await _svc.ListInboxItemsAsync(1, 3, null, null, null, false, CancellationToken.None);

        Assert.Equal(3, result.Items.Count);
        Assert.Equal(1, result.Page);
        Assert.Equal(3, result.PageSize);
        Assert.Equal(5, result.TotalItems);
        Assert.Equal(2, result.TotalPages);
    }

    [Fact]
    public async Task Should_ReturnEmptyList_WhenNoItems()
    {
        var result = await _svc.ListInboxItemsAsync(1, 10, null, null, null, false, CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalItems);
    }

    [Fact]
    public async Task Should_ReturnSecondPage_WhenPageIs2()
    {
        await SeedItems(5);

        var result = await _svc.ListInboxItemsAsync(2, 3, null, null, null, false, CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(2, result.Page);
    }

    // --- List with search filter ---

    [Fact]
    public async Task Should_FilterBySearch_WhenSearchProvided()
    {
        await SeedItem("Alpha important note");
        await SeedItem("Beta random text");
        await SeedItem("Gamma important idea");

        var result = await _svc.ListInboxItemsAsync(1, 10, "important", null, null, false, CancellationToken.None);

        Assert.Equal(2, result.TotalItems);
        Assert.All(result.Items, item => Assert.Contains("important", item.Body, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Should_FilterBySearch_CaseInsensitive()
    {
        await SeedItem("UPPERCASE TEXT");

        var result = await _svc.ListInboxItemsAsync(1, 10, "uppercase", null, null, false, CancellationToken.None);

        Assert.Equal(1, result.TotalItems);
    }

    // --- List with type filter ---

    [Fact]
    public async Task Should_FilterByType_WhenTypeProvided()
    {
        await SeedItem("A note", InboxItemType.Note);
        await SeedItem("A link", InboxItemType.Link);
        await SeedItem("Another note", InboxItemType.Note);

        var result = await _svc.ListInboxItemsAsync(1, 10, null, "Note", null, false, CancellationToken.None);

        Assert.Equal(2, result.TotalItems);
        Assert.All(result.Items, item => Assert.Equal("Note", item.Type));
    }

    // --- Get by ID ---

    [Fact]
    public async Task Should_ReturnItem_WhenIdExists()
    {
        var created = await _svc.CreateInboxItemAsync("Get me", null, CancellationToken.None);

        var result = await _svc.GetInboxItemAsync(created.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(created.Id, result.Id);
        Assert.Equal("Get me", result.Body);
    }

    [Fact]
    public async Task Should_ReturnNull_WhenIdNotFound()
    {
        var result = await _svc.GetInboxItemAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }

    // --- Update ---

    [Fact]
    public async Task Should_UpdateBody_WhenItemExists()
    {
        var created = await _svc.CreateInboxItemAsync("Original", null, CancellationToken.None);

        var result = await _svc.UpdateInboxItemAsync(created.Id, "Updated body", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Updated body", result.Body);
    }

    [Fact]
    public async Task Should_UpdateTimestamp_WhenItemUpdated()
    {
        var created = await _svc.CreateInboxItemAsync("Original", null, CancellationToken.None);
        var beforeUpdate = DateTime.UtcNow;

        await Task.Delay(10); // small delay so timestamps differ
        var result = await _svc.UpdateInboxItemAsync(created.Id, "Updated", CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.UpdatedAt >= beforeUpdate);
    }

    [Fact]
    public async Task Should_ReturnNull_WhenUpdatingNonExistentItem()
    {
        var result = await _svc.UpdateInboxItemAsync(Guid.NewGuid(), "body", CancellationToken.None);

        Assert.Null(result);
    }

    // --- Delete (soft) ---

    [Fact]
    public async Task Should_SoftDeleteItem_WhenItemExists()
    {
        var created = await _svc.CreateInboxItemAsync("Delete me", null, CancellationToken.None);

        var result = await _svc.DeleteInboxItemAsync(created.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(created.Id, result.Id);
        Assert.True(result.Deleted);
    }

    [Fact]
    public async Task Should_NotAppearInList_AfterSoftDelete()
    {
        var created = await _svc.CreateInboxItemAsync("Will be deleted", null, CancellationToken.None);
        await _svc.DeleteInboxItemAsync(created.Id, CancellationToken.None);

        var list = await _svc.ListInboxItemsAsync(1, 10, null, null, null, false, CancellationToken.None);

        Assert.DoesNotContain(list.Items, i => i.Id == created.Id);
    }

    [Fact]
    public async Task Should_ReturnNull_WhenDeletingNonExistentItem()
    {
        var result = await _svc.DeleteInboxItemAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }

    // --- Convert to Knowledge ---

    [Fact]
    public async Task Should_ConvertToKnowledge_WhenItemExists()
    {
        var created = await _svc.CreateInboxItemAsync("Knowledge-worthy content", null, CancellationToken.None);

        var result = await _svc.ConvertToKnowledgeAsync(created.Id, null, null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Converted);
        Assert.Equal(created.Id, result.InboxItemId);
        Assert.NotEqual(Guid.Empty, result.KnowledgeId);
    }

    [Fact]
    public async Task Should_SoftDeleteInboxItem_AfterConversion()
    {
        var created = await _svc.CreateInboxItemAsync("Convert me", null, CancellationToken.None);
        await _svc.ConvertToKnowledgeAsync(created.Id, null, null, CancellationToken.None);

        // Item should be soft-deleted and not appear in list
        var list = await _svc.ListInboxItemsAsync(1, 10, null, null, null, false, CancellationToken.None);
        Assert.DoesNotContain(list.Items, i => i.Id == created.Id);
    }

    [Fact]
    public async Task Should_CreateKnowledgeWithInboxBody_AfterConversion()
    {
        var created = await _svc.CreateInboxItemAsync("My special content", null, CancellationToken.None);

        var result = await _svc.ConvertToKnowledgeAsync(created.Id, null, null, CancellationToken.None);

        Assert.NotNull(result);
        var knowledge = await _db.KnowledgeItems.FindAsync(result.KnowledgeId);
        Assert.NotNull(knowledge);
        Assert.Equal("My special content", knowledge.Content);
    }

    [Fact]
    public async Task Should_ReturnNull_WhenConvertingNonExistentItem()
    {
        var result = await _svc.ConvertToKnowledgeAsync(Guid.NewGuid(), null, null, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Should_ConvertWithVaultId_WhenProvided()
    {
        // Create a vault first
        var vault = new Vault { TenantId = TenantId, Name = "TestVault" };
        _db.Vaults.Add(vault);
        await _db.SaveChangesAsync();

        var created = await _svc.CreateInboxItemAsync("Vault content", null, CancellationToken.None);

        var result = await _svc.ConvertToKnowledgeAsync(
            created.Id, vault.Id.ToString(), null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Converted);
    }

    // --- Batch Convert ---

    [Fact]
    public async Task Should_BatchConvert_AllValidIds()
    {
        var id1 = (await _svc.CreateInboxItemAsync("Item 1", null, CancellationToken.None)).Id;
        var id2 = (await _svc.CreateInboxItemAsync("Item 2", null, CancellationToken.None)).Id;
        var id3 = (await _svc.CreateInboxItemAsync("Item 3", null, CancellationToken.None)).Id;

        var result = await _svc.BatchConvertToKnowledgeAsync(
            new List<Guid> { id1, id2, id3 }, null, null, CancellationToken.None);

        Assert.Equal(3, result.Requested);
        Assert.Equal(3, result.Converted);
        Assert.Equal(0, result.Failed);
        Assert.Equal(3, result.Results.Count);
    }

    [Fact]
    public async Task Should_ReportFailures_WhenSomeIdsInvalid()
    {
        var validId = (await _svc.CreateInboxItemAsync("Valid item", null, CancellationToken.None)).Id;
        var invalidId = Guid.NewGuid();

        var result = await _svc.BatchConvertToKnowledgeAsync(
            new List<Guid> { validId, invalidId }, null, null, CancellationToken.None);

        Assert.Equal(2, result.Requested);
        Assert.Equal(1, result.Converted);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task Should_ThrowOrError_WhenBatchExceeds50()
    {
        var ids = Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToList();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _svc.BatchConvertToKnowledgeAsync(ids, null, null, CancellationToken.None));
    }

    // --- Tenant isolation ---

    [Fact]
    public async Task Should_NotReturnItemsFromOtherTenant()
    {
        // Seed an item directly with a different tenant
        var otherTenantItem = new InboxItem
        {
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000099"),
            Body = "Other tenant item"
        };
        _db.InboxItems.Add(otherTenantItem);
        await _db.SaveChangesAsync();

        // List should not include the other tenant's item (query filter on DbContext)
        var result = await _svc.ListInboxItemsAsync(1, 10, null, null, null, false, CancellationToken.None);

        Assert.DoesNotContain(result.Items, i => i.Body == "Other tenant item");
    }

    // --- DTO mapping ---

    [Fact]
    public async Task Should_MapCorrectFields_InListResponse()
    {
        await SeedItem("My body text", InboxItemType.Link);

        var result = await _svc.ListInboxItemsAsync(1, 10, null, null, null, false, CancellationToken.None);

        Assert.Single(result.Items);
        var item = result.Items[0];
        Assert.Equal("My body text", item.Body);
        Assert.Equal("Link", item.Type);
        Assert.True(item.CreatedAt <= DateTime.UtcNow);
    }

    // --- Helpers ---

    private async Task SeedItems(int count)
    {
        for (int i = 0; i < count; i++)
        {
            await _svc.CreateInboxItemAsync($"Item {i}", null, CancellationToken.None);
        }
    }

    private async Task SeedItem(string body, InboxItemType type = InboxItemType.Note)
    {
        var item = new InboxItem
        {
            TenantId = TenantId,
            Body = body,
            Type = type
        };
        _db.InboxItems.Add(item);
        await _db.SaveChangesAsync();
    }
}
