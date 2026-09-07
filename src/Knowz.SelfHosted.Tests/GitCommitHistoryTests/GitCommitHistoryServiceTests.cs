using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.Services.GitCommitHistory;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Knowz.SelfHosted.Tests.GitCommitHistoryTests;

/// <summary>
/// Unit tests for the selfhosted <see cref="GitCommitHistoryService"/>. Mirrors the
/// verification criteria from <c>SVC_CommitHistoryIngestion.md</c>, adapted for the
/// in-process (no Service Bus) execution model.
///
/// WorkGroupID: kc-feat-git-commit-knowledge-sync-20260410
/// NodeID: SelfHostedCommitHistoryParity (NODE-4)
/// </summary>
public class GitCommitHistoryServiceTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly ICommitElaborationLlmClient _llm;
    private readonly GitCommitHistoryService _svc;

    public GitCommitHistoryServiceTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _tenantProvider = Substitute.For<ITenantProvider>();
        _tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, _tenantProvider);

        _llm = Substitute.For<ICommitElaborationLlmClient>();
        _llm.IsAvailable.Returns(true);
        _llm.ElaborateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("AI-elaborated description of the commit.");

        _svc = new GitCommitHistoryService(
            _db,
            _tenantProvider,
            new CommitSecretScanner(),
            new CommitElaborationPromptBuilder(new CommitSecretScanner(), NullLogger<CommitElaborationPromptBuilder>.Instance),
            _llm,
            NullLogger<GitCommitHistoryService>.Instance);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private GitRepository SeedRepoWithTracking(
        bool trackCommitHistory = true,
        int? commitHistoryDepth = null)
    {
        var vault = new Vault { Id = Guid.NewGuid(), TenantId = TenantId, Name = "v" };
        _db.Vaults.Add(vault);

        var repo = new GitRepository
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            VaultId = vault.Id,
            RepositoryUrl = "https://git.example.com/org/repo.git",
            Branch = "main",
            Status = "Synced",
            TrackCommitHistory = trackCommitHistory,
            CommitHistoryDepth = commitHistoryDepth
        };
        _db.GitRepositories.Add(repo);
        _db.SaveChanges();
        return repo;
    }

    private static CommitDescriptor MakeCommit(
        string sha,
        string msg = "fix: bug",
        params CommitChangedFile[] files) =>
        new(
            Sha: sha,
            ParentShas: Array.Empty<string>(),
            AuthorName: "Alice",
            AuthorEmail: "alice@example.com",
            AuthoredAt: DateTimeOffset.UtcNow,
            CommittedAt: DateTimeOffset.UtcNow,
            Message: msg,
            ChangedFiles: files.Length == 0
                ? new List<CommitChangedFile> { new("src/Foo.cs", 1, 0, GitCommitChangeType.Modified) }
                : files.ToList());

    // ─── Feature flag default OFF (VERIFY #5) ────────────────────────────────

    [Fact]
    public async Task ProcessCommitsAsync_FeatureFlagOff_DoesNotCreateAnyRow()
    {
        var repo = SeedRepoWithTracking(trackCommitHistory: false);
        var commits = new[] { MakeCommit("abc1234") };

        var result = await _svc.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(_db.KnowledgeItems.IgnoreQueryFilters().ToList());
        await _llm.DidNotReceive().ElaborateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ─── Parent + per-commit-child topology (VERIFY #1) ──────────────────────

    [Fact]
    public async Task ProcessCommitsAsync_CreatesParentAndChildKnowledge()
    {
        var repo = SeedRepoWithTracking();
        var commits = new[]
        {
            MakeCommit("abc1234", "feat: add feature"),
            MakeCommit("def5678", "fix: bug")
        };

        await _svc.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);

        var all = _db.KnowledgeItems.IgnoreQueryFilters().ToList();
        Assert.Equal(3, all.Count);
        Assert.Single(all, k => k.Type == KnowledgeType.CommitHistory);
        Assert.Equal(2, all.Count(k => k.Type == KnowledgeType.Commit));
    }

    [Fact]
    public async Task ProcessCommitsAsync_WritesPartOfRelationshipForEachChild()
    {
        var repo = SeedRepoWithTracking();
        var commits = new[] { MakeCommit("abc1234") };

        await _svc.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);

        var rels = _db.KnowledgeRelationships.IgnoreQueryFilters().ToList();
        Assert.Single(rels);
        Assert.Equal(KnowledgeRelationshipType.PartOf, rels[0].RelationshipType);
    }

    // ─── Idempotency (VERIFY #2, #3) ─────────────────────────────────────────

    [Fact]
    public async Task ProcessCommitsAsync_RunTwice_ProducesNoDuplicateRows()
    {
        var repo = SeedRepoWithTracking();
        var commits = new[] { MakeCommit("abc1234"), MakeCommit("def5678") };

        await _svc.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);
        await _svc.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);

        var children = _db.KnowledgeItems
            .IgnoreQueryFilters()
            .Where(k => k.Type == KnowledgeType.Commit)
            .ToList();
        var parents = _db.KnowledgeItems
            .IgnoreQueryFilters()
            .Where(k => k.Type == KnowledgeType.CommitHistory)
            .ToList();
        var rels = _db.KnowledgeRelationships.IgnoreQueryFilters().ToList();

        Assert.Equal(2, children.Count);
        Assert.Single(parents);
        Assert.Equal(2, rels.Count); // one PartOf per child, no duplicates
    }

    // ─── Depth cap enforcement (VERIFY #6) ───────────────────────────────────

    [Fact]
    public async Task ProcessCommitsAsync_TruncatesToCommitHistoryDepth()
    {
        var repo = SeedRepoWithTracking(commitHistoryDepth: 3);
        // 5 commits supplied, but depth=3 clamps it
        var commits = Enumerable.Range(0, 5)
            .Select(i => MakeCommit($"sha{i:0000000}"))
            .ToList();

        await _svc.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);

        var children = _db.KnowledgeItems
            .IgnoreQueryFilters()
            .Where(k => k.Type == KnowledgeType.Commit)
            .ToList();
        Assert.Equal(3, children.Count);
    }

    [Fact]
    public async Task ProcessCommitsAsync_DepthExceedsCeiling_ClampsToMaxDepth()
    {
        var repo = SeedRepoWithTracking(commitHistoryDepth: 9999); // way over ceiling 2000
        var commits = Enumerable.Range(0, 2500)
            .Select(i => MakeCommit($"s{i:0000000}"))
            .ToList();

        await _svc.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);

        var childCount = _db.KnowledgeItems
            .IgnoreQueryFilters()
            .Count(k => k.Type == KnowledgeType.Commit);
        Assert.Equal(GitCommitHistoryService.MaxCommitHistoryDepth, childCount);
    }

    // ─── CRIT-2: sensitive-file deny list (VERIFY #10) ───────────────────────

    [Fact]
    public async Task ProcessCommitsAsync_SensitiveFileCommit_CreatesStubWithoutLlmCall()
    {
        var repo = SeedRepoWithTracking();
        var commits = new[]
        {
            MakeCommit("sha1111", "touch .env",
                new CommitChangedFile(".env", 2, 0, GitCommitChangeType.Modified))
        };

        await _svc.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);

        var child = _db.KnowledgeItems
            .IgnoreQueryFilters()
            .Single(k => k.Type == KnowledgeType.Commit);
        Assert.Contains(".env", child.Content);
        Assert.Contains("sensitive file pattern", child.Content);
        Assert.Contains("\"elaborationSkipped\":\"sensitive-file\"", child.PlatformData ?? string.Empty);
        await _llm.DidNotReceive().ElaborateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ─── CRIT-4: NoOp fallback (VERIFY #14) ──────────────────────────────────

    [Fact]
    public async Task ProcessCommitsAsync_LlmUnavailable_CreatesStubsNoLlmCall()
    {
        _llm.IsAvailable.Returns(false);
        var repo = SeedRepoWithTracking();
        var commits = new[] { MakeCommit("sha2222") };

        await _svc.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);

        var child = _db.KnowledgeItems
            .IgnoreQueryFilters()
            .Single(k => k.Type == KnowledgeType.Commit);
        Assert.Contains("\"elaborationSkipped\":\"platform-ai-unavailable\"", child.PlatformData ?? string.Empty);
        await _llm.DidNotReceive().ElaborateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ─── Parent rolling-window markers (VERIFY #7) ────────────────────────────

    [Fact]
    public async Task ProcessCommitsAsync_ParentContainsDelimitedMarkers()
    {
        var repo = SeedRepoWithTracking();
        var commits = new[] { MakeCommit("abc1234567"), MakeCommit("def0011223") };

        await _svc.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);

        var parent = _db.KnowledgeItems
            .IgnoreQueryFilters()
            .Single(k => k.Type == KnowledgeType.CommitHistory);
        Assert.Contains("<!-- commit:abc1234567 -->", parent.Content);
        Assert.Contains("<!-- /commit:abc1234567 -->", parent.Content);
        Assert.Contains("<!-- commit:def0011223 -->", parent.Content);
    }

    // ─── LastCommitHistorySyncSha return value (VERIFY #13) ───────────────────

    [Fact]
    public async Task ProcessCommitsAsync_ReturnsNewestSha_OnSuccess()
    {
        var repo = SeedRepoWithTracking();
        var newest = MakeCommit("abc1234567");
        var older = MakeCommit("def0011223");
        // Walker returns newest-first
        var commits = new[] { newest, older };

        var result = await _svc.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);

        Assert.Equal("abc1234567", result);
    }

    [Fact]
    public async Task ProcessCommitsAsync_AllAlreadyExist_ReturnsNull()
    {
        var repo = SeedRepoWithTracking();
        var commits = new[] { MakeCommit("abc1234567") };

        await _svc.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);
        var result = await _svc.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);

        Assert.Null(result);
    }
}
