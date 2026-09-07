using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// Tests for the knowledge version history feature: initial version on create,
/// post-update snapshot timing, and meaningful change descriptions.
/// </summary>
public class VersioningTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly KnowledgeService _knowledgeSvc;
    private readonly VersioningService _versioningSvc;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public VersioningTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);

        var search = Substitute.For<ISearchService>();
        var openAI = Substitute.For<IOpenAIService>();
        openAI.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f, 0.2f, 0.3f });

        var versioningLogger = Substitute.For<ILogger<VersioningService>>();
        _versioningSvc = new VersioningService(_db, tenantProvider, versioningLogger);

        var knowledgeRepo = new SelfHostedRepository<Knowledge>(_db);
        var tagRepo = new SelfHostedRepository<Tag>(_db);
        var chunking = new SelfHostedChunkingService();
        var ksLogger = Substitute.For<ILogger<KnowledgeService>>();

        _knowledgeSvc = new KnowledgeService(
            knowledgeRepo, tagRepo, _db, search, openAI, chunking, tenantProvider, ksLogger,
            enrichmentWriter: null,
            versioningService: _versioningSvc);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // --- Initial version on create ---

    [Fact]
    public async Task CreateKnowledgeAsync_CreatesInitialVersion1()
    {
        var result = await _knowledgeSvc.CreateKnowledgeAsync(
            "original content", "Original Title", "Note", null,
            new List<string>(), null, CancellationToken.None);

        Assert.NotNull(result);
        var versions = await _versioningSvc.GetVersionsAsync(result.Id, CancellationToken.None);
        Assert.Single(versions);
        Assert.Equal(1, versions[0].VersionNumber);
        Assert.Equal("Original Title", versions[0].Title);
        Assert.Equal("original content", versions[0].Content);
        Assert.Equal("Initial version", versions[0].ChangeDescription);
    }

    // --- Post-update snapshot reflects NEW state, not pre-edit state ---

    [Fact]
    public async Task UpdateKnowledgeAsync_CreatesVersion2WithNewContent()
    {
        var create = await _knowledgeSvc.CreateKnowledgeAsync(
            "v1 content", "v1 title", "Note", null,
            new List<string>(), null, CancellationToken.None);

        await _knowledgeSvc.UpdateKnowledgeAsync(
            create!.Id, "v2 title", "v2 content", null, null, null, CancellationToken.None);

        var versions = await _versioningSvc.GetVersionsAsync(create.Id, CancellationToken.None);
        Assert.Equal(2, versions.Count);

        // Latest version (sorted desc by GetVersionsAsync) reflects the NEW state.
        var latest = versions[0];
        Assert.Equal(2, latest.VersionNumber);
        Assert.Equal("v2 title", latest.Title);
        Assert.Equal("v2 content", latest.Content);

        // Version 1 is still the original.
        var initial = versions[1];
        Assert.Equal(1, initial.VersionNumber);
        Assert.Equal("v1 title", initial.Title);
        Assert.Equal("v1 content", initial.Content);
    }

    // --- Change description summarises what actually changed ---

    [Fact]
    public void BuildChangeDescription_TitleOnly_ReportsTitle()
    {
        var desc = KnowledgeService.BuildChangeDescription(
            titleChanged: true, contentChanged: false, tagsChanged: false, vaultChanged: false,
            originalContent: "abc", newContent: "abc");

        Assert.Equal("Updated title", desc);
    }

    [Fact]
    public void BuildChangeDescription_ContentOnly_ReportsContentDelta()
    {
        var desc = KnowledgeService.BuildChangeDescription(
            titleChanged: false, contentChanged: true, tagsChanged: false, vaultChanged: false,
            originalContent: "hello", newContent: "hello world");

        Assert.Equal("Updated content (+6 chars)", desc);
    }

    [Fact]
    public void BuildChangeDescription_ContentShrunk_ReportsNegativeDelta()
    {
        var desc = KnowledgeService.BuildChangeDescription(
            titleChanged: false, contentChanged: true, tagsChanged: false, vaultChanged: false,
            originalContent: "hello world", newContent: "hi");

        Assert.Equal("Updated content (-9 chars)", desc);
    }

    [Fact]
    public void BuildChangeDescription_TitleAndContent_ReportsBoth()
    {
        var desc = KnowledgeService.BuildChangeDescription(
            titleChanged: true, contentChanged: true, tagsChanged: false, vaultChanged: false,
            originalContent: "old", newContent: "new content");

        Assert.Equal("Updated title, content (+8 chars)", desc);
    }

    [Fact]
    public void BuildChangeDescription_AllChanged_ReportsAll()
    {
        var desc = KnowledgeService.BuildChangeDescription(
            titleChanged: true, contentChanged: true, tagsChanged: true, vaultChanged: true,
            originalContent: "a", newContent: "ab");

        Assert.Equal("Updated title, content (+1 chars), tags, vault", desc);
    }

    [Fact]
    public void BuildChangeDescription_NothingChanged_ReportsGeneric()
    {
        var desc = KnowledgeService.BuildChangeDescription(
            titleChanged: false, contentChanged: false, tagsChanged: false, vaultChanged: false,
            originalContent: "x", newContent: "x");

        Assert.Equal("Updated", desc);
    }

    // --- End-to-end: meaningful descriptions reach the version row ---

    [Fact]
    public async Task UpdateKnowledgeAsync_TitleEdit_PersistsMeaningfulDescription()
    {
        var create = await _knowledgeSvc.CreateKnowledgeAsync(
            "content", "Original", "Note", null,
            new List<string>(), null, CancellationToken.None);

        await _knowledgeSvc.UpdateKnowledgeAsync(
            create!.Id, "Renamed", null, null, null, null, CancellationToken.None);

        var versions = await _versioningSvc.GetVersionsAsync(create.Id, CancellationToken.None);
        var latest = versions[0]; // sorted desc
        Assert.Equal("Updated title", latest.ChangeDescription);
    }
}
