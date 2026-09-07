using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.Core.Models;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Knowz.SelfHosted.Tests.GitCommitHistoryTests;

/// <summary>
/// Tests for the per-item commit-history query (NodeID B —
/// SelfHostedKnowledgeCommitHistoryQuery). Covers VERIFY-B.1 through VERIFY-B.4
/// from API_SelfHostedVaultKnowledgeCommits.md.
///
/// Service-level tests on <see cref="KnowledgeService.GetCommitHistoryForItemAsync"/>.
/// The HTTP auth gate (VERIFY-B.3 403 path) is exercised by asserting the service
/// returns the correct "which vaults does this knowledge item belong to" data
/// through <see cref="KnowledgeService.GetKnowledgeVaultIdsAsync"/>, which the
/// endpoint uses to gate access. The endpoint itself is a thin pass-through
/// verified by inspection.
///
/// WorkGroupID: kc-feat-commit-knowledge-link-20260410-230500
/// </summary>
public class CommitHistoryQueryTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SelfHostedDbContext _db;
    private readonly KnowledgeService _svc;

    public CommitHistoryQueryTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);

        var knowledgeRepo = Substitute.For<ISelfHostedRepository<Knowledge>>();
        var tagRepo = Substitute.For<ISelfHostedRepository<Tag>>();
        var search = Substitute.For<ISearchService>();
        var openAi = Substitute.For<IOpenAIService>();
        var chunking = Substitute.For<ISelfHostedChunkingService>();

        _svc = new KnowledgeService(
            knowledgeRepo,
            tagRepo,
            _db,
            search,
            openAi,
            chunking,
            tenantProvider,
            NullLogger<KnowledgeService>.Instance);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

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

    private Knowledge SeedCommitChild(
        Guid vaultId,
        Guid fileId,
        string sha,
        DateTime createdAt,
        string authorName = "Alice",
        int changedFileCount = 1,
        int linesAdded = 10,
        int linesDeleted = 2)
    {
        var commit = new Knowledge
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Title = $"Commit {sha[..7]}: some change",
            Content = $"elaborated prose about {sha}",
            Source = $"https://git.example.com/repo.git:main:commit:{sha}",
            Type = KnowledgeType.Commit,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            PlatformData = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["commitSha"] = sha,
                ["authorName"] = authorName,
                ["committedAt"] = createdAt,
                ["changedFileCount"] = changedFileCount,
                ["linesAddedTotal"] = linesAdded,
                ["linesDeletedTotal"] = linesDeleted,
                ["unlinkedFiles"] = Array.Empty<string>()
            })
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

    // ─── VERIFY-B.1: Three commits → three ordered items ────────────────────

    [Fact]
    public async Task Verify_B1_ThreeCommitsTargetingFile_ReturnsThreeOrderedItems()
    {
        var (vault, file) = SeedVaultAndFile();
        var oldest = SeedCommitChild(vault.Id, file.Id, "abc1111", DateTime.UtcNow.AddDays(-3), "Alice", 1, 5, 0);
        var middle = SeedCommitChild(vault.Id, file.Id, "def2222", DateTime.UtcNow.AddDays(-2), "Bob", 2, 10, 1);
        var newest = SeedCommitChild(vault.Id, file.Id, "ghi3333", DateTime.UtcNow.AddDays(-1), "Carol", 3, 15, 2);

        var (items, total) = await _svc.GetCommitHistoryForItemAsync(file.Id, 1, 20, CancellationToken.None);

        Assert.Equal(3, total);
        Assert.Equal(3, items.Count);

        // Most-recent first
        Assert.Equal(newest.Id, items[0].KnowledgeId);
        Assert.Equal("ghi3333", items[0].Sha);
        Assert.Equal("ghi3333", items[0].ShortSha); // sha < 7 chars stays as-is
        Assert.Equal("Carol", items[0].AuthorName);
        Assert.Equal(3, items[0].ChangedFileCount);
        Assert.Equal(15, items[0].LinesAdded);
        Assert.Equal(2, items[0].LinesDeleted);

        Assert.Equal(middle.Id, items[1].KnowledgeId);
        Assert.Equal(oldest.Id, items[2].KnowledgeId);
    }

    // ─── VERIFY-B.2: Zero commits → empty array, not 404 ────────────────────

    [Fact]
    public async Task Verify_B2_ZeroCommits_ReturnsEmptyList()
    {
        var (_, file) = SeedVaultAndFile();
        // No commits seeded

        var (items, total) = await _svc.GetCommitHistoryForItemAsync(file.Id, 1, 20, CancellationToken.None);

        Assert.Equal(0, total);
        Assert.Empty(items);
    }

    // ─── VERIFY-B.3: Vault access — service returns vault IDs for gate ──────

    [Fact]
    public async Task Verify_B3_GetKnowledgeVaultIds_ReturnsVaultsForGate()
    {
        // VERIFY-B.3 is primarily an endpoint-level assertion: the HTTP handler uses
        // GetKnowledgeVaultIdsAsync to gate access. This test proves the service
        // correctly reports the vault IDs the endpoint auth-gate will check against.
        var (vault, file) = SeedVaultAndFile();
        SeedCommitChild(vault.Id, file.Id, "abc1111", DateTime.UtcNow);

        var vaultIds = await _svc.GetKnowledgeVaultIdsAsync(file.Id, CancellationToken.None);
        Assert.Single(vaultIds);
        Assert.Equal(vault.Id, vaultIds[0]);

        // Proving service is unguarded-by-design: it is the endpoint's job to gate.
        // A caller that has already passed the vault-access check will get data here.
        var (items, total) = await _svc.GetCommitHistoryForItemAsync(file.Id, 1, 20, CancellationToken.None);
        Assert.Equal(1, total);
        Assert.Single(items);
    }

    // ─── VERIFY-B.4: Pagination ─────────────────────────────────────────────

    [Fact]
    public async Task Verify_B4_Pagination_FivecommitsPageSizeTwo_Page2GivesItems3and4()
    {
        var (vault, file) = SeedVaultAndFile();
        var base_ = DateTime.UtcNow.AddHours(-10);
        var c1 = SeedCommitChild(vault.Id, file.Id, "c1111111", base_.AddMinutes(1));
        var c2 = SeedCommitChild(vault.Id, file.Id, "c2222222", base_.AddMinutes(2));
        var c3 = SeedCommitChild(vault.Id, file.Id, "c3333333", base_.AddMinutes(3));
        var c4 = SeedCommitChild(vault.Id, file.Id, "c4444444", base_.AddMinutes(4));
        var c5 = SeedCommitChild(vault.Id, file.Id, "c5555555", base_.AddMinutes(5));

        // Ordered most-recent first: c5, c4, c3, c2, c1
        var (page2, total2) = await _svc.GetCommitHistoryForItemAsync(file.Id, 2, 2, CancellationToken.None);
        Assert.Equal(5, total2);
        Assert.Equal(2, page2.Count);
        Assert.Equal(c3.Id, page2[0].KnowledgeId);
        Assert.Equal(c2.Id, page2[1].KnowledgeId);

        var (page3, total3) = await _svc.GetCommitHistoryForItemAsync(file.Id, 3, 2, CancellationToken.None);
        Assert.Equal(5, total3);
        Assert.Single(page3);
        Assert.Equal(c1.Id, page3[0].KnowledgeId);
    }
}
