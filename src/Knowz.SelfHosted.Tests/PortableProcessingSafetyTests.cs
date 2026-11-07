using System.Threading.Channels;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Models;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed class PortableProcessingSafetyTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid(), _actor = Guid.NewGuid(), _otherTenant = Guid.NewGuid();
    private readonly DbContextOptions<SelfHostedDbContext> _options;
    private readonly ITenantProvider _tenantProvider = Substitute.For<ITenantProvider>();
    private readonly ITextEnrichmentService _enrichment = Substitute.For<ITextEnrichmentService>();
    private readonly IOpenAIService _ai = Substitute.For<IOpenAIService>();
    private readonly ISearchService _search = Substitute.For<ISearchService>();
    private readonly IStreamingOpenAIService _streaming = Substitute.For<IStreamingOpenAIService>();
    private readonly ServiceProvider _services;
    private readonly Channel<EnrichmentWorkItem> _channel = Channel.CreateUnbounded<EnrichmentWorkItem>();
    private sealed class Actor(Guid id) : IPortableActorProvider { public Guid? ActorId => id; }
    public PortableProcessingSafetyTests()
    {
        _options = new DbContextOptionsBuilder<SelfHostedDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _tenantProvider.TenantId.Returns(_tenant);
        using (var db = Open()) { db.Users.Add(new() { Id = _actor, TenantId = _tenant, Username = "processing-actor", IsActive = true }); db.SaveChanges(); }
        _enrichment.SummarizeAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>()).Returns("generated summary");
        _enrichment.ExtractTagsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>()).Returns(new List<string>());
        _ai.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([1f, 0f, 0f]);
        var services = new ServiceCollection();
        services.AddScoped(_ => Open()); services.AddSingleton(_enrichment); services.AddSingleton(_ai); services.AddSingleton(_search);
        services.AddScoped<ISelfHostedChunkingService, SelfHostedChunkingService>();
        _services = services.BuildServiceProvider();
    }
    private SelfHostedDbContext Open() => new(_options, _tenantProvider, new Actor(_actor));
    private async Task<Guid> SeedAsync(string state = "plain")
    {
        await using var db = Open(); using var import = db.BeginInactiveRecoveryImport();
        var id = Guid.NewGuid();
        db.KnowledgeItems.Add(new Knowledge { Id = id, TenantId = _tenant, CreatedByUserId = _actor, Title = "Stale protected title", Content = "stale-ciphertext-token", Summary = "old protected summary", IsIndexed = true,
            PortableRecoveryState = state == "inactive" ? "suspended-release" : null });
        if (state == "sealed") db.ShSealedKnowledge.Add(new() { Id = Guid.NewGuid(), TenantId = _tenant, KnowledgeId = id, OriginalItemId = id, Ciphertext = "actual-ciphertext" });
        db.ContentChunks.Add(new() { TenantId = _tenant, KnowledgeId = id, Content = "old plaintext chunk", ContextSummary = "old confidential context", ContentHash = "old", EmbeddingVectorJson = "[1,0,0]" });
        await db.SaveChangesAsync(); return id;
    }
    private SearchFacade Facade(SelfHostedDbContext db) => new(db, _search, _ai, _streaming, NullLogger<SearchFacade>.Instance);
    private EnrichmentBackgroundService Worker() => new(_channel, _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<EnrichmentBackgroundService>.Instance);

    [Theory]
    [InlineData("sealed")]
    [InlineData("inactive")]
    public async Task QueuedProtectedKnowledgeCannotReachAiOrIndexAfterRestart(string state)
    {
        var id = await SeedAsync(state);
        await using (var db = Open())
        {
            db.EnrichmentOutbox.Add(new() { KnowledgeId = id, TenantId = _tenant, Status = EnrichmentStatus.Processing });
            db.EnrichmentOutbox.Add(new() { KnowledgeId = id, TenantId = _otherTenant, Status = EnrichmentStatus.Pending });
            await db.SaveChangesAsync();
        }
        await Worker().ProcessWorkItemAsync(new(id, _tenant), CancellationToken.None);
        await _enrichment.DidNotReceive().SummarizeAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>());
        await _ai.DidNotReceive().GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain(_search.ReceivedCalls(), c => c.GetMethodInfo().Name == "IndexDocumentAsync");
        await using var check = Open();
        Assert.Empty(await check.ContentChunks.Where(c => c.KnowledgeId == id).ToListAsync());
        Assert.Equal(EnrichmentStatus.Failed, (await check.EnrichmentOutbox.SingleAsync(e => e.TenantId == _tenant)).Status);
        Assert.Equal(EnrichmentStatus.Pending, (await check.EnrichmentOutbox.SingleAsync(e => e.TenantId == _otherTenant)).Status);
    }

    [Fact]
    public async Task ProtectionAppearingDuringSummaryStopsLaterCallsAndIndexing()
    {
        var id = await SeedAsync();
        _enrichment.SummarizeAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>()).Returns(async _ =>
        {
            await using var db = Open(); db.ShSealedKnowledge.Add(new() { Id = Guid.NewGuid(), TenantId = _tenant, KnowledgeId = id, OriginalItemId = id, Ciphertext = "sealed-during-call" }); await db.SaveChangesAsync(); return "must not be persisted";
        });
        await Worker().ProcessWorkItemAsync(new(id, _tenant), CancellationToken.None);
        await _enrichment.DidNotReceive().GenerateBriefSummaryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>());
        await _enrichment.DidNotReceive().ExtractTagsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>());
        Assert.DoesNotContain(_search.ReceivedCalls(), c => c.GetMethodInfo().Name == "IndexDocumentAsync");
    }

    [Theory]
    [InlineData("sealed")]
    [InlineData("inactive")]
    public async Task DirectReindexRejectsProtectedRowsAndClearsStaleChunks(string state)
    {
        var id = await SeedAsync(state); await using var db = Open(); var row = await db.KnowledgeItems.SingleAsync(k => k.Id == id);
        await EnrichmentBackgroundService.ReindexAsync(db, row, _search, _ai, new SelfHostedChunkingService(), _enrichment, CancellationToken.None);
        await _ai.DidNotReceive().GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain(_search.ReceivedCalls(), c => c.GetMethodInfo().Name == "IndexDocumentAsync");
        Assert.Empty(await db.ContentChunks.ToListAsync());
    }

    [Theory]
    [InlineData("sealed")]
    [InlineData("inactive")]
    public async Task ManualOutboxResetCannotQueueProtectedOrResetForeignTenantRows(string state)
    {
        var id = await SeedAsync(state); await using var db = Open();
        db.EnrichmentOutbox.Add(new() { KnowledgeId = id, TenantId = _otherTenant, Status = EnrichmentStatus.Processing }); await db.SaveChangesAsync();
        var writer = new EnrichmentOutboxWriter(db, _channel, NullLogger<EnrichmentOutboxWriter>.Instance);
        await writer.EnqueueAsync(id, _tenant, true);
        Assert.False(_channel.Reader.TryRead(out _)); Assert.Equal(EnrichmentStatus.Processing, (await db.EnrichmentOutbox.SingleAsync()).Status);
    }

    [Fact]
    public async Task OutboxDeduplicationDoesNotAdoptAnotherTenantsSameKnowledgeId()
    {
        var id = await SeedAsync(); await using var db = Open();
        db.EnrichmentOutbox.Add(new() { KnowledgeId = id, TenantId = _otherTenant, Status = EnrichmentStatus.Processing }); await db.SaveChangesAsync();
        await new EnrichmentOutboxWriter(db, _channel, NullLogger<EnrichmentOutboxWriter>.Instance).EnqueueAsync(id, _tenant, true);
        Assert.Equal(2, await db.EnrichmentOutbox.CountAsync()); Assert.Equal(EnrichmentStatus.Processing, (await db.EnrichmentOutbox.SingleAsync(e => e.TenantId == _otherTenant)).Status);
    }

    [Theory]
    [InlineData("sealed")]
    [InlineData("inactive")]
    public async Task CachedExternalSearchAndDirectAiContextExcludeProtectedKnowledge(string state)
    {
        var id = await SeedAsync(state); await using var db = Open();
        _search.HybridSearchAsync(Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new() { KnowledgeId = id, Title = "cached protected title", Content = "cached old plaintext", Score = 1 }]);
        Assert.Empty((await Facade(db).SearchKnowledgeAsync("token", 10, null, false, [], false, null, null, null, default)).Items);
        Assert.Empty(await Facade(db).BuildKnowledgeScopedContextAsync(id, "context", default));
    }

    [Fact]
    public async Task CachedForeignTenantResultCannotEnterCurrentTenantSearch()
    {
        await using var db = Open(); var id = Guid.NewGuid(); db.KnowledgeItems.Add(new() { Id = id, TenantId = _otherTenant, Title = "foreign secret", Content = "foreign plaintext" }); await db.SaveChangesAsync();
        _search.HybridSearchAsync(Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new() { KnowledgeId = id, Content = "foreign plaintext", Score = 1 }]);
        Assert.Empty((await Facade(db).SearchKnowledgeAsync("secret", 10, null, false, [], false, null, null, null, default)).Items);
    }

    [Fact]
    public async Task ActorRevokedDuringExternalSearchCannotReceiveCachedPlaintext()
    {
        var id = await SeedAsync(); await using var db = Open();
        _search.HybridSearchAsync(Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async _ => { await using var changed = Open(); (await changed.Users.SingleAsync()).IsActive = false; await changed.SaveChangesAsync(); return new List<SearchResultItem> { new() { KnowledgeId = id, Content = "plain private result", Score = 1 } }; });
        Assert.Empty((await Facade(db).SearchKnowledgeAsync("private", 10, null, false, [], false, null, null, null, default)).Items);
    }

    [Fact]
    public async Task TenantSuspendedDuringSearchCannotReceiveCachedPlaintext()
    {
        var id = await SeedAsync(); await using var db = Open(); db.Tenants.Add(new() { Id = _tenant, Name = "owned fixture", Slug = "owned-fixture" }); await db.SaveChangesAsync();
        _search.HybridSearchAsync(Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async _ => { await using var changed = Open(); (await changed.Tenants.SingleAsync()).IsActive = false; await changed.SaveChangesAsync(); return new List<SearchResultItem> { new() { KnowledgeId = id, Content = "tenant private result", Score = 1 } }; });
        Assert.Empty((await Facade(db).SearchKnowledgeAsync("private", 10, null, false, [], false, null, null, null, default)).Items);
    }

    [Fact]
    public async Task SuspendedTenantQueueDoesNotInvokeAi()
    {
        var id = await SeedAsync(); await using (var db = Open()) { db.Tenants.Add(new() { Id = _tenant, Name = "suspended", Slug = "suspended", IsActive = false }); db.EnrichmentOutbox.Add(new() { TenantId = _tenant, KnowledgeId = id }); await db.SaveChangesAsync(); }
        await Worker().ProcessWorkItemAsync(new(id, _tenant), default);
        await _enrichment.DidNotReceive().SummarizeAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>());
        await _ai.DidNotReceive().GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PermissionRevokedDuringSearchOverridesEarlierAccessibleVaultSnapshot()
    {
        var id = await SeedAsync(); await using var db = Open(); var vault = Guid.NewGuid(); db.Vaults.Add(new() { Id = vault, TenantId = _tenant, Name = "restricted" });
        db.KnowledgeVaults.Add(new() { KnowledgeId = id, VaultId = vault, TenantId = _tenant }); db.UserPermissions.Add(new() { UserId = _actor, TenantId = _tenant, HasAllVaultsAccess = true }); await db.SaveChangesAsync();
        _search.HybridSearchAsync(Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async _ => { await using var changed = Open(); (await changed.UserPermissions.SingleAsync()).HasAllVaultsAccess = false; await changed.SaveChangesAsync(); return new List<SearchResultItem> { new() { KnowledgeId = id, Content = "previously authorized result", Score = 1 } }; });
        Assert.Empty((await Facade(db).SearchKnowledgeAsync("result", 10, vault, false, [], false, null, null, null, default, [vault])).Items);
    }

    [Fact]
    public async Task FailedExternalCleanupRetriesOnlyDeletionWithoutCallingAi()
    {
        var id = await SeedAsync("sealed"); await using (var db = Open()) { db.EnrichmentOutbox.Add(new() { TenantId = _tenant, KnowledgeId = id }); await db.SaveChangesAsync(); }
        var attempts = 0; _search.DeleteDocumentAsync(id, Arg.Any<CancellationToken>()).Returns(_ => ++attempts == 1 ? Task.FromException(new IOException("synthetic index unavailable")) : Task.CompletedTask);
        await Worker().ProcessWorkItemAsync(new(id, _tenant), default);
        await using (var check = Open()) Assert.Equal(EnrichmentStatus.Pending, (await check.EnrichmentOutbox.SingleAsync()).Status);
        await Worker().ProcessWorkItemAsync(new(id, _tenant), default);
        Assert.Equal(2, attempts); await _ai.DidNotReceive().GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await using var final = Open(); Assert.Equal(EnrichmentStatus.Failed, (await final.EnrichmentOutbox.SingleAsync()).Status);
    }

    [Fact]
    public async Task InactiveCleanupDoesNotPermitAddingOrChangingContentChunks()
    {
        var id = await SeedAsync("inactive"); await using var db = Open(); var chunk = await db.ContentChunks.IgnoreQueryFilters().SingleAsync(c => c.TenantId == _tenant && c.KnowledgeId == id); chunk.Content = "must not become a new inactive projection";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task LocalVectorSearchCannotMatchLockedCiphertextOrStaleChunk()
    {
        var id = await SeedAsync("sealed"); await using var db = Open();
        var search = new LocalVectorSearchService(db, _tenantProvider, NullLogger<LocalVectorSearchService>.Instance);
        Assert.Empty(await search.HybridSearchAsync("stale-ciphertext-token", [1f, 0f, 0f]));
    }

    [Fact]
    public async Task AttachmentContextDropsSealedCommentAndBinaryButRetainsPlainContentWithProtectedFilename()
    {
        var id = await SeedAsync(); await using var db = Open(); var comment = Guid.NewGuid(); var binary = Guid.NewGuid(); var plain = Guid.NewGuid();
        db.Comments.Add(new() { Id = comment, TenantId = _tenant, KnowledgeId = id, Body = "sealed-comment-token", AuthorName = "Alice" });
        db.ShSealedComments.Add(new() { Id = comment, TenantId = _tenant, KnowledgeId = id, OriginalCommentId = comment, Ciphertext = "sealed-comment-body" });
        db.FileRecords.Add(new() { Id = binary, TenantId = _tenant, FileName = "sealed-binary.pdf", ExtractedText = "sealed-binary-token", ContentType = "application/pdf" });
        db.ShSealedFiles.Add(new() { Id = binary, TenantId = _tenant, OriginalFileId = binary, BinaryContentBase64 = "sealed" });
        db.FileRecords.Add(new() { Id = plain, TenantId = _tenant, FileName = "must-not-leak-secret-name.txt", EncryptedFieldsJson = "{\"version\":1,\"fields\":{\"fileName\":{}}}", ExtractedText = "allowed plain file content", ContentType = "text/plain" });
        db.FileAttachments.AddRange(new() { TenantId = _tenant, FileRecordId = binary, KnowledgeId = id }, new() { TenantId = _tenant, FileRecordId = plain, KnowledgeId = id }); await db.SaveChangesAsync();
        var text = await EnrichmentBackgroundService.GetAllAttachmentTextAsync(db, id, default);
        Assert.DoesNotContain("sealed-comment-token", text); Assert.DoesNotContain("sealed-binary-token", text); Assert.DoesNotContain("must-not-leak-secret-name", text);
        Assert.Contains("allowed plain file content", text); Assert.Contains("encrypted-file", text);
    }

    [Fact]
    public async Task OrdinaryFileAttachmentCannotSendTypedProtectedBinaryToExtractor()
    {
        var id = await SeedAsync(); await using var db = Open(); var file = new FileRecord { TenantId = _tenant, FileName = "cipher.pdf", ContentType = "application/pdf" };
        db.FileRecords.Add(file); db.ShSealedFiles.Add(new() { Id = file.Id, TenantId = _tenant, OriginalFileId = file.Id, BinaryContentBase64 = "protected binary" }); await db.SaveChangesAsync();
        var storage = Substitute.For<IFileStorageProvider>(); var extractor = Substitute.For<IFileContentExtractor>(); extractor.CanExtract(Arg.Any<string>()).Returns(true);
        var service = new FileStorageService(storage, new SelfHostedRepository<FileRecord>(db), db, _tenantProvider, NullLogger<FileStorageService>.Instance, extractor);
        await Assert.ThrowsAsync<SealedItemLockedException>(() => service.AttachToKnowledgeAsync(file.Id, id));
        await extractor.DidNotReceive().ExtractAsync(Arg.Any<FileRecord>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        Assert.Empty(await db.FileAttachments.ToListAsync());
    }

    [Fact]
    public async Task PlainBinaryExtractionKeepsContentButNeverExposesProtectedFilename()
    {
        var id = await SeedAsync(); await using var db = Open(); var file = new FileRecord { TenantId = _tenant, FileName = "private-file-name.pdf", ContentType = "application/pdf", EncryptedFieldsJson = "{\"version\":1,\"fields\":{\"fileName\":{}}}" };
        db.FileRecords.Add(file); await db.SaveChangesAsync(); var storage = Substitute.For<IFileStorageProvider>();
        storage.DownloadAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => ((Stream)new MemoryStream([1,2,3]), "application/pdf", "encrypted-file"));
        var extractor = Substitute.For<IFileContentExtractor>(); extractor.CanExtract(Arg.Any<string>()).Returns(true);
        extractor.ExtractAsync(Arg.Any<FileRecord>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(call => { var record = call.ArgAt<FileRecord>(0); Assert.Equal("encrypted-file", record.FileName); Assert.Null(record.EncryptedFieldsJson); return new FileExtractionResult(true, "allowed ordinary content"); });
        var service = new FileStorageService(storage, new SelfHostedRepository<FileRecord>(db), db, _tenantProvider, NullLogger<FileStorageService>.Instance, extractor);
        await service.AttachToKnowledgeAsync(file.Id, id);
        Assert.Equal("allowed ordinary content", (await db.FileRecords.SingleAsync()).ExtractedText);
        Assert.Equal("private-file-name.pdf", (await db.FileRecords.SingleAsync()).FileName);
    }

    public void Dispose() => _services.Dispose();
}
