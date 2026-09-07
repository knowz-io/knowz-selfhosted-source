using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.Core.Models;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Application.Services.GitCommitHistory;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Knowz.SelfHosted.Tests.GitCommitHistoryTests;

/// <summary>
/// Tests for NODE-2 (FEAT_CommittedAtColumn). Verifies that the new
/// <see cref="Knowledge.CommittedAt"/> column is populated by ingestion,
/// drives the commit-history sort, and participates in the R-5 precedence
/// rule (column &gt; JSON &gt; CreatedAt) inside <c>MapCommitHistoryEntry</c>.
///
/// VERIFY criteria covered (from knowzcode/specs/FEAT_CommittedAtColumn.md):
///   VERIFY-2.1 Column exists on <see cref="SelfHostedDbContext"/> model.
///   VERIFY-2.3 Selfhosted ingestion populates the column AND keeps the
///              existing <c>PlatformData.committedAt</c> JSON key.
///   VERIFY-2.4 Out-of-order walk sorts by CommittedAt (not CreatedAt).
///   VERIFY-2.5 NULL CommittedAt falls back to CreatedAt for sort.
///   VERIFY-2.6 MapCommitHistoryEntry precedence: column &gt; JSON &gt; CreatedAt.
///
/// WorkGroupID: kc-feat-commit-history-polish-20260411-051000
/// NodeID: NODE-2 CommittedAtColumn
/// </summary>
public class CommittedAtColumnTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly ICommitElaborationLlmClient _llm;
    private readonly GitCommitHistoryService _ingestionSvc;
    private readonly KnowledgeService _knowledgeSvc;

    public CommittedAtColumnTests()
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

        _ingestionSvc = new GitCommitHistoryService(
            _db,
            _tenantProvider,
            new CommitSecretScanner(),
            new CommitElaborationPromptBuilder(new CommitSecretScanner(), NullLogger<CommitElaborationPromptBuilder>.Instance),
            _llm,
            NullLogger<GitCommitHistoryService>.Instance);

        var knowledgeRepo = Substitute.For<ISelfHostedRepository<Knowledge>>();
        var tagRepo = Substitute.For<ISelfHostedRepository<Tag>>();
        var search = Substitute.For<ISearchService>();
        var openAi = Substitute.For<IOpenAIService>();
        var chunking = Substitute.For<ISelfHostedChunkingService>();

        _knowledgeSvc = new KnowledgeService(
            knowledgeRepo,
            tagRepo,
            _db,
            search,
            openAi,
            chunking,
            _tenantProvider,
            NullLogger<KnowledgeService>.Instance);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private (Vault vault, Knowledge file) SeedVaultAndFile(string filePath = "src/target.cs")
    {
        var vault = new Vault { Id = Guid.NewGuid(), TenantId = TenantId, Name = "v" };
        _db.Vaults.Add(vault);

        var file = new Knowledge
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Title = Path.GetFileName(filePath),
            Content = $"contents of {filePath}",
            FilePath = filePath,
            Source = "git-sync",
            Type = KnowledgeType.Code,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.KnowledgeItems.Add(file);
        _db.KnowledgeVaults.Add(new KnowledgeVault
        {
            TenantId = TenantId,
            KnowledgeId = file.Id,
            VaultId = vault.Id,
            IsPrimary = true
        });
        _db.SaveChanges();
        return (vault, file);
    }

    /// <summary>
    /// Seeds a commit child with the NODE-2 <see cref="Knowledge.CommittedAt"/> column
    /// set to the provided value. Mirrors <c>CommitHistoryQueryTests.SeedCommitChild</c>
    /// but bypasses the JSON-only path so tests can exercise the column directly.
    /// </summary>
    private Knowledge SeedCommitChildWithColumn(
        Guid vaultId,
        Guid fileId,
        string sha,
        DateTime createdAt,
        DateTime? committedAt,
        string? jsonCommittedAtOverride = null,
        bool writeJsonCommittedAt = true,
        string authorName = "Alice",
        int changedFileCount = 1,
        int linesAdded = 10,
        int linesDeleted = 2)
    {
        var platformDataDict = new Dictionary<string, object?>
        {
            ["commitSha"] = sha,
            ["authorName"] = authorName,
            ["changedFileCount"] = changedFileCount,
            ["linesAddedTotal"] = linesAdded,
            ["linesDeletedTotal"] = linesDeleted,
            ["unlinkedFiles"] = Array.Empty<string>()
        };

        if (writeJsonCommittedAt)
        {
            // Default to the same value as column unless an override is supplied.
            platformDataDict["committedAt"] = jsonCommittedAtOverride
                ?? committedAt?.ToString("o")
                ?? createdAt.ToString("o");
        }

        var commit = new Knowledge
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Title = $"Commit {sha[..Math.Min(7, sha.Length)]}: change",
            Content = $"elaborated prose about {sha}",
            Source = $"https://git.example.com/repo.git:main:commit:{sha}",
            Type = KnowledgeType.Commit,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            CommittedAt = committedAt,
            PlatformData = JsonSerializer.Serialize(platformDataDict)
        };

        _db.KnowledgeItems.Add(commit);
        _db.KnowledgeVaults.Add(new KnowledgeVault
        {
            TenantId = TenantId,
            KnowledgeId = commit.Id,
            VaultId = vaultId,
            IsPrimary = true
        });
        _db.KnowledgeRelationships.Add(new KnowledgeRelationship
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            SourceKnowledgeId = commit.Id,
            TargetKnowledgeId = fileId,
            RelationshipType = KnowledgeRelationshipType.References,
            Confidence = 1.0,
            Weight = 1.0,
            IsAutoDetected = true,
            IsBidirectional = false
        });
        _db.SaveChanges();
        return commit;
    }

    private GitRepository SeedRepoWithTracking()
    {
        var vault = new Vault { Id = Guid.NewGuid(), TenantId = TenantId, Name = "ingest-vault" };
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

    private static CommitDescriptor MakeCommit(string sha, DateTimeOffset committedAt)
        => new(
            Sha: sha,
            ParentShas: Array.Empty<string>(),
            AuthorName: "Alice",
            AuthorEmail: "alice@example.com",
            AuthoredAt: committedAt,
            CommittedAt: committedAt,
            Message: "refactor: thing",
            ChangedFiles: new List<CommitChangedFile>
            {
                new("src/Foo.cs", 1, 0, GitCommitChangeType.Modified)
            });

    // ─── VERIFY-2.1: Column exists on the EF model ───────────────────────────

    [Fact]
    public void Verify_2_1_CommittedAt_ExistsInEfModel()
    {
        // Inspect the EF model rather than raw SQL so the test works against
        // the InMemory provider used by the rest of this test class.
        var entity = _db.Model.FindEntityType(typeof(Knowledge));
        Assert.NotNull(entity);
        var property = entity!.FindProperty(nameof(Knowledge.CommittedAt));

        Assert.NotNull(property);
        Assert.True(property!.IsNullable,
            "CommittedAt must be nullable (pre-NODE-2 rows stay NULL until backfill).");
        Assert.Equal(typeof(DateTime?), property.ClrType);
    }

    // ─── VERIFY-2.3: Selfhosted ingestion populates column + keeps JSON key ──

    [Fact]
    public async Task Verify_2_3_SelfhostedIngestion_SetsColumn_AndKeepsJsonCommittedAtKey()
    {
        var repo = SeedRepoWithTracking();
        var committedAt = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero);
        var commits = new[] { MakeCommit("aaa1111", committedAt) };

        await _ingestionSvc.ProcessCommitsAsync(repo.Id, commits, repo.VaultId, CancellationToken.None);

        var child = _db.KnowledgeItems
            .IgnoreQueryFilters()
            .Single(k => k.Type == KnowledgeType.Commit);

        // Column must be set from desc.CommittedAt.UtcDateTime.
        Assert.NotNull(child.CommittedAt);
        Assert.Equal(committedAt.UtcDateTime, child.CommittedAt!.Value);
        Assert.Equal(DateTimeKind.Utc, DateTime.SpecifyKind(child.CommittedAt.Value, DateTimeKind.Utc).Kind);

        // JSON metadata must still carry committedAt for NODE-3 backfill compatibility.
        Assert.False(string.IsNullOrEmpty(child.PlatformData));
        using var doc = JsonDocument.Parse(child.PlatformData!);
        Assert.True(doc.RootElement.TryGetProperty("committedAt", out var jsonCommittedAt),
            "Ingestion must still write 'committedAt' into PlatformData JSON for NODE-3 compatibility.");
        Assert.Equal(JsonValueKind.String, jsonCommittedAt.ValueKind);
        Assert.True(DateTime.TryParse(jsonCommittedAt.GetString(), out var parsed));
        Assert.Equal(committedAt.UtcDateTime, parsed.ToUniversalTime());
    }

    // ─── VERIFY-2.4: Out-of-order walk sorts by CommittedAt ──────────────────

    [Fact]
    public async Task Verify_2_4_OutOfOrderWalk_SortsByCommittedAt_NotCreatedAt()
    {
        var (vault, file) = SeedVaultAndFile();

        // CreatedAt order: A newest, B middle, C oldest
        // CommittedAt order: C newest, B middle, A oldest (inverted)
        var a = SeedCommitChildWithColumn(vault.Id, file.Id,
            sha: "aaa1111",
            createdAt: new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            committedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var b = SeedCommitChildWithColumn(vault.Id, file.Id,
            sha: "bbb2222",
            createdAt: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            committedAt: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        var c = SeedCommitChildWithColumn(vault.Id, file.Id,
            sha: "ccc3333",
            createdAt: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            committedAt: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        var (items, total) = await _knowledgeSvc.GetCommitHistoryForItemAsync(
            file.Id, 1, 20, CancellationToken.None);

        Assert.Equal(3, total);
        Assert.Equal(3, items.Count);
        Assert.Equal(c.Id, items[0].KnowledgeId);
        Assert.Equal(b.Id, items[1].KnowledgeId);
        Assert.Equal(a.Id, items[2].KnowledgeId);
    }

    // ─── VERIFY-2.5: NULL CommittedAt falls back to CreatedAt for sort ───────

    [Fact]
    public async Task Verify_2_5_NullCommittedAt_FallsBackToCreatedAt_InSort()
    {
        var (vault, file) = SeedVaultAndFile();

        // Row A: CommittedAt 2026-03-01 (newest true commit time)
        var newest = SeedCommitChildWithColumn(vault.Id, file.Id,
            sha: "aaa0301",
            createdAt: new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc),
            committedAt: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        // Row B: pre-NODE-2 row, column NULL, CreatedAt 2026-02-01 (middle fallback)
        var middleNull = SeedCommitChildWithColumn(vault.Id, file.Id,
            sha: "bbb0201",
            createdAt: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            committedAt: null,
            writeJsonCommittedAt: false);

        // Row C: CommittedAt 2026-01-01 (oldest)
        var oldest = SeedCommitChildWithColumn(vault.Id, file.Id,
            sha: "ccc0101",
            createdAt: new DateTime(2025, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            committedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var (items, total) = await _knowledgeSvc.GetCommitHistoryForItemAsync(
            file.Id, 1, 20, CancellationToken.None);

        // Expect: 2026-03-01 (column), 2026-02-01 (NULL column → CreatedAt fallback), 2026-01-01 (column)
        Assert.Equal(3, total);
        Assert.Equal(newest.Id, items[0].KnowledgeId);
        Assert.Equal(middleNull.Id, items[1].KnowledgeId);
        Assert.Equal(oldest.Id, items[2].KnowledgeId);
    }

    // ─── VERIFY-2.6: MapCommitHistoryEntry precedence ────────────────────────

    [Fact]
    public async Task Verify_2_6_MapPrecedence_ColumnWinsOverJsonOverCreatedAt()
    {
        var (vault, file) = SeedVaultAndFile();

        // Row 1: column set AND JSON committedAt set to a stale value — column must win.
        var columnDate = new DateTime(2026, 3, 15, 10, 0, 0, DateTimeKind.Utc);
        var staleJsonDate = "2020-01-01T00:00:00Z";
        var columnWinsRow = SeedCommitChildWithColumn(vault.Id, file.Id,
            sha: "col1111",
            createdAt: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            committedAt: columnDate,
            jsonCommittedAtOverride: staleJsonDate);

        var (items1, _) = await _knowledgeSvc.GetCommitHistoryForItemAsync(
            file.Id, 1, 20, CancellationToken.None);
        var columnEntry = items1.Single(i => i.KnowledgeId == columnWinsRow.Id);
        Assert.Equal(columnDate, columnEntry.CommittedAt);
        Assert.NotEqual(DateTime.Parse(staleJsonDate).ToUniversalTime(), columnEntry.CommittedAt);

        // Isolate row 2 and row 3 by using a fresh file with no prior rows.
        var (vault2, file2) = SeedVaultAndFile(filePath: "src/other.cs");

        // Row 2: column NULL, JSON committedAt present — JSON fallback wins.
        var jsonDate = new DateTime(2023, 5, 5, 12, 0, 0, DateTimeKind.Utc);
        var jsonFallbackRow = SeedCommitChildWithColumn(vault2.Id, file2.Id,
            sha: "jsn2222",
            createdAt: new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            committedAt: null,
            jsonCommittedAtOverride: jsonDate.ToString("o"));

        // Row 3: column NULL, JSON missing committedAt — CreatedAt fallback wins.
        var createdFallbackTime = new DateTime(2022, 6, 6, 6, 6, 6, DateTimeKind.Utc);
        var createdFallbackRow = SeedCommitChildWithColumn(vault2.Id, file2.Id,
            sha: "crt3333",
            createdAt: createdFallbackTime,
            committedAt: null,
            writeJsonCommittedAt: false);

        var (items2, _) = await _knowledgeSvc.GetCommitHistoryForItemAsync(
            file2.Id, 1, 20, CancellationToken.None);

        var jsonEntry = items2.Single(i => i.KnowledgeId == jsonFallbackRow.Id);
        Assert.Equal(jsonDate, jsonEntry.CommittedAt.ToUniversalTime());

        var createdEntry = items2.Single(i => i.KnowledgeId == createdFallbackRow.Id);
        Assert.Equal(createdFallbackTime, createdEntry.CommittedAt);
    }
}
