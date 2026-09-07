using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services.GitCommitHistory;
using Knowz.SelfHosted.Application.Services.Shared;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Knowz.SelfHosted.Tests.GitCommitHistoryTests;

/// <summary>
/// Tests for NODE-3 (CommitBackfillEndpoint) — <see cref="CommitRelinkService"/> and the
/// <c>BuildInitialChildMetadataJson</c> addition that persists <c>changedFilePaths</c> into
/// <c>PlatformData</c> during ingestion.
///
/// Covers VERIFY-3.1 through VERIFY-3.7 from <c>FEAT_CommitBackfillEndpoint.md</c>
/// (VERIFY-3.5 is HTTP-level and lives in the endpoint tests — see <c>CommitHistoryEndpointTests</c>
/// style harness if added).
///
/// WorkGroupID: kc-feat-commit-history-polish-20260411-051000
/// NodeID: NODE-3 CommitBackfillEndpoint
/// </summary>
public class CommitRelinkServiceTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly ICommitElaborationLlmClient _llm;
    private readonly GitCommitHistoryService _commitHistoryService;
    private readonly CommitRelinkService _relink;

    public CommitRelinkServiceTests()
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

        _commitHistoryService = new GitCommitHistoryService(
            _db,
            _tenantProvider,
            new CommitSecretScanner(),
            new CommitElaborationPromptBuilder(new CommitSecretScanner(), NullLogger<CommitElaborationPromptBuilder>.Instance),
            _llm,
            NullLogger<GitCommitHistoryService>.Instance);

        _relink = new CommitRelinkService(
            _db,
            _tenantProvider,
            _commitHistoryService,
            NullLogger<CommitRelinkService>.Instance);
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

    /// <summary>
    /// Seeds a bare commit child row directly (simulating what ingestion would have written).
    /// Allows callers to control the PlatformData JSON precisely — used to drive the
    /// "pre-NODE-3 row missing changedFilePaths" scenario and also the "paths present, edges not"
    /// backfill scenario without re-running ingestion.
    /// </summary>
    private Knowledge SeedCommitChild(
        GitRepository repo,
        string sha,
        string? platformDataJson)
    {
        var commit = new Knowledge
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Title = $"Commit {sha[..7]}: seeded",
            Content = "seeded stub",
            Source = $"{repo.RepositoryUrl}:{repo.Branch}:commit:{sha}",
            Type = KnowledgeType.Commit,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            PlatformData = platformDataJson
        };
        _db.KnowledgeItems.Add(commit);
        _db.KnowledgeVaults.Add(new KnowledgeVault
        {
            TenantId = TenantId,
            KnowledgeId = commit.Id,
            VaultId = repo.VaultId,
            IsPrimary = true
        });
        _db.SaveChanges();
        return commit;
    }

    private static string BuildPlatformDataWithPaths(params string[] paths)
    {
        var obj = new Dictionary<string, object?>
        {
            ["commitSha"] = "deadbeef",
            ["unlinkedFiles"] = Array.Empty<string>(),
            ["changedFilePaths"] = paths
        };
        return JsonSerializer.Serialize(obj);
    }

    private static string BuildPlatformDataWithoutPaths()
    {
        var obj = new Dictionary<string, object?>
        {
            ["commitSha"] = "deadbeef",
            ["unlinkedFiles"] = Array.Empty<string>()
            // Deliberately no "changedFilePaths" — simulates pre-NODE-3 row.
        };
        return JsonSerializer.Serialize(obj);
    }

    private static CommitDescriptor MakeCommit(string sha, string msg, params string[] paths) =>
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

    private static List<string>? ParseChangedFilePaths(string? platformDataJson)
    {
        if (string.IsNullOrEmpty(platformDataJson)) return null;
        using var doc = JsonDocument.Parse(platformDataJson);
        if (!doc.RootElement.TryGetProperty("changedFilePaths", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        return arr.EnumerateArray()
            .Select(e => e.GetString() ?? string.Empty)
            .ToList();
    }

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

    // ─── VERIFY-3.1: Ingestion writes changedFilePaths ───────────────────────

    [Fact]
    public async Task Verify_3_1_Ingestion_Writes_ChangedFilePaths_Excluding_Sensitive()
    {
        var repo = SeedRepoWithTracking();
        // Pre-create file rows so the resolution loop runs but the path list still gets persisted
        // regardless (the key is independent of linking).
        SeedFileKnowledge(repo.VaultId, "src/a.cs");
        SeedFileKnowledge(repo.VaultId, "src/b.cs");

        var commits = new[] { MakeCommit("abc1234", "feat: mixed", "src/a.cs", "src/b.cs", ".env") };
        await _commitHistoryService.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);

        var commitChild = _db.KnowledgeItems
            .IgnoreQueryFilters()
            .Single(k => k.Type == KnowledgeType.Commit);

        var paths = ParseChangedFilePaths(commitChild.PlatformData);
        Assert.NotNull(paths);
        Assert.Equal(2, paths!.Count);
        Assert.Contains("src/a.cs", paths);
        Assert.Contains("src/b.cs", paths);
        Assert.DoesNotContain(".env", paths); // CRIT-2 write-time filter
    }

    // ─── VERIFY-3.2: Shared helper delivers identical edges to both callers ─

    [Fact]
    public async Task Verify_3_2_BackfillRebuildsSameEdgesAsIngestion_AfterManualDelete()
    {
        var repo = SeedRepoWithTracking();
        var fileA = SeedFileKnowledge(repo.VaultId, "src/a.cs");
        var fileB = SeedFileKnowledge(repo.VaultId, "src/b.cs");

        var commits = new[] { MakeCommit("abc1234", "feat: mixed", "src/a.cs", "src/b.cs", ".env") };
        await _commitHistoryService.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);

        var commitChild = _db.KnowledgeItems
            .IgnoreQueryFilters()
            .Single(k => k.Type == KnowledgeType.Commit);

        // Ingestion must produce exactly 2 edges (not 3 — .env is sensitive).
        var edgesAfterIngest = _db.KnowledgeRelationships
            .IgnoreQueryFilters()
            .Where(r => r.RelationshipType == KnowledgeRelationshipType.References
                && r.SourceKnowledgeId == commitChild.Id)
            .ToList();
        Assert.Equal(2, edgesAfterIngest.Count);

        // Manually strip the References edges to simulate "edges lost / never built".
        _db.KnowledgeRelationships.RemoveRange(edgesAfterIngest);
        await _db.SaveChangesAsync();

        var result = await _relink.RelinkRepositoryAsync(repo.Id, CancellationToken.None);

        Assert.Equal(1, result.Processed);
        Assert.Equal(2, result.Linked);
        Assert.Equal(0, result.Skipped);

        var edgesAfterRelink = _db.KnowledgeRelationships
            .IgnoreQueryFilters()
            .Where(r => r.RelationshipType == KnowledgeRelationshipType.References
                && r.SourceKnowledgeId == commitChild.Id)
            .ToList();
        Assert.Equal(2, edgesAfterRelink.Count);
        Assert.Contains(edgesAfterRelink, r => r.TargetKnowledgeId == fileA.Id);
        Assert.Contains(edgesAfterRelink, r => r.TargetKnowledgeId == fileB.Id);
    }

    // ─── VERIFY-3.3: Idempotent re-run — no new edges on second call ─────────

    [Fact]
    public async Task Verify_3_3_Relink_Is_Idempotent()
    {
        var repo = SeedRepoWithTracking();
        SeedFileKnowledge(repo.VaultId, "src/a.cs");
        SeedFileKnowledge(repo.VaultId, "src/b.cs");
        SeedFileKnowledge(repo.VaultId, "src/c.cs");

        // Seed 5 commits × 3 files each.
        var commits = Enumerable.Range(0, 5)
            .Select(i => MakeCommit($"abc{i:D4}", $"c{i}", "src/a.cs", "src/b.cs", "src/c.cs"))
            .ToArray();
        await _commitHistoryService.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);

        // After ingestion: 15 edges (5 commits × 3 files).
        var edgesBefore = _db.KnowledgeRelationships
            .IgnoreQueryFilters()
            .Count(r => r.RelationshipType == KnowledgeRelationshipType.References);
        Assert.Equal(15, edgesBefore);

        // First relink: all rows visited, zero new edges.
        var result1 = await _relink.RelinkRepositoryAsync(repo.Id, CancellationToken.None);
        Assert.Equal(5, result1.Processed);
        Assert.Equal(0, result1.Linked);
        Assert.Equal(0, result1.Skipped);

        var edgesAfter1 = _db.KnowledgeRelationships
            .IgnoreQueryFilters()
            .Count(r => r.RelationshipType == KnowledgeRelationshipType.References);
        Assert.Equal(15, edgesAfter1);

        // Second relink: same result.
        var result2 = await _relink.RelinkRepositoryAsync(repo.Id, CancellationToken.None);
        Assert.Equal(5, result2.Processed);
        Assert.Equal(0, result2.Linked);
        Assert.Equal(0, result2.Skipped);

        var edgesAfter2 = _db.KnowledgeRelationships
            .IgnoreQueryFilters()
            .Count(r => r.RelationshipType == KnowledgeRelationshipType.References);
        Assert.Equal(15, edgesAfter2);
    }

    // ─── VERIFY-3.4: Pre-NODE-3 rows report as skipped ───────────────────────

    [Fact]
    public async Task Verify_3_4_PreNode3Row_MissingChangedFilePaths_IsSkipped_NotError()
    {
        var repo = SeedRepoWithTracking();
        SeedFileKnowledge(repo.VaultId, "src/a.cs"); // file exists but commit row has no paths

        var preNode3Json = BuildPlatformDataWithoutPaths();
        var seededCommit = SeedCommitChild(repo, "deadbeef", preNode3Json);
        var originalUpdatedAt = seededCommit.UpdatedAt;

        var result = await _relink.RelinkRepositoryAsync(repo.Id, CancellationToken.None);

        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Linked);
        Assert.Equal(1, result.Skipped);

        // No edges created.
        var edges = _db.KnowledgeRelationships
            .IgnoreQueryFilters()
            .Count(r => r.SourceKnowledgeId == seededCommit.Id
                && r.RelationshipType == KnowledgeRelationshipType.References);
        Assert.Equal(0, edges);

        // PlatformData not rewritten — no spurious UpdatedAt bump (row untouched).
        var reloaded = _db.KnowledgeItems.IgnoreQueryFilters().Single(k => k.Id == seededCommit.Id);
        Assert.Equal(preNode3Json, reloaded.PlatformData);
        Assert.Equal(originalUpdatedAt, reloaded.UpdatedAt);
    }

    // ─── VERIFY-3.6: Sensitive path stays skipped at read time ───────────────

    [Fact]
    public async Task Verify_3_6_SensitivePathInStoredPaths_IsRejectedAtReadTime()
    {
        var repo = SeedRepoWithTracking();
        // Seed a file knowledge row that would match the sensitive path by FilePath.
        // Even so, the read-time IsSensitiveFile filter must skip it — no edge.
        SeedFileKnowledge(repo.VaultId, "config/secrets.yaml");

        // Manually seed a commit row whose changedFilePaths contains a sensitive file
        // (simulating: the deny-list was expanded AFTER this commit was ingested, OR
        // a test wrote it directly).
        var platformData = BuildPlatformDataWithPaths("config/secrets.yaml");
        var commit = SeedCommitChild(repo, "sens0001", platformData);

        var result = await _relink.RelinkRepositoryAsync(repo.Id, CancellationToken.None);

        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Linked);
        Assert.Equal(0, result.Skipped);

        // Zero edges for this commit — sensitive path rejected at read time.
        var edges = _db.KnowledgeRelationships
            .IgnoreQueryFilters()
            .Count(r => r.SourceKnowledgeId == commit.Id
                && r.RelationshipType == KnowledgeRelationshipType.References);
        Assert.Equal(0, edges);
    }

    // ─── VERIFY-3.7: New edges built when file rows arrive after the commit ─

    [Fact]
    public async Task Verify_3_7_RelinkPicksUpFilesCreatedAfterCommit()
    {
        var repo = SeedRepoWithTracking();

        // Step 1: ingest a commit that touches path/newfile.cs — but the file row doesn't exist yet.
        var commits = new[] { MakeCommit("feed0001", "feat: new file", "path/newfile.cs") };
        await _commitHistoryService.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);

        var commitChild = _db.KnowledgeItems
            .IgnoreQueryFilters()
            .Single(k => k.Type == KnowledgeType.Commit);

        // After ingestion: no edges (file didn't exist), path in unlinkedFiles.
        var edges0 = _db.KnowledgeRelationships
            .IgnoreQueryFilters()
            .Count(r => r.SourceKnowledgeId == commitChild.Id
                && r.RelationshipType == KnowledgeRelationshipType.References);
        Assert.Equal(0, edges0);
        var unlinked0 = ParseUnlinkedFiles(commitChild.PlatformData);
        Assert.Single(unlinked0);
        Assert.Equal("path/newfile.cs", unlinked0[0]);

        // Step 2: first relink with no file yet — still no edges, still orphaned.
        var result1 = await _relink.RelinkRepositoryAsync(repo.Id, CancellationToken.None);
        Assert.Equal(1, result1.Processed);
        Assert.Equal(0, result1.Linked);
        Assert.Equal(0, result1.Skipped);

        // Reload commit child from DB — the unlinked list should still hold the path.
        var reloaded1 = _db.KnowledgeItems.IgnoreQueryFilters().Single(k => k.Id == commitChild.Id);
        var unlinkedAfterRelink1 = ParseUnlinkedFiles(reloaded1.PlatformData);
        Assert.Single(unlinkedAfterRelink1);
        Assert.Equal("path/newfile.cs", unlinkedAfterRelink1[0]);

        // Step 3: NOW create the file knowledge row.
        var newFile = SeedFileKnowledge(repo.VaultId, "path/newfile.cs");

        // Step 4: second relink — file exists → edge created.
        var result2 = await _relink.RelinkRepositoryAsync(repo.Id, CancellationToken.None);
        Assert.Equal(1, result2.Processed);
        Assert.Equal(1, result2.Linked);
        Assert.Equal(0, result2.Skipped);

        var edges1 = _db.KnowledgeRelationships
            .IgnoreQueryFilters()
            .Where(r => r.SourceKnowledgeId == commitChild.Id
                && r.RelationshipType == KnowledgeRelationshipType.References)
            .ToList();
        Assert.Single(edges1);
        Assert.Equal(newFile.Id, edges1[0].TargetKnowledgeId);

        // unlinkedFiles should now be empty — path moved from orphan to linked.
        var reloaded2 = _db.KnowledgeItems.IgnoreQueryFilters().Single(k => k.Id == commitChild.Id);
        var unlinkedAfter2 = ParseUnlinkedFiles(reloaded2.PlatformData);
        Assert.Empty(unlinkedAfter2);
    }

    // ─── Bonus: unknown repository id throws ─────────────────────────────────

    [Fact]
    public async Task RelinkRepositoryAsync_UnknownRepo_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _relink.RelinkRepositoryAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
