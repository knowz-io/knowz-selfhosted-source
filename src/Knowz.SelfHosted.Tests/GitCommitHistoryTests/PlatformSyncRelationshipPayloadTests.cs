using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Knowz.SelfHosted.Tests.GitCommitHistoryTests;

/// <summary>
/// Unit tests covering the 5 VERIFY criteria from
/// <c>knowzcode/specs/SVC_PlatformSyncRelationshipPayload.md</c>.
///
/// WorkGroupID: kc-feat-git-commit-knowledge-sync-20260410
/// NodeID: CommitRelationshipSyncPayload (NODE-5)
/// </summary>
public class PlatformSyncRelationshipPayloadTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000042");
    private const string RepoUrl = "https://git.example.com/org/repo.git";
    private const string Branch = "main";

    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly VaultScopedExportService _exportService;

    public PlatformSyncRelationshipPayloadTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _tenantProvider = Substitute.For<ITenantProvider>();
        _tenantProvider.TenantId.Returns(TenantId);

        _db = new SelfHostedDbContext(options, _tenantProvider);
        _exportService = new VaultScopedExportService(
            _db, _tenantProvider, NullLogger<VaultScopedExportService>.Instance);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private Vault SeedVault(string name = "v")
    {
        var vault = new Vault { Id = Guid.NewGuid(), TenantId = TenantId, Name = name };
        _db.Vaults.Add(vault);
        _db.SaveChanges();
        return vault;
    }

    private Knowledge SeedKnowledge(
        Guid vaultId,
        KnowledgeType type,
        string title,
        string? source = null,
        string? filePath = null)
    {
        var k = new Knowledge
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Title = title,
            Content = "content",
            Type = type,
            Source = source,
            FilePath = filePath,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.KnowledgeItems.Add(k);
        _db.KnowledgeVaults.Add(new KnowledgeVault
        {
            TenantId = TenantId,
            KnowledgeId = k.Id,
            VaultId = vaultId,
            IsPrimary = true
        });
        _db.SaveChanges();
        return k;
    }

    private KnowledgeRelationship SeedRelationship(
        Guid sourceId,
        Guid targetId,
        KnowledgeRelationshipType type)
    {
        var r = new KnowledgeRelationship
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            SourceKnowledgeId = sourceId,
            TargetKnowledgeId = targetId,
            RelationshipType = type,
            IsAutoDetected = true,
            IsBidirectional = false
        };
        _db.KnowledgeRelationships.Add(r);
        _db.SaveChanges();
        return r;
    }

    private static string BuildCommitSource(string sha)
        => $"{RepoUrl}:{Branch}:commit:{sha}";

    private static string BuildCommitHistorySource()
        => $"{RepoUrl}:{Branch}:commit-history";

    // ─── VERIFY #1: Only commit-history-scoped relationships serialized ──────

    [Fact]
    public async Task Export_OnlyCommitHistoryScopedRelationshipsSerialized()
    {
        var vault = SeedVault();
        // Seed 2 non-commit Knowledge items with a general-purpose RelatedTo
        // relationship — this must NOT be serialized.
        var noteA = SeedKnowledge(vault.Id, KnowledgeType.Note, "Note A");
        var noteB = SeedKnowledge(vault.Id, KnowledgeType.Note, "Note B");
        SeedRelationship(noteA.Id, noteB.Id, KnowledgeRelationshipType.RelatedTo);

        // Seed a CommitHistory parent + commit child + PartOf relationship — included.
        var parent = SeedKnowledge(
            vault.Id,
            KnowledgeType.CommitHistory,
            "Commit history",
            source: BuildCommitHistorySource());
        var child = SeedKnowledge(
            vault.Id,
            KnowledgeType.Commit,
            "Commit abc1234",
            source: BuildCommitSource("abc1234"));
        SeedRelationship(child.Id, parent.Id, KnowledgeRelationshipType.PartOf);

        // Seed a file + References relationship — also included.
        var file = SeedKnowledge(
            vault.Id,
            KnowledgeType.Document,
            "Foo.cs",
            filePath: "src/Foo.cs");
        SeedRelationship(child.Id, file.Id, KnowledgeRelationshipType.References);

        var package = await _exportService.ExportDeltaAsync(
            vault.Id, since: null, syncLink: null, CancellationToken.None);

        // The commit child must carry 2 relationships (1 PartOf + 1 References).
        var commitChild = package.Data.KnowledgeItems.Single(
            k => k.Type == KnowledgeType.Commit);
        Assert.NotNull(commitChild.Relationships);
        Assert.Equal(2, commitChild.Relationships!.Count);

        // Note A (non-commit) must NOT carry any relationships.
        var noteAExported = package.Data.KnowledgeItems.First(k => k.Title == "Note A");
        Assert.True(
            noteAExported.Relationships == null || noteAExported.Relationships.Count == 0,
            "General-purpose relationships must not ride on commit sync payload.");

        // No exported knowledge item should carry a RelatedTo relationship.
        Assert.DoesNotContain(
            package.Data.KnowledgeItems,
            k => k.Relationships != null
                && k.Relationships.Any(r => r.RelationshipType == KnowledgeRelationshipType.RelatedTo));
    }

    // ─── VERIFY #2: Cross-instance FilePath resolution succeeds ──────────────

    [Fact]
    public async Task Import_ResolvesTargetByFilePath_CreatesRelationshipWithReceiverGuids()
    {
        var receiverVault = SeedVault();

        // Receiver already has:
        //   - a CommitHistory parent (so the commit child's PartOf target resolves)
        //   - a file Knowledge at src/ReadMe.md
        var receiverParent = SeedKnowledge(
            receiverVault.Id,
            KnowledgeType.CommitHistory,
            "Commit history",
            source: BuildCommitHistorySource());
        var receiverFile = SeedKnowledge(
            receiverVault.Id,
            KnowledgeType.Document,
            "ReadMe.md",
            filePath: "src/ReadMe.md");

        // Build a fake incoming payload with a commit child carrying a References
        // payload entry to src/ReadMe.md. GUIDs are deliberately different from the
        // receiver's — cross-instance resolution must NOT use them.
        var senderCommitId = Guid.NewGuid(); // sender's guid, must not leak into receiver
        var package = BuildIncomingPackageWithCommitChild(
            receiverVault.Id,
            commitSha: "abc1234",
            senderCommitKnowledgeId: senderCommitId,
            filePathTargets: new[] { "src/ReadMe.md" });

        await RunImportAsync(receiverVault.Id, package);

        // After import, the receiver should have a Reference relationship:
        //   source = a NEW commit-child Knowledge (resolved by Source string), and
        //   target = receiverFile.Id (the receiver's guid, NOT the sender's).
        var commitChild = _db.KnowledgeItems.Single(
            k => k.Source == BuildCommitSource("abc1234") && k.Type == KnowledgeType.Commit);

        var references = _db.KnowledgeRelationships.Where(
            r => r.SourceKnowledgeId == commitChild.Id
                && r.RelationshipType == KnowledgeRelationshipType.References).ToList();

        Assert.Single(references);
        Assert.Equal(receiverFile.Id, references[0].TargetKnowledgeId);
        Assert.NotEqual(senderCommitId, commitChild.Id); // receiver generated its own guid
    }

    // ─── VERIFY #3: Missing file paths stored as orphan Metadata ─────────────

    [Fact]
    public async Task Import_OrphanTargetFilePath_StoredInUnlinkedFilesMetadata()
    {
        var receiverVault = SeedVault();
        SeedKnowledge(
            receiverVault.Id,
            KnowledgeType.CommitHistory,
            "Commit history",
            source: BuildCommitHistorySource());
        // Seed ONE file that does exist
        var existingFile = SeedKnowledge(
            receiverVault.Id, KnowledgeType.Document, "Existing.md",
            filePath: "src/Existing.md");

        // Payload with two References: one resolvable, one orphan (Deleted.md)
        var package = BuildIncomingPackageWithCommitChild(
            receiverVault.Id,
            commitSha: "abc5555",
            senderCommitKnowledgeId: Guid.NewGuid(),
            filePathTargets: new[] { "src/Existing.md", "src/Deleted.md" });

        await RunImportAsync(receiverVault.Id, package);

        var commitChild = _db.KnowledgeItems.Single(
            k => k.Source == BuildCommitSource("abc5555") && k.Type == KnowledgeType.Commit);

        // Only one References row for the resolvable target
        var references = _db.KnowledgeRelationships.Where(
            r => r.SourceKnowledgeId == commitChild.Id
                && r.RelationshipType == KnowledgeRelationshipType.References).ToList();
        Assert.Single(references);
        Assert.Equal(existingFile.Id, references[0].TargetKnowledgeId);

        // Orphan path captured in child's PlatformData.unlinkedFiles
        Assert.False(string.IsNullOrEmpty(commitChild.PlatformData));
        using var doc = JsonDocument.Parse(commitChild.PlatformData!);
        Assert.True(doc.RootElement.TryGetProperty("unlinkedFiles", out var unlinked));
        var orphans = unlinked.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("src/Deleted.md", orphans);
    }

    // ─── VERIFY #4: Backwards-compat on unknown / absent field ───────────────

    [Fact]
    public void Deserializer_HandlesPayloadWithoutRelationshipsField()
    {
        // An older producer emits PortableKnowledge WITHOUT the relationships field.
        // Tolerance is proven by deserializing a JSON document that has no
        // "relationships" property: the field is nullable so should land as null.
        const string legacyJson = """
            {
              "Id": "11111111-1111-1111-1111-111111111111",
              "Title": "Legacy",
              "Content": "x",
              "Type": 0,
              "CreatedAt": "2026-04-10T00:00:00Z",
              "UpdatedAt": "2026-04-10T00:00:00Z"
            }
            """;

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var knowledge = JsonSerializer.Deserialize<PortableKnowledge>(legacyJson, opts);

        Assert.NotNull(knowledge);
        Assert.Equal("Legacy", knowledge!.Title);
        Assert.Null(knowledge.Relationships); // absent field → null, no exception
    }

    [Fact]
    public async Task Import_BackwardsCompatible_PackageWithoutRelationshipsField()
    {
        var receiverVault = SeedVault();
        SeedKnowledge(
            receiverVault.Id,
            KnowledgeType.CommitHistory,
            "Commit history",
            source: BuildCommitHistorySource());

        // Build a package whose commit-child PortableKnowledge has Relationships == null
        // (simulating an old producer). Import must succeed and produce the knowledge row.
        var package = new PortableExportPackage
        {
            SourceEdition = "platform",
            SourceTenantId = Guid.NewGuid(),
            ExportedAt = DateTime.UtcNow,
            Metadata = new PortableExportMetadata { TotalKnowledgeItems = 1 },
            Data = new PortableExportData
            {
                KnowledgeItems = new List<PortableKnowledge>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Title = "Commit abc9999",
                        Content = "...",
                        Type = KnowledgeType.Commit,
                        Source = BuildCommitSource("abc9999"),
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                        VaultIds = new List<Guid> { receiverVault.Id },
                        PrimaryVaultId = receiverVault.Id,
                        Relationships = null // legacy payload
                    }
                }
            }
        };

        await RunImportAsync(receiverVault.Id, package);

        var child = _db.KnowledgeItems.Single(
            k => k.Source == BuildCommitSource("abc9999"));
        Assert.NotNull(child);
        // No relationships created for a legacy payload — and crucially, no exception.
        Assert.Empty(_db.KnowledgeRelationships.Where(
            r => r.SourceKnowledgeId == child.Id
                && r.RelationshipType == KnowledgeRelationshipType.References));
    }

    // ─── VERIFY #5: Duplicate prevention via unique source/target dedup ──────

    [Fact]
    public async Task Import_ResyncSameCommit_DoesNotProduceDuplicateRelationships()
    {
        var receiverVault = SeedVault();
        SeedKnowledge(
            receiverVault.Id,
            KnowledgeType.CommitHistory,
            "Commit history",
            source: BuildCommitHistorySource());
        SeedKnowledge(
            receiverVault.Id, KnowledgeType.Document, "Target.md",
            filePath: "src/Target.md");

        var package = BuildIncomingPackageWithCommitChild(
            receiverVault.Id,
            commitSha: "dup1111",
            senderCommitKnowledgeId: Guid.NewGuid(),
            filePathTargets: new[] { "src/Target.md" });

        // First import: creates 1 References row
        await RunImportAsync(receiverVault.Id, package);

        // Second import of the same payload: must still be exactly 1 row
        await RunImportAsync(receiverVault.Id, package);

        var commitChild = _db.KnowledgeItems.Single(
            k => k.Source == BuildCommitSource("dup1111"));
        var references = _db.KnowledgeRelationships.Where(
            r => r.SourceKnowledgeId == commitChild.Id
                && r.RelationshipType == KnowledgeRelationshipType.References).ToList();
        Assert.Single(references);
    }

    // ─── Helpers for incoming-payload construction + import ─────────────────

    private static PortableExportPackage BuildIncomingPackageWithCommitChild(
        Guid receiverVaultId,
        string commitSha,
        Guid senderCommitKnowledgeId,
        string[] filePathTargets)
    {
        var relationships = new List<PortableKnowledgeRelationship>
        {
            // PartOf → CommitHistory parent (receiver resolves its own parent via Source string)
            new()
            {
                SourceCommitSha = commitSha,
                TargetFilePath = null, // parent resolved via Source string, not FilePath
                RelationshipType = KnowledgeRelationshipType.PartOf
            }
        };
        foreach (var path in filePathTargets)
        {
            relationships.Add(new PortableKnowledgeRelationship
            {
                SourceCommitSha = commitSha,
                TargetFilePath = path,
                RelationshipType = KnowledgeRelationshipType.References
            });
        }

        return new PortableExportPackage
        {
            SourceEdition = "platform",
            SourceTenantId = Guid.NewGuid(),
            ExportedAt = DateTime.UtcNow,
            Metadata = new PortableExportMetadata { TotalKnowledgeItems = 1 },
            Data = new PortableExportData
            {
                KnowledgeItems = new List<PortableKnowledge>
                {
                    new()
                    {
                        Id = senderCommitKnowledgeId,
                        Title = $"Commit {commitSha[..7]}",
                        Content = "elaborated content",
                        Type = KnowledgeType.Commit,
                        Source = $"{RepoUrl}:{Branch}:commit:{commitSha}",
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                        VaultIds = new List<Guid> { receiverVaultId },
                        PrimaryVaultId = receiverVaultId,
                        Relationships = relationships
                    }
                }
            }
        };
    }

    private async Task RunImportAsync(Guid vaultId, PortableExportPackage package)
    {
        // Exercise the vault-import path via the TestOnly entry point. FileSync is
        // not touched by ImportSyncDeltaLocallyAsync so we pass null! to avoid
        // wiring up the full file storage pipeline.
        var platformClient = Substitute.For<IPlatformSyncClient>();
        var orchestrator = new VaultSyncOrchestrator(
            _db,
            _tenantProvider,
            platformClient,
            _exportService,
            fileSyncService: null!,
            NullLogger<VaultSyncOrchestrator>.Instance);

        var result = await orchestrator.TestOnly_ImportSyncDeltaLocallyAsync(
            package, vaultId, CancellationToken.None);
        Assert.True(result.Success, $"Import failed: {result.Error}");
    }
}
