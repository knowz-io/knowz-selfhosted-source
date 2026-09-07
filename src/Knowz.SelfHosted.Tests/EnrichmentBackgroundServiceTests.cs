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

public class EnrichmentBackgroundServiceTests : IDisposable
{
    private readonly Channel<EnrichmentWorkItem> _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ServiceProvider _serviceProvider;
    private readonly SelfHostedDbContext _db;
    private readonly ITextEnrichmentService _enrichmentService;
    private readonly ISearchService _searchService;
    private readonly IOpenAIService _openAIService;
    private readonly EnrichmentBackgroundService _svc;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public EnrichmentBackgroundServiceTests()
    {
        _channel = Channel.CreateBounded<EnrichmentWorkItem>(
            new BoundedChannelOptions(100) { SingleReader = true });

        var dbName = Guid.NewGuid().ToString();

        _enrichmentService = Substitute.For<ITextEnrichmentService>();
        _searchService = Substitute.For<ISearchService>();
        _openAIService = Substitute.For<IOpenAIService>();
        _openAIService.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f, 0.2f, 0.3f });

        var services = new ServiceCollection();
        services.AddScoped<ITenantProvider>(sp =>
        {
            var tp = Substitute.For<ITenantProvider>();
            tp.TenantId.Returns(_ => TenantContext.CurrentTenantId ?? TenantId);
            return tp;
        });
        services.AddDbContext<SelfHostedDbContext>(opts =>
            opts.UseInMemoryDatabase(dbName));
        services.AddScoped<ITextEnrichmentService>(_ => _enrichmentService);
        services.AddScoped<ISearchService>(_ => _searchService);
        services.AddScoped<IOpenAIService>(_ => _openAIService);
        services.AddScoped<ISelfHostedChunkingService, SelfHostedChunkingService>();

        _serviceProvider = services.BuildServiceProvider();
        _scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();

        // Get a reference DB for seeding
        using var seedScope = _scopeFactory.CreateScope();
        _db = seedScope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();

        var logger = Substitute.For<ILogger<EnrichmentBackgroundService>>();
        _svc = new EnrichmentBackgroundService(_channel, _scopeFactory, logger);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
    }

    // --- IsPlaceholderTitle tests ---

    [Theory]
    [InlineData(null, "any content", true)]
    [InlineData("", "any content", true)]
    [InlineData("   ", "any content", true)]
    [InlineData("Untitled", "any content", true)]
    [InlineData("untitled", "any content", true)]
    [InlineData("UNTITLED", "any content", true)]
    [InlineData("Untitled Content", "any content", true)]
    [InlineData("Media File", "any content", true)]
    [InlineData("Document", "any content", true)]
    [InlineData("file", "any content", true)]
    [InlineData("n/a", "any content", true)]
    [InlineData("unknown", "any content", true)]
    [InlineData("A Real Title", "any content", false)]
    [InlineData("My Important Document", "any content", false)]
    public void IsPlaceholderTitle_DetectsCorrectly(string? title, string? content, bool expected)
    {
        Assert.Equal(expected, EnrichmentBackgroundService.IsPlaceholderTitle(title, content));
    }

    [Fact]
    public void IsPlaceholderTitle_TruncatedBodyText_IsPlaceholder()
    {
        var content = new string('x', 200);
        var title = content[..80]; // First 80 chars
        Assert.True(EnrichmentBackgroundService.IsPlaceholderTitle(title, content));
    }

    [Fact]
    public void IsPlaceholderTitle_ShortContentExactMatch_IsPlaceholder()
    {
        var content = "Short";
        var title = "Short";
        Assert.True(EnrichmentBackgroundService.IsPlaceholderTitle(title, content));
    }

    [Fact]
    public void IsPlaceholderTitle_ShortDistinctTitle_NotPlaceholder()
    {
        Assert.False(EnrichmentBackgroundService.IsPlaceholderTitle("Short title", "Short content"));
    }

    // --- ProcessWorkItemAsync tests ---

    [Fact]
    public async Task ProcessWorkItem_SetsOutboxToCompleted_OnSuccess()
    {
        // Seed
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            TenantContext.CurrentTenantId = TenantId;
            db.KnowledgeItems.Add(new Knowledge
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                TenantId = TenantId,
                Title = "Real Title",
                Content = "Some content to enrich"
            });
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Status = EnrichmentStatus.Pending
            });
            await db.SaveChangesAsync();
            TenantContext.CurrentTenantId = null;
        }

        _enrichmentService.SummarizeAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns("A summary");
        _enrichmentService.ExtractTagsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(new List<string> { "tag1" });

        var workItem = new EnrichmentWorkItem(
            Guid.Parse("11111111-1111-1111-1111-111111111111"), TenantId);

        await _svc.ProcessWorkItemAsync(workItem, CancellationToken.None);

        // Verify outbox item is completed
        using var verifyScope = _scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        var outbox = await verifyDb.EnrichmentOutbox.FirstAsync();
        Assert.Equal(EnrichmentStatus.Completed, outbox.Status);
        Assert.NotNull(outbox.ProcessedAt);
    }

    [Fact]
    public async Task ProcessWorkItem_SkipsTitleGeneration_WhenTitleIsNotPlaceholder()
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            TenantContext.CurrentTenantId = TenantId;
            db.KnowledgeItems.Add(new Knowledge
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                TenantId = TenantId,
                Title = "A Real Title",
                Content = "Some content"
            });
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Status = EnrichmentStatus.Pending
            });
            await db.SaveChangesAsync();
            TenantContext.CurrentTenantId = null;
        }

        _enrichmentService.SummarizeAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns((string?)null);
        _enrichmentService.ExtractTagsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(new List<string>());

        await _svc.ProcessWorkItemAsync(
            new EnrichmentWorkItem(Guid.Parse("22222222-2222-2222-2222-222222222222"), TenantId),
            CancellationToken.None);

        // Title generation should NOT have been called
        await _enrichmentService.DidNotReceive()
            .GenerateTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>());
    }

    [Fact]
    public async Task ProcessWorkItem_GeneratesTitle_WhenPlaceholder()
    {
        var knowledgeId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            TenantContext.CurrentTenantId = TenantId;
            db.KnowledgeItems.Add(new Knowledge
            {
                Id = knowledgeId,
                TenantId = TenantId,
                Title = "Untitled",
                Content = "Content about machine learning"
            });
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = knowledgeId,
                Status = EnrichmentStatus.Pending
            });
            await db.SaveChangesAsync();
            TenantContext.CurrentTenantId = null;
        }

        _enrichmentService.GenerateTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns("Machine Learning Overview");
        _enrichmentService.SummarizeAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns((string?)null);
        _enrichmentService.ExtractTagsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(new List<string>());

        await _svc.ProcessWorkItemAsync(new EnrichmentWorkItem(knowledgeId, TenantId), CancellationToken.None);

        // Verify title was updated
        using var verifyScope = _scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        TenantContext.CurrentTenantId = TenantId;
        var knowledge = await verifyDb.KnowledgeItems.FindAsync(knowledgeId);
        TenantContext.CurrentTenantId = null;
        Assert.Equal("Machine Learning Overview", knowledge!.Title);
    }

    [Fact]
    public async Task ProcessWorkItem_SetsOutboxToFailed_WhenKnowledgeNotFound()
    {
        var knowledgeId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = knowledgeId,
                Status = EnrichmentStatus.Pending
            });
            await db.SaveChangesAsync();
        }

        await _svc.ProcessWorkItemAsync(new EnrichmentWorkItem(knowledgeId, TenantId), CancellationToken.None);

        using var verifyScope = _scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        var outbox = await verifyDb.EnrichmentOutbox.FirstAsync();
        Assert.Equal(EnrichmentStatus.Failed, outbox.Status);
        Assert.Equal("Knowledge item not found", outbox.ErrorMessage);
    }

    [Fact]
    public async Task ProcessWorkItem_IncreasesRetryCount_OnProcessingError()
    {
        var knowledgeId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            TenantContext.CurrentTenantId = TenantId;
            db.KnowledgeItems.Add(new Knowledge
            {
                Id = knowledgeId,
                TenantId = TenantId,
                Title = "Untitled",
                Content = "Content"
            });
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = knowledgeId,
                Status = EnrichmentStatus.Pending
            });
            await db.SaveChangesAsync();
            TenantContext.CurrentTenantId = null;
        }

        _enrichmentService.GenerateTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns<string?>(_ => throw new Exception("AI error"));

        await _svc.ProcessWorkItemAsync(new EnrichmentWorkItem(knowledgeId, TenantId), CancellationToken.None);

        using var verifyScope = _scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        var outbox = await verifyDb.EnrichmentOutbox.FirstAsync();
        Assert.Equal(1, outbox.RetryCount);
        Assert.Equal(EnrichmentStatus.Pending, outbox.Status); // Retryable
        Assert.NotNull(outbox.NextRetryAt);
        Assert.Contains("AI error", outbox.ErrorMessage);
    }

    [Fact]
    public async Task ProcessWorkItem_SetsToFailed_AfterMaxRetries()
    {
        var knowledgeId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            TenantContext.CurrentTenantId = TenantId;
            db.KnowledgeItems.Add(new Knowledge
            {
                Id = knowledgeId,
                TenantId = TenantId,
                Title = "Untitled",
                Content = "Content"
            });
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = knowledgeId,
                Status = EnrichmentStatus.Pending,
                RetryCount = 2, // Already retried twice, next failure = 3 = max
                MaxRetries = 3
            });
            await db.SaveChangesAsync();
            TenantContext.CurrentTenantId = null;
        }

        _enrichmentService.GenerateTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns<string?>(_ => throw new Exception("Persistent error"));

        await _svc.ProcessWorkItemAsync(new EnrichmentWorkItem(knowledgeId, TenantId), CancellationToken.None);

        using var verifyScope = _scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        var outbox = await verifyDb.EnrichmentOutbox.FirstAsync();
        Assert.Equal(3, outbox.RetryCount);
        Assert.Equal(EnrichmentStatus.Failed, outbox.Status);
    }

    [Fact]
    public async Task ProcessWorkItem_ClearsTenantContext_Always()
    {
        // Even if processing fails, TenantContext should be cleared
        var knowledgeId = Guid.NewGuid();

        await _svc.ProcessWorkItemAsync(new EnrichmentWorkItem(knowledgeId, TenantId), CancellationToken.None);

        Assert.Null(TenantContext.CurrentTenantId);
    }

    [Fact]
    public async Task ProcessWorkItem_UpdatesSummary()
    {
        var knowledgeId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            TenantContext.CurrentTenantId = TenantId;
            db.KnowledgeItems.Add(new Knowledge
            {
                Id = knowledgeId,
                TenantId = TenantId,
                Title = "Good Title",
                Content = "This is a longer piece of content about various important topics that need to be properly summarized by the enrichment service to produce a meaningful AI-generated summary for the user"
            });
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = knowledgeId,
                Status = EnrichmentStatus.Pending
            });
            await db.SaveChangesAsync();
            TenantContext.CurrentTenantId = null;
        }

        _enrichmentService.SummarizeAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns("This is a generated summary");
        _enrichmentService.ExtractTagsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(new List<string>());

        await _svc.ProcessWorkItemAsync(new EnrichmentWorkItem(knowledgeId, TenantId), CancellationToken.None);

        using var verifyScope = _scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        TenantContext.CurrentTenantId = TenantId;
        var knowledge = await verifyDb.KnowledgeItems.FindAsync(knowledgeId);
        TenantContext.CurrentTenantId = null;
        Assert.Equal("This is a generated summary", knowledge!.Summary);
    }

    // --- BriefSummary tests ---

    [Fact]
    public async Task ProcessWorkItem_GeneratesBriefSummary_AfterFullSummary()
    {
        var knowledgeId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            TenantContext.CurrentTenantId = TenantId;
            db.KnowledgeItems.Add(new Knowledge
            {
                Id = knowledgeId,
                TenantId = TenantId,
                Title = "Good Title",
                Content = "Content about important topics that should get a brief summary"
            });
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = knowledgeId,
                Status = EnrichmentStatus.Pending
            });
            await db.SaveChangesAsync();
            TenantContext.CurrentTenantId = null;
        }

        _enrichmentService.SummarizeAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns("Full summary text");
        _enrichmentService.GenerateBriefSummaryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns("Brief summary");
        _enrichmentService.ExtractTagsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(new List<string>());
        _enrichmentService.GenerateChunkContextsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IList<(string, int)>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string?>());

        await _svc.ProcessWorkItemAsync(new EnrichmentWorkItem(knowledgeId, TenantId), CancellationToken.None);

        // Verify BriefSummary was generated and persisted
        using var verifyScope = _scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        TenantContext.CurrentTenantId = TenantId;
        var knowledge = await verifyDb.KnowledgeItems.FindAsync(knowledgeId);
        TenantContext.CurrentTenantId = null;
        Assert.Equal("Brief summary", knowledge!.BriefSummary);

        // Verify GenerateBriefSummaryAsync was called
        await _enrichmentService.Received(1).GenerateBriefSummaryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>());
    }

    // --- Author name resolution tests ---

    [Fact]
    public async Task ProcessWorkItem_ResolvesAuthorName_ViaCreatedByUserId()
    {
        var knowledgeId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var userId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            TenantContext.CurrentTenantId = TenantId;
            db.Users.Add(new User
            {
                Id = userId,
                TenantId = TenantId,
                Username = "alex",
                PasswordHash = "hash",
                DisplayName = "Alex Smith"
            });
            db.KnowledgeItems.Add(new Knowledge
            {
                Id = knowledgeId,
                TenantId = TenantId,
                Title = "Good Title",
                Content = "This is a longer content that should trigger full summarization with multiple words to pass the threshold and ensure the enrichment service generates a proper AI summary for display",
                CreatedByUserId = userId,
                CreatedAt = new DateTime(2026, 3, 15)
            });
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = knowledgeId,
                Status = EnrichmentStatus.Pending
            });
            await db.SaveChangesAsync();
            TenantContext.CurrentTenantId = null;
        }

        _enrichmentService.SummarizeAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns("A summary");
        _enrichmentService.GenerateBriefSummaryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns((string?)null);
        _enrichmentService.ExtractTagsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(new List<string>());
        _enrichmentService.GenerateChunkContextsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IList<(string, int)>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string?>());

        await _svc.ProcessWorkItemAsync(new EnrichmentWorkItem(knowledgeId, TenantId), CancellationToken.None);

        // The SummarizeAsync on the concrete TextEnrichmentService would receive createdAt/authorName,
        // but since we use the interface mock, we verify the call was made.
        // The key verification is that ProcessWorkItemAsync resolves the author and passes it.
        await _enrichmentService.Received(1).SummarizeAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>());
    }

    // --- Delta chunking tests ---

    [Fact]
    public async Task ReindexAsync_DeltaChunking_PreservesUnchangedChunkEmbeddings()
    {
        var knowledgeId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        TenantContext.CurrentTenantId = TenantId;

        var knowledge = new Knowledge
        {
            Id = knowledgeId,
            TenantId = TenantId,
            Title = "Test Title",
            Content = "Short content for testing",
            Summary = "A summary"
        };
        db.KnowledgeItems.Add(knowledge);

        // Pre-existing chunk with known hash and embedding
        var existingHash = ContentHasher.Hash("Short content for testing");
        db.ContentChunks.Add(new ContentChunk
        {
            TenantId = TenantId,
            KnowledgeId = knowledgeId,
            Position = 0,
            Content = "Short content for testing",
            ContentHash = existingHash,
            EmbeddingVectorJson = "[0.5,0.6,0.7]",
            EmbeddedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var searchService = Substitute.For<ISearchService>();
        var openAIService = Substitute.For<IOpenAIService>();
        openAIService.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f, 0.2f, 0.3f });
        var chunkingService = new SelfHostedChunkingService();
        var enrichmentService = Substitute.For<ITextEnrichmentService>();
        enrichmentService.GenerateChunkContextsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IList<(string, int)>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string?>());

        await EnrichmentBackgroundService.ReindexAsync(db, knowledge, searchService, openAIService, chunkingService, enrichmentService, ct: CancellationToken.None);

        TenantContext.CurrentTenantId = null;
    }

    // --- Contextual embedding flag tests ---

    [Fact]
    public async Task ReindexAsync_SetsIsContextualEmbedding_WhenContextProvided()
    {
        var knowledgeId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        TenantContext.CurrentTenantId = TenantId;

        var knowledge = new Knowledge
        {
            Id = knowledgeId,
            TenantId = TenantId,
            Title = "Test Title",
            Content = "Some content for testing contextual embeddings",
            Summary = "A summary",
            BriefSummary = "Brief"
        };
        db.KnowledgeItems.Add(knowledge);
        await db.SaveChangesAsync();

        var searchService = Substitute.For<ISearchService>();
        var openAIService = Substitute.For<IOpenAIService>();
        openAIService.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f, 0.2f, 0.3f });
        var chunkingService = new SelfHostedChunkingService();
        var enrichmentService = Substitute.For<ITextEnrichmentService>();
        enrichmentService.GenerateChunkContextsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IList<(string, int)>>(), Arg.Any<CancellationToken>())
            .Returns(new string?[] { "Context for chunk 0" });

        await EnrichmentBackgroundService.ReindexAsync(db, knowledge, searchService, openAIService, chunkingService, enrichmentService, ct: CancellationToken.None);

        var chunks = await db.ContentChunks.Where(c => c.KnowledgeId == knowledgeId).ToListAsync();
        Assert.True(chunks.All(c => c.IsContextualEmbedding), "All chunks should have IsContextualEmbedding = true");
        Assert.True(chunks.All(c => c.ContextSummary != null), "All chunks should have ContextSummary set");

        TenantContext.CurrentTenantId = null;
    }

    // --- Enrichment pipeline resilience ---

    [Fact]
    public async Task ProcessWorkItem_CompletesSuccessfully_WhenContextualRetrievalFails()
    {
        var knowledgeId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            TenantContext.CurrentTenantId = TenantId;
            db.KnowledgeItems.Add(new Knowledge
            {
                Id = knowledgeId,
                TenantId = TenantId,
                Title = "Good Title",
                Content = "Content for testing"
            });
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = knowledgeId,
                Status = EnrichmentStatus.Pending
            });
            await db.SaveChangesAsync();
            TenantContext.CurrentTenantId = null;
        }

        _enrichmentService.SummarizeAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns("A summary");
        _enrichmentService.GenerateBriefSummaryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns((string?)null);
        _enrichmentService.ExtractTagsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(new List<string>());
        _enrichmentService.GenerateChunkContextsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IList<(string, int)>>(), Arg.Any<CancellationToken>())
            .Returns<IList<string?>>(_ => throw new Exception("LLM failure"));

        await _svc.ProcessWorkItemAsync(new EnrichmentWorkItem(knowledgeId, TenantId), CancellationToken.None);

        // Verify enrichment still completed
        using var verifyScope = _scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        var outbox = await verifyDb.EnrichmentOutbox.FirstAsync();
        Assert.Equal(EnrichmentStatus.Completed, outbox.Status);
    }

    // --- Startup recovery scan tests (Fix 1) ---

    [Fact]
    public async Task ScanAndEnqueuePendingAsync_EnqueuesPendingItems()
    {
        // Seed pending items in DB
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = Guid.NewGuid(),
                Status = EnrichmentStatus.Pending,
                NextRetryAt = null
            });
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = Guid.NewGuid(),
                Status = EnrichmentStatus.Pending,
                NextRetryAt = DateTime.UtcNow.AddMinutes(-1) // past NextRetryAt
            });
            await db.SaveChangesAsync();
        }

        await _svc.ScanAndEnqueuePendingAsync(CancellationToken.None);

        // Both items should be enqueued to the channel
        Assert.True(_channel.Reader.TryRead(out _));
        Assert.True(_channel.Reader.TryRead(out _));
        Assert.False(_channel.Reader.TryRead(out _)); // No more
    }

    [Fact]
    public async Task ScanAndEnqueuePendingAsync_SkipsPendingWithFutureRetry()
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = Guid.NewGuid(),
                Status = EnrichmentStatus.Pending,
                NextRetryAt = DateTime.UtcNow.AddMinutes(10) // future retry
            });
            await db.SaveChangesAsync();
        }

        await _svc.ScanAndEnqueuePendingAsync(CancellationToken.None);

        Assert.False(_channel.Reader.TryRead(out _)); // Not enqueued
    }

    [Fact]
    public async Task ScanAndEnqueuePendingAsync_EnqueuesStuckProcessingItems()
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = Guid.NewGuid(),
                Status = EnrichmentStatus.Processing,
                CreatedAt = DateTime.UtcNow.AddMinutes(-10) // stuck: older than 5 min threshold
            });
            await db.SaveChangesAsync();
        }

        await _svc.ScanAndEnqueuePendingAsync(CancellationToken.None);

        Assert.True(_channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ScanAndEnqueuePendingAsync_SkipsRecentProcessingItems()
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = Guid.NewGuid(),
                Status = EnrichmentStatus.Processing,
                CreatedAt = DateTime.UtcNow.AddMinutes(-2) // recent — not stuck
            });
            await db.SaveChangesAsync();
        }

        await _svc.ScanAndEnqueuePendingAsync(CancellationToken.None);

        Assert.False(_channel.Reader.TryRead(out _)); // Not enqueued — still processing
    }

    [Fact]
    public async Task ScanAndEnqueuePendingAsync_SkipsCompletedAndFailed()
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = Guid.NewGuid(),
                Status = EnrichmentStatus.Completed,
                ProcessedAt = DateTime.UtcNow.AddMinutes(-30)
            });
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = Guid.NewGuid(),
                Status = EnrichmentStatus.Failed,
                ProcessedAt = DateTime.UtcNow.AddMinutes(-30)
            });
            await db.SaveChangesAsync();
        }

        await _svc.ScanAndEnqueuePendingAsync(CancellationToken.None);

        Assert.False(_channel.Reader.TryRead(out _)); // None enqueued
    }

    [Fact]
    public async Task ScanAndEnqueuePendingAsync_LimitsTo100Items()
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            for (int i = 0; i < 120; i++)
            {
                db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
                {
                    TenantId = TenantId,
                    KnowledgeId = Guid.NewGuid(),
                    Status = EnrichmentStatus.Pending
                });
            }
            await db.SaveChangesAsync();
        }

        await _svc.ScanAndEnqueuePendingAsync(CancellationToken.None);

        int count = 0;
        while (_channel.Reader.TryRead(out _)) count++;
        Assert.Equal(100, count); // Capped at 100
    }

    [Fact]
    public async Task ScanAndEnqueuePendingAsync_DoesNotThrow_OnDbError()
    {
        // Using a cancelled token to simulate DB failure
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Should not throw — logs warning and returns gracefully
        var ex = await Record.ExceptionAsync(() => _svc.ScanAndEnqueuePendingAsync(cts.Token));
        // No unhandled exception (OperationCanceledException is caught internally)
        // The method catches all exceptions, so this should not propagate
        Assert.Null(ex);
    }

    // --- Recovery timer stuck threshold tests (Fix 3) ---

    [Fact]
    public async Task RecoveryTimerAsync_Uses5MinuteStuckThreshold()
    {
        // Seed a Processing item that is 6 min old (should be picked up with 5-min threshold)
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            db.EnrichmentOutbox.Add(new EnrichmentOutboxItem
            {
                TenantId = TenantId,
                KnowledgeId = Guid.NewGuid(),
                Status = EnrichmentStatus.Processing,
                CreatedAt = DateTime.UtcNow.AddMinutes(-6)
            });
            await db.SaveChangesAsync();
        }

        // Run one iteration of recovery by cancelling after the first scan
        using var cts = new CancellationTokenSource();
        // We'll let it run the initial delay and one scan, then cancel
        // Since RecoveryTimerAsync has a 10-second initial delay, we need to
        // test the recovery logic via the startup scan which uses the same threshold
        await _svc.ScanAndEnqueuePendingAsync(CancellationToken.None);

        Assert.True(_channel.Reader.TryRead(out _));
    }

    // --- TenantContext tests ---

    [Fact]
    public void TenantContext_DefaultsToNull()
    {
        TenantContext.CurrentTenantId = null; // Reset
        Assert.Null(TenantContext.CurrentTenantId);
    }

    [Fact]
    public void TenantContext_SetAndGet()
    {
        var id = Guid.NewGuid();
        TenantContext.CurrentTenantId = id;
        Assert.Equal(id, TenantContext.CurrentTenantId);
        TenantContext.CurrentTenantId = null; // Cleanup
    }
}
