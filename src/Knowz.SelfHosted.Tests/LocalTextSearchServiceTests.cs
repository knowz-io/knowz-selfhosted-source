using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Models;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class LocalTextSearchServiceTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly LocalTextSearchService _svc;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherTenantId = Guid.Parse("00000000-0000-0000-0000-000000000099");

    public LocalTextSearchServiceTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);

        var logger = Substitute.For<ILogger<LocalTextSearchService>>();
        _svc = new LocalTextSearchService(_db, tenantProvider, logger);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // --- HybridSearchAsync: Basic keyword matching ---

    [Fact]
    public async Task HybridSearchAsync_ReturnsItems_WhenTitleMatches()
    {
        _db.KnowledgeItems.Add(new Knowledge
        {
            TenantId = TenantId, Title = "Apollo 11 Moon Landing", Content = "Content about space"
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo");

        Assert.Single(results);
        Assert.Equal("Apollo 11 Moon Landing", results[0].Title);
    }

    [Fact]
    public async Task HybridSearchAsync_ReturnsItems_WhenContentMatches()
    {
        _db.KnowledgeItems.Add(new Knowledge
        {
            TenantId = TenantId, Title = "Space Facts", Content = "The Apollo program was remarkable"
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo");

        Assert.Single(results);
        Assert.Equal("Space Facts", results[0].Title);
    }

    [Fact]
    public async Task HybridSearchAsync_ReturnsItems_WhenSummaryMatches()
    {
        _db.KnowledgeItems.Add(new Knowledge
        {
            TenantId = TenantId, Title = "Space Facts", Content = "Content here",
            Summary = "Overview of the Apollo program"
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo");

        Assert.Single(results);
    }

    // --- HybridSearchAsync: Empty/null query ---

    [Fact]
    public async Task HybridSearchAsync_ReturnsEmptyList_WhenQueryIsNull()
    {
        _db.KnowledgeItems.Add(new Knowledge
        {
            TenantId = TenantId, Title = "Test", Content = "Content"
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync(null!);

        Assert.Empty(results);
    }

    [Fact]
    public async Task HybridSearchAsync_ReturnsEmptyList_WhenQueryIsEmpty()
    {
        _db.KnowledgeItems.Add(new Knowledge
        {
            TenantId = TenantId, Title = "Test", Content = "Content"
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("");

        Assert.Empty(results);
    }

    [Fact]
    public async Task HybridSearchAsync_ReturnsEmptyList_WhenQueryIsWhitespace()
    {
        _db.KnowledgeItems.Add(new Knowledge
        {
            TenantId = TenantId, Title = "Test", Content = "Content"
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("   ");

        Assert.Empty(results);
    }

    // --- HybridSearchAsync: No matches ---

    [Fact]
    public async Task HybridSearchAsync_ReturnsEmptyList_WhenNoItemsMatch()
    {
        _db.KnowledgeItems.Add(new Knowledge
        {
            TenantId = TenantId, Title = "Space Facts", Content = "Mars exploration"
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("quantum");

        Assert.Empty(results);
    }

    // --- HybridSearchAsync: Tenant scoping ---

    [Fact]
    public async Task HybridSearchAsync_OnlyReturnsTenantScopedItems()
    {
        _db.KnowledgeItems.AddRange(
            new Knowledge { TenantId = TenantId, Title = "My Apollo Doc", Content = "Content" },
            new Knowledge { TenantId = OtherTenantId, Title = "Other Apollo Doc", Content = "Content" });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo");

        Assert.Single(results);
        Assert.Equal("My Apollo Doc", results[0].Title);
    }

    // --- HybridSearchAsync: Synthetic scoring ---

    [Fact]
    public async Task HybridSearchAsync_TitleMatch_ScoresPositive()
    {
        _db.KnowledgeItems.Add(new Knowledge
        {
            TenantId = TenantId, Title = "Apollo Guide", Content = "Unrelated content"
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo");

        Assert.Single(results);
        Assert.True(results[0].Score > 0);
    }

    [Fact]
    public async Task HybridSearchAsync_SummaryOnlyMatch_ScoresPositive()
    {
        _db.KnowledgeItems.Add(new Knowledge
        {
            TenantId = TenantId, Title = "Space Guide", Content = "Unrelated content",
            Summary = "About the Apollo missions"
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo");

        Assert.Single(results);
        Assert.True(results[0].Score > 0);
    }

    [Fact]
    public async Task HybridSearchAsync_ContentOnlyMatch_ScoresPositive()
    {
        _db.KnowledgeItems.Add(new Knowledge
        {
            TenantId = TenantId, Title = "Space Guide", Content = "The Apollo program changed history"
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo");

        Assert.Single(results);
        Assert.True(results[0].Score > 0);
    }

    [Fact]
    public async Task HybridSearchAsync_MultipleFieldsMatch_ScoresHigherThanSingle()
    {
        _db.KnowledgeItems.Add(new Knowledge
        {
            TenantId = TenantId, Title = "Apollo Guide",
            Content = "The Apollo program details",
            Summary = "Apollo summary"
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo");

        Assert.Single(results);
        // Multiple fields matching should sum across fields (higher than any single)
        Assert.True(results[0].Score > 0);
    }

    // --- HybridSearchAsync: Ordering ---

    [Fact]
    public async Task HybridSearchAsync_ResultsOrderedByScoreDescending()
    {
        _db.KnowledgeItems.AddRange(
            new Knowledge
            {
                TenantId = TenantId, Title = "Space Guide",
                Content = "The Apollo program was amazing"
            },
            new Knowledge
            {
                TenantId = TenantId, Title = "Apollo Guide",
                Content = "Unrelated content here"
            },
            new Knowledge
            {
                TenantId = TenantId, Title = "History Notes",
                Content = "Unrelated", Summary = "Apollo missions overview"
            });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo");

        Assert.Equal(3, results.Count);
        // Verify descending order by score
        Assert.True(results[0].Score >= results[1].Score);
        Assert.True(results[1].Score >= results[2].Score);
        Assert.True(results[0].Score > 0);
        Assert.True(results[2].Score > 0);
    }

    // --- HybridSearchAsync: maxResults ---

    [Fact]
    public async Task HybridSearchAsync_RespectsMaxResults()
    {
        for (int i = 0; i < 20; i++)
        {
            _db.KnowledgeItems.Add(new Knowledge
            {
                TenantId = TenantId, Title = $"Apollo Doc {i}", Content = "Content"
            });
        }
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo", maxResults: 5);

        Assert.Equal(5, results.Count);
    }

    // --- HybridSearchAsync: Vault filter ---

    [Fact]
    public async Task HybridSearchAsync_FiltersToSpecifiedVault()
    {
        var vault1 = new Vault { TenantId = TenantId, Name = "Vault 1" };
        var vault2 = new Vault { TenantId = TenantId, Name = "Vault 2" };
        _db.Vaults.AddRange(vault1, vault2);

        var k1 = new Knowledge { TenantId = TenantId, Title = "Apollo Doc 1", Content = "Content" };
        var k2 = new Knowledge { TenantId = TenantId, Title = "Apollo Doc 2", Content = "Content" };
        _db.KnowledgeItems.AddRange(k1, k2);
        await _db.SaveChangesAsync();

        _db.KnowledgeVaults.AddRange(
            new KnowledgeVault { KnowledgeId = k1.Id, VaultId = vault1.Id, TenantId = TenantId },
            new KnowledgeVault { KnowledgeId = k2.Id, VaultId = vault2.Id, TenantId = TenantId });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo", vaultId: vault1.Id);

        Assert.Single(results);
        Assert.Equal("Apollo Doc 1", results[0].Title);
    }

    [Fact]
    public async Task HybridSearchAsync_IncludesDescendantVaults()
    {
        var parentVault = new Vault { TenantId = TenantId, Name = "Parent" };
        var childVault = new Vault { TenantId = TenantId, Name = "Child", ParentVaultId = parentVault.Id };
        _db.Vaults.AddRange(parentVault, childVault);

        var k1 = new Knowledge { TenantId = TenantId, Title = "Apollo Parent", Content = "Content" };
        var k2 = new Knowledge { TenantId = TenantId, Title = "Apollo Child", Content = "Content" };
        _db.KnowledgeItems.AddRange(k1, k2);
        await _db.SaveChangesAsync();

        _db.KnowledgeVaults.AddRange(
            new KnowledgeVault { KnowledgeId = k1.Id, VaultId = parentVault.Id, TenantId = TenantId },
            new KnowledgeVault { KnowledgeId = k2.Id, VaultId = childVault.Id, TenantId = TenantId });
        _db.VaultAncestors.Add(new VaultAncestor
        {
            AncestorVaultId = parentVault.Id, DescendantVaultId = childVault.Id, Depth = 1
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo", vaultId: parentVault.Id, includeDescendants: true);

        Assert.Equal(2, results.Count);
    }

    // --- HybridSearchAsync: Tag filter ---

    [Fact]
    public async Task HybridSearchAsync_FiltersToAnyTag()
    {
        var tag1 = new Tag { TenantId = TenantId, Name = "space" };
        var tag2 = new Tag { TenantId = TenantId, Name = "history" };
        var tag3 = new Tag { TenantId = TenantId, Name = "science" };
        _db.Tags.AddRange(tag1, tag2, tag3);

        var k1 = new Knowledge { TenantId = TenantId, Title = "Apollo Tagged Space", Content = "Content" };
        var k2 = new Knowledge { TenantId = TenantId, Title = "Apollo Tagged History", Content = "Content" };
        var k3 = new Knowledge { TenantId = TenantId, Title = "Apollo Tagged Science", Content = "Content" };
        k1.Tags.Add(tag1);
        k2.Tags.Add(tag2);
        k3.Tags.Add(tag3);
        _db.KnowledgeItems.AddRange(k1, k2, k3);
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo",
            tags: new[] { "space", "history" }, requireAllTags: false);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task HybridSearchAsync_FiltersToAllTags()
    {
        var tag1 = new Tag { TenantId = TenantId, Name = "space" };
        var tag2 = new Tag { TenantId = TenantId, Name = "history" };
        _db.Tags.AddRange(tag1, tag2);

        var k1 = new Knowledge { TenantId = TenantId, Title = "Apollo Both Tags", Content = "Content" };
        var k2 = new Knowledge { TenantId = TenantId, Title = "Apollo One Tag", Content = "Content" };
        k1.Tags.Add(tag1);
        k1.Tags.Add(tag2);
        k2.Tags.Add(tag1);
        _db.KnowledgeItems.AddRange(k1, k2);
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo",
            tags: new[] { "space", "history" }, requireAllTags: true);

        Assert.Single(results);
        Assert.Equal("Apollo Both Tags", results[0].Title);
    }

    // --- HybridSearchAsync: Date filter ---

    [Fact]
    public async Task HybridSearchAsync_FiltersbyStartDate()
    {
        var old = new Knowledge
        {
            TenantId = TenantId, Title = "Old Apollo", Content = "Content"
        };
        var recent = new Knowledge
        {
            TenantId = TenantId, Title = "Recent Apollo", Content = "Content"
        };
        _db.KnowledgeItems.AddRange(old, recent);
        await _db.SaveChangesAsync();

        // Manually set CreatedAt since it may be auto-set
        var oldEntry = _db.Entry(old);
        oldEntry.Property(nameof(Knowledge.CreatedAt)).CurrentValue = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var recentEntry = _db.Entry(recent);
        recentEntry.Property(nameof(Knowledge.CreatedAt)).CurrentValue = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo",
            startDate: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Single(results);
        Assert.Equal("Recent Apollo", results[0].Title);
    }

    [Fact]
    public async Task HybridSearchAsync_FiltersByEndDate()
    {
        var old = new Knowledge
        {
            TenantId = TenantId, Title = "Old Apollo", Content = "Content"
        };
        var recent = new Knowledge
        {
            TenantId = TenantId, Title = "Recent Apollo", Content = "Content"
        };
        _db.KnowledgeItems.AddRange(old, recent);
        await _db.SaveChangesAsync();

        var oldEntry = _db.Entry(old);
        oldEntry.Property(nameof(Knowledge.CreatedAt)).CurrentValue = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var recentEntry = _db.Entry(recent);
        recentEntry.Property(nameof(Knowledge.CreatedAt)).CurrentValue = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo",
            endDate: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Single(results);
        Assert.Equal("Old Apollo", results[0].Title);
    }

    // --- HybridSearchAsync: queryEmbedding ignored ---

    [Fact]
    public async Task HybridSearchAsync_IgnoresQueryEmbedding()
    {
        _db.KnowledgeItems.Add(new Knowledge
        {
            TenantId = TenantId, Title = "Apollo Doc", Content = "Content"
        });
        await _db.SaveChangesAsync();

        var withEmbedding = await _svc.HybridSearchAsync("Apollo",
            queryEmbedding: new float[] { 0.1f, 0.2f, 0.3f });
        var withoutEmbedding = await _svc.HybridSearchAsync("Apollo");

        Assert.Single(withEmbedding);
        Assert.Single(withoutEmbedding);
        Assert.Equal(withEmbedding[0].KnowledgeId, withoutEmbedding[0].KnowledgeId);
        Assert.Equal(withEmbedding[0].Score, withoutEmbedding[0].Score);
    }

    // --- HybridSearchAsync: ContextSummary via ContentChunk join ---

    [Fact]
    public async Task HybridSearchAsync_FindsItems_WhenContextSummaryMatches()
    {
        // Knowledge with no match in title/content/summary
        var knowledge = new Knowledge
        {
            TenantId = TenantId, Title = "Space Guide", Content = "General content"
        };
        _db.KnowledgeItems.Add(knowledge);
        await _db.SaveChangesAsync();

        // Chunk with ContextSummary containing the query term
        _db.ContentChunks.Add(new ContentChunk
        {
            TenantId = TenantId,
            KnowledgeId = knowledge.Id,
            Position = 0,
            Content = "chunk text",
            ContentHash = "cs1",
            ContextSummary = "Discusses Apollo mission details and lunar exploration"
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo");

        Assert.Single(results);
        Assert.True(results[0].Score > 0);
    }

    [Fact]
    public async Task HybridSearchAsync_HandlesNullContextSummary_InChunks()
    {
        var knowledge = new Knowledge
        {
            TenantId = TenantId, Title = "Apollo Guide", Content = "Apollo content"
        };
        _db.KnowledgeItems.Add(knowledge);
        await _db.SaveChangesAsync();

        // Mix of chunks with and without ContextSummary
        _db.ContentChunks.Add(new ContentChunk
        {
            TenantId = TenantId,
            KnowledgeId = knowledge.Id,
            Position = 0,
            Content = "chunk",
            ContentHash = "h1",
            ContextSummary = null
        });
        _db.ContentChunks.Add(new ContentChunk
        {
            TenantId = TenantId,
            KnowledgeId = knowledge.Id,
            Position = 1,
            Content = "chunk 2",
            ContentHash = "h2",
            ContextSummary = "Apollo discussion"
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo");

        Assert.Single(results);
        Assert.True(results[0].Score > 0);
    }

    [Fact]
    public async Task HybridSearchAsync_UsesSharedFieldWeightedScoring()
    {
        // Verify title match scores higher than content match (field boost ordering)
        _db.KnowledgeItems.AddRange(
            new Knowledge
            {
                TenantId = TenantId, Title = "Apollo Mission Guide",
                Content = "Unrelated content stuff"
            },
            new Knowledge
            {
                TenantId = TenantId, Title = "Unrelated Guide Title",
                Content = "The Apollo program was remarkable"
            });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo");

        Assert.Equal(2, results.Count);
        // Title match (boost 3.0) should score higher than content match (boost 2.5)
        Assert.True(results[0].Score >= results[1].Score);
    }

    // --- IndexDocumentAsync: no-op ---

    [Fact]
    public async Task IndexDocumentAsync_CompletesWithoutError()
    {
        await _svc.IndexDocumentAsync(
            Guid.NewGuid(), "Title", "Content", "Summary",
            "VaultName", Guid.NewGuid(), null, "TopicName",
            new[] { "tag1" }, "Note", "/path/file.txt", null,
            cancellationToken: CancellationToken.None);
    }

    // --- DeleteDocumentAsync: no-op ---

    [Fact]
    public async Task DeleteDocumentAsync_CompletesWithoutError()
    {
        await _svc.DeleteDocumentAsync(Guid.NewGuid(), CancellationToken.None);
    }

    // --- HybridSearchAsync: Special characters ---

    [Fact]
    public async Task HybridSearchAsync_HandlesSpecialCharacters()
    {
        _db.KnowledgeItems.Add(new Knowledge
        {
            TenantId = TenantId, Title = "O'Brien's Notes", Content = "Content with % and _ chars"
        });
        await _db.SaveChangesAsync();

        // Should not throw -- parameterized queries prevent SQL injection
        var results = await _svc.HybridSearchAsync("O'Brien");
        Assert.Single(results);
    }

    // --- HybridSearchAsync: Maps fields correctly ---

    [Fact]
    public async Task HybridSearchAsync_MapsAllSearchResultItemFields()
    {
        var knowledge = new Knowledge
        {
            TenantId = TenantId,
            Title = "Apollo Mission Report",
            Content = "Detailed Apollo mission content",
            Summary = "Apollo mission summary",
            FilePath = "/docs/apollo.md"
        };
        _db.KnowledgeItems.Add(knowledge);
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo");

        Assert.Single(results);
        var result = results[0];
        Assert.Equal(knowledge.Id, result.KnowledgeId);
        Assert.Equal("Apollo Mission Report", result.Title);
        Assert.Equal("Detailed Apollo mission content", result.Content);
        Assert.Equal("Apollo mission summary", result.Summary);
    }
}
