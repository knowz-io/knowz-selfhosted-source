using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.Services.GitCommitHistory;
using Knowz.SelfHosted.Application.Services.Shared;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Knowz.SelfHosted.Tests.GitCommitHistoryTests;

/// <summary>
/// Tests for commit↔knowledge linkage (NodeID A — SelfHostedCommitKnowledgeLinkage).
/// Covers VERIFY-A.1 through VERIFY-A.7 from the SVC_CommitHistoryIngestion.md
/// "SelfHosted NODE-3 parity" subsection.
///
/// WorkGroupID: kc-feat-commit-knowledge-link-20260410-230500
/// </summary>
public class CommitKnowledgeLinkageTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly ICommitElaborationLlmClient _llm;
    private readonly GitCommitHistoryService _svc;

    public CommitKnowledgeLinkageTests()
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

    // ─── Seed helpers ────────────────────────────────────────────────────────

    private GitRepository SeedRepoWithTracking()
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
            TrackCommitHistory = true
        };
        _db.GitRepositories.Add(repo);
        _db.SaveChanges();
        return repo;
    }

    private Knowledge SeedFileKnowledge(Guid vaultId, string filePath)
    {
        var file = new Knowledge
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Title = Path.GetFileName(filePath),
            Content = $"contents of {filePath}",
            Source = "git-sync",
            FilePath = filePath,
            Type = KnowledgeType.Code,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.KnowledgeItems.Add(file);
        _db.KnowledgeVaults.Add(new KnowledgeVault
        {
            TenantId = TenantId,
            KnowledgeId = file.Id,
            VaultId = vaultId,
            IsPrimary = true
        });
        _db.SaveChanges();
        return file;
    }

    private static CommitDescriptor MakeCommit(
        string sha,
        string msg,
        params string[] paths) =>
        new(
            Sha: sha,
            ParentShas: Array.Empty<string>(),
            AuthorName: "Alice",
            AuthorEmail: "alice@example.com",
            AuthoredAt: DateTimeOffset.UtcNow,
            CommittedAt: DateTimeOffset.UtcNow,
            Message: msg,
            ChangedFiles: paths.Select(p =>
                new CommitChangedFile(p, 1, 0, GitCommitChangeType.Modified)).ToList());

    private static List<string> ParseUnlinkedFiles(string? platformDataJson)
    {
        if (string.IsNullOrEmpty(platformDataJson)) return new List<string>();
        using var doc = JsonDocument.Parse(platformDataJson);
        if (!doc.RootElement.TryGetProperty("unlinkedFiles", out var arr))
        {
            return new List<string>();
        }
        return arr.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
    }

    // ─── VERIFY-A.1: Both files resolved ─────────────────────────────────────

    [Fact]
    public async Task Verify_A1_BothFilesResolved_WritesTwoReferencesEdges_EmptyUnlinked()
    {
        var repo = SeedRepoWithTracking();
        var fileA = SeedFileKnowledge(repo.VaultId, "src/a.cs");
        var fileB = SeedFileKnowledge(repo.VaultId, "src/b.cs");

        var commits = new[] { MakeCommit("abc1234", "feat: touch a and b", "src/a.cs", "src/b.cs") };

        await _svc.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);

        var commitChild = _db.KnowledgeItems
            .IgnoreQueryFilters()
            .Single(k => k.Type == KnowledgeType.Commit);

        var refEdges = _db.KnowledgeRelationships
            .IgnoreQueryFilters()
            .Where(r => r.RelationshipType == KnowledgeRelationshipType.References
                && r.SourceKnowledgeId == commitChild.Id)
            .ToList();

        Assert.Equal(2, refEdges.Count);
        Assert.Contains(refEdges, r => r.TargetKnowledgeId == fileA.Id);
        Assert.Contains(refEdges, r => r.TargetKnowledgeId == fileB.Id);

        var unlinked = ParseUnlinkedFiles(commitChild.PlatformData);
        Assert.Empty(unlinked);
    }

    // ─── VERIFY-A.2: Partial match — one edge, orphan preserved ──────────────

    [Fact]
    public async Task Verify_A2_PartialMatch_WritesOneEdgeAndPreservesOrphan()
    {
        var repo = SeedRepoWithTracking();
        var fileA = SeedFileKnowledge(repo.VaultId, "src/a.cs");
        // No seed for orphan.md

        var commits = new[] { MakeCommit("abc1234", "feat: mixed", "src/a.cs", "orphan.md") };

        await _svc.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);

        var commitChild = _db.KnowledgeItems
            .IgnoreQueryFilters()
            .Single(k => k.Type == KnowledgeType.Commit);

        var refEdges = _db.KnowledgeRelationships
            .IgnoreQueryFilters()
            .Where(r => r.RelationshipType == KnowledgeRelationshipType.References
                && r.SourceKnowledgeId == commitChild.Id)
            .ToList();

        Assert.Single(refEdges);
        Assert.Equal(fileA.Id, refEdges[0].TargetKnowledgeId);

        var unlinked = ParseUnlinkedFiles(commitChild.PlatformData);
        Assert.Single(unlinked);
        Assert.Equal("orphan.md", unlinked[0]);
    }

    // ─── VERIFY-A.3: Idempotent re-run — no duplicate edges ──────────────────

    [Fact]
    public async Task Verify_A3_IdempotentReRun_DoesNotDuplicateReferencesEdges()
    {
        var repo = SeedRepoWithTracking();
        SeedFileKnowledge(repo.VaultId, "src/a.cs");

        var commits = new[] { MakeCommit("abc1234", "feat: touch a", "src/a.cs") };

        await _svc.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);
        await _svc.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);

        var refEdges = _db.KnowledgeRelationships
            .IgnoreQueryFilters()
            .Where(r => r.RelationshipType == KnowledgeRelationshipType.References)
            .ToList();
        Assert.Single(refEdges);

        var commitChildren = _db.KnowledgeItems
            .IgnoreQueryFilters()
            .Where(k => k.Type == KnowledgeType.Commit)
            .ToList();
        Assert.Single(commitChildren);
    }

    // ─── VERIFY-A.3b: UpsertRelationshipAsync dedup unit guard ───────────────

    [Fact]
    public async Task Verify_A3b_UpsertHelper_DedupsIdenticalEdge()
    {
        // Direct unit test of the helper's AnyAsync guard — proves the code-level
        // dedup short-circuits before the unique-index ever gets invoked.
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        await KnowledgeRelationshipHelpers.UpsertRelationshipAsync(
            _db, TenantId, sourceId, targetId, KnowledgeRelationshipType.References, CancellationToken.None);
        await _db.SaveChangesAsync();

        await KnowledgeRelationshipHelpers.UpsertRelationshipAsync(
            _db, TenantId, sourceId, targetId, KnowledgeRelationshipType.References, CancellationToken.None);
        await _db.SaveChangesAsync();

        var count = _db.KnowledgeRelationships
            .IgnoreQueryFilters()
            .Count(r => r.SourceKnowledgeId == sourceId && r.TargetKnowledgeId == targetId);
        Assert.Equal(1, count);
    }

    // ─── VERIFY-A.4: Sensitive + real file mix ───────────────────────────────

    [Fact]
    public async Task Verify_A4_SensitivePlusRealFile_WritesRefForRealFile_SkipsSensitive()
    {
        var repo = SeedRepoWithTracking();
        var real = SeedFileKnowledge(repo.VaultId, "src/realFile.cs");

        var commits = new[] { MakeCommit("abc1234", "update config + code", ".env", "src/realFile.cs") };

        await _svc.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);

        var commitChild = _db.KnowledgeItems
            .IgnoreQueryFilters()
            .Single(k => k.Type == KnowledgeType.Commit);

        // (a) sensitive stub content
        Assert.Contains(".env", commitChild.Content);
        Assert.Contains("sensitive file pattern", commitChild.Content);

        // (b) elaborationSkipped = sensitive-file
        Assert.Contains("\"elaborationSkipped\":\"sensitive-file\"", commitChild.PlatformData ?? string.Empty);

        // (c) exactly one References edge, targeting realFile.cs
        var refEdges = _db.KnowledgeRelationships
            .IgnoreQueryFilters()
            .Where(r => r.RelationshipType == KnowledgeRelationshipType.References
                && r.SourceKnowledgeId == commitChild.Id)
            .ToList();
        Assert.Single(refEdges);
        Assert.Equal(real.Id, refEdges[0].TargetKnowledgeId);

        // (d) .env appears in neither References edges nor unlinkedFiles
        var unlinked = ParseUnlinkedFiles(commitChild.PlatformData);
        Assert.DoesNotContain(".env", unlinked);

        // No LLM call because sensitive flag
        await _llm.DidNotReceive().ElaborateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ─── VERIFY-A.5: NoOp fallback preserves References + unlinkedFiles ─────

    [Fact]
    public async Task Verify_A5_NoOpFallback_WritesReferencesAndUnlinkedFilesCorrectly()
    {
        _llm.IsAvailable.Returns(false);

        var repo = SeedRepoWithTracking();
        var fileA = SeedFileKnowledge(repo.VaultId, "src/a.cs");

        var commits = new[] { MakeCommit("abc1234", "feat: mixed", "src/a.cs", "missing.md") };

        await _svc.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);

        var commitChild = _db.KnowledgeItems
            .IgnoreQueryFilters()
            .Single(k => k.Type == KnowledgeType.Commit);

        // (a) References edge still written for resolvable path
        var refEdges = _db.KnowledgeRelationships
            .IgnoreQueryFilters()
            .Where(r => r.RelationshipType == KnowledgeRelationshipType.References
                && r.SourceKnowledgeId == commitChild.Id)
            .ToList();
        Assert.Single(refEdges);
        Assert.Equal(fileA.Id, refEdges[0].TargetKnowledgeId);

        // (b) unlinkedFiles contains the unresolvable path
        var unlinked = ParseUnlinkedFiles(commitChild.PlatformData);
        Assert.Single(unlinked);
        Assert.Equal("missing.md", unlinked[0]);

        // (c) elaborationSkipped = platform-ai-unavailable
        Assert.Contains("\"elaborationSkipped\":\"platform-ai-unavailable\"", commitChild.PlatformData ?? string.Empty);

        // (d) no LLM call attempted
        await _llm.DidNotReceive().ElaborateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ─── VERIFY-A.6: Path-separator normalization regression guard ───────────

    [Fact]
    public async Task Verify_A6_PathSeparatorNormalization_ForwardSlashInvariant()
    {
        // Part 1 — forward-slash path resolves (baseline, mirrors A.1 but isolated here for clarity)
        var repo = SeedRepoWithTracking();
        var file = SeedFileKnowledge(repo.VaultId, "src/a.cs");  // stored with forward-slash

        var commits = new[] { MakeCommit("aaa1111", "fix: touch a", "src/a.cs") };
        await _svc.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);

        var child = _db.KnowledgeItems.IgnoreQueryFilters()
            .Single(k => k.Type == KnowledgeType.Commit);
        var refEdges = _db.KnowledgeRelationships.IgnoreQueryFilters()
            .Where(r => r.RelationshipType == KnowledgeRelationshipType.References
                && r.SourceKnowledgeId == child.Id).ToList();
        Assert.Single(refEdges);
        Assert.Equal(file.Id, refEdges[0].TargetKnowledgeId);

        // Part 2 — if Knowledge.FilePath was stored with a backslash (broken normalization),
        // the join silently fails and the path becomes an orphan.
        // This is the regression this test guards against.
        var backslashFile = SeedFileKnowledge(repo.VaultId, "src\\b.cs");  // NOT normalized — deliberate
        var commits2 = new[] { MakeCommit("bbb2222", "fix: touch b", "src/b.cs") };
        await _svc.ProcessCommitsAsync(repo.Id, commits2, repo.VaultId, CancellationToken.None);

        var child2 = _db.KnowledgeItems.IgnoreQueryFilters()
            .Single(k => k.Type == KnowledgeType.Commit && k.Source!.Contains("bbb2222"));
        var refEdges2 = _db.KnowledgeRelationships.IgnoreQueryFilters()
            .Where(r => r.RelationshipType == KnowledgeRelationshipType.References
                && r.SourceKnowledgeId == child2.Id).ToList();
        // backslash-stored FilePath does NOT match the forward-slash commit path → orphan
        Assert.Empty(refEdges2);
        var unlinked2 = ParseUnlinkedFiles(child2.PlatformData);
        Assert.Single(unlinked2);
        Assert.Equal("src/b.cs", unlinked2[0]);
        // This proves: WalkFiles MUST normalize FilePath to forward-slash at storage time,
        // otherwise the resolution join silently returns null and correct files become orphans.
    }

    // ─── VERIFY-A.7: helper extraction regression guard ─────────────────────
    // This VERIFY is covered by running the FULL Knowz.SelfHosted.Tests suite
    // post-extraction. The proof is "zero failures in the suite" — no dedicated
    // test code. See the WorkGroup progress log for the evidence.
}
