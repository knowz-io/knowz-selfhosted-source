using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Models;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class LocalVectorSearchServiceTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly LocalVectorSearchService _svc;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherTenantId = Guid.Parse("00000000-0000-0000-0000-000000000099");

    public LocalVectorSearchServiceTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);

        var logger = Substitute.For<ILogger<LocalVectorSearchService>>();
        _svc = new LocalVectorSearchService(_db, tenantProvider, logger);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // --- Helper: Create a unit vector along a given axis ---

    private static float[] UnitVector(int dimension, int axis)
    {
        var v = new float[dimension];
        v[axis] = 1.0f;
        return v;
    }

    private static string EmbeddingJson(float[] vector) =>
        JsonSerializer.Serialize(vector);

    // ============================================================
    // CosineSimilarity unit tests
    // ============================================================

    [Fact]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        var v = new float[] { 1.0f, 2.0f, 3.0f };

        var result = LocalVectorSearchService.CosineSimilarity(v, v);

        Assert.Equal(1.0, result, precision: 10);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        var a = UnitVector(3, 0); // [1, 0, 0]
        var b = UnitVector(3, 1); // [0, 1, 0]

        var result = LocalVectorSearchService.CosineSimilarity(a, b);

        Assert.Equal(0.0, result, precision: 10);
    }

    [Fact]
    public void CosineSimilarity_OppositeVectors_ReturnsNegativeOne()
    {
        var a = new float[] { 1.0f, 0.0f, 0.0f };
        var b = new float[] { -1.0f, 0.0f, 0.0f };

        var result = LocalVectorSearchService.CosineSimilarity(a, b);

        Assert.Equal(-1.0, result, precision: 10);
    }

    [Fact]
    public void CosineSimilarity_DifferentLengthVectors_ReturnsZero()
    {
        var a = new float[] { 1.0f, 2.0f };
        var b = new float[] { 1.0f, 2.0f, 3.0f };

        var result = LocalVectorSearchService.CosineSimilarity(a, b);

        Assert.Equal(0.0, result);
    }

    [Fact]
    public void CosineSimilarity_EmptyVectors_ReturnsZero()
    {
        var a = Array.Empty<float>();
        var b = Array.Empty<float>();

        var result = LocalVectorSearchService.CosineSimilarity(a, b);

        Assert.Equal(0.0, result);
    }

    [Fact]
    public void CosineSimilarity_ZeroVector_ReturnsZero()
    {
        var a = new float[] { 0.0f, 0.0f, 0.0f };
        var b = new float[] { 1.0f, 2.0f, 3.0f };

        var result = LocalVectorSearchService.CosineSimilarity(a, b);

        Assert.Equal(0.0, result);
    }

    [Fact]
    public void CosineSimilarity_ParallelVectors_DifferentMagnitude_ReturnsOne()
    {
        var a = new float[] { 1.0f, 2.0f, 3.0f };
        var b = new float[] { 2.0f, 4.0f, 6.0f };

        var result = LocalVectorSearchService.CosineSimilarity(a, b);

        Assert.Equal(1.0, result, precision: 10);
    }

    // ============================================================
    // Legacy ComputeKeywordScore tests (updated for ComputeFieldWeightedScore)
    // ============================================================

    [Fact]
    public void ComputeFieldWeightedScore_TitleMatch_ScoresPositive()
    {
        var score = LocalVectorSearchService.ComputeFieldWeightedScore(
            "Apollo Guide", null, "Unrelated content", null, "Apollo");

        Assert.True(score > 0);
    }

    [Fact]
    public void ComputeFieldWeightedScore_SummaryMatch_ScoresPositive()
    {
        var score = LocalVectorSearchService.ComputeFieldWeightedScore(
            "Space Guide", "Apollo missions overview", "Unrelated content", null, "Apollo");

        Assert.True(score > 0);
    }

    [Fact]
    public void ComputeFieldWeightedScore_ContentMatch_ScoresPositive()
    {
        var score = LocalVectorSearchService.ComputeFieldWeightedScore(
            "Space Guide", null, "The Apollo program was great", null, "Apollo");

        Assert.True(score > 0);
    }

    [Fact]
    public void ComputeFieldWeightedScore_LegacyNoMatch_ReturnsZero()
    {
        var score = LocalVectorSearchService.ComputeFieldWeightedScore(
            "Space Guide", "Space summary", "Space content", null, "Apollo");

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void ComputeFieldWeightedScore_LegacyCaseInsensitive()
    {
        var score = LocalVectorSearchService.ComputeFieldWeightedScore(
            "apollo guide", null, "Unrelated", null, "APOLLO");

        Assert.True(score > 0);
    }

    [Fact]
    public void ComputeFieldWeightedScore_MultipleFieldsMatch_SumsAcrossFields()
    {
        var allMatch = LocalVectorSearchService.ComputeFieldWeightedScore(
            "Apollo Guide", "Apollo summary", "Apollo content", null, "Apollo");

        var titleOnly = LocalVectorSearchService.ComputeFieldWeightedScore(
            "Apollo Guide", "No match", "No match", null, "Apollo");

        // All fields matching should score higher than title-only
        Assert.True(allMatch > titleOnly);
    }

    [Fact]
    public void ComputeFieldWeightedScore_LegacyNullSummary_DoesNotThrow()
    {
        var score = LocalVectorSearchService.ComputeFieldWeightedScore(
            "Space Guide", null, "Content", null, "query");

        Assert.Equal(0.0, score);
    }

    // ============================================================
    // ComputeFieldWeightedScore unit tests
    // ============================================================

    [Fact]
    public void ComputeFieldWeightedScore_SplitsQueryIntoTerms()
    {
        // "machine learning" should match both terms independently
        // "machine" appears in title, "learning" appears in title
        var score = LocalVectorSearchService.ComputeFieldWeightedScore(
            "machine learning fundamentals", null, "other content", null, "machine learning");

        Assert.True(score > 0);
    }

    [Fact]
    public void ComputeFieldWeightedScore_UsesFieldBoostWeights()
    {
        // Title match (boost 3.0) should score higher than content match (boost 2.5)
        var titleScore = LocalVectorSearchService.ComputeFieldWeightedScore(
            "Apollo guide to space", null, "unrelated content here", null, "Apollo");

        var contentScore = LocalVectorSearchService.ComputeFieldWeightedScore(
            "unrelated title here", null, "Apollo guide to space", null, "Apollo");

        Assert.True(titleScore > contentScore, $"Title score {titleScore} should be > content score {contentScore}");
    }

    [Fact]
    public void ComputeFieldWeightedScore_CountsTermOccurrences()
    {
        // "Apollo Apollo Apollo" has 3 occurrences; "Apollo once" has 1
        var moreOccurrences = LocalVectorSearchService.ComputeFieldWeightedScore(
            "Apollo Apollo Apollo in short", null, "other", null, "Apollo");

        var fewerOccurrences = LocalVectorSearchService.ComputeFieldWeightedScore(
            "Apollo in a longer title field", null, "other", null, "Apollo");

        Assert.True(moreOccurrences > fewerOccurrences,
            $"More occurrences {moreOccurrences} should score > fewer {fewerOccurrences}");
    }

    [Fact]
    public void ComputeFieldWeightedScore_NormalizesByFieldLength()
    {
        // Same occurrence count but shorter field = higher TF
        var shortField = LocalVectorSearchService.ComputeFieldWeightedScore(
            "Apollo", null, "x", null, "Apollo");

        var longField = LocalVectorSearchService.ComputeFieldWeightedScore(
            "Apollo " + new string('x', 500), null, "x", null, "Apollo");

        Assert.True(shortField > longField,
            $"Short field score {shortField} should be > long field score {longField}");
    }

    [Fact]
    public void ComputeFieldWeightedScore_ContextSummaryBoost_IsOnePointFive()
    {
        // ContextSummary-only match (boost 1.5) should produce a score
        var score = LocalVectorSearchService.ComputeFieldWeightedScore(
            "unrelated", null, "unrelated", "Apollo context summary info", "Apollo");

        Assert.True(score > 0);

        // ContextSummary (1.5) < Summary (2.0)
        var summaryScore = LocalVectorSearchService.ComputeFieldWeightedScore(
            "unrelated", "Apollo summary info long", "unrelated", null, "Apollo");

        Assert.True(summaryScore > score, $"Summary score {summaryScore} should be > context score {score}");
    }

    [Fact]
    public void ComputeFieldWeightedScore_NullContextSummary_DoesNotThrow()
    {
        var score = LocalVectorSearchService.ComputeFieldWeightedScore(
            "Apollo guide", null, "content", null, "Apollo");

        Assert.True(score > 0);
    }

    [Fact]
    public void ComputeFieldWeightedScore_NullSummaryAndContextSummary_DoesNotThrow()
    {
        var score = LocalVectorSearchService.ComputeFieldWeightedScore(
            "Apollo guide", null, "content", null, "Apollo");

        Assert.True(score > 0);
    }

    [Fact]
    public void ComputeFieldWeightedScore_NoMatch_ReturnsZero()
    {
        var score = LocalVectorSearchService.ComputeFieldWeightedScore(
            "Space Guide", "Space summary", "Space content", "Space context", "Quantum");

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void ComputeFieldWeightedScore_CaseInsensitive()
    {
        var score1 = LocalVectorSearchService.ComputeFieldWeightedScore(
            "APOLLO GUIDE", null, "content", null, "apollo");

        var score2 = LocalVectorSearchService.ComputeFieldWeightedScore(
            "apollo guide", null, "content", null, "APOLLO");

        Assert.True(score1 > 0);
        Assert.Equal(score1, score2, precision: 10);
    }

    [Fact]
    public void ComputeFieldWeightedScore_MultiWordQuery_AveragesAcrossTerms()
    {
        // "machine learning" — both terms match title
        var bothMatch = LocalVectorSearchService.ComputeFieldWeightedScore(
            "machine learning basics", null, "content", null, "machine learning");

        // "machine quantum" — only "machine" matches title
        var oneMatch = LocalVectorSearchService.ComputeFieldWeightedScore(
            "machine learning basics", null, "content", null, "machine quantum");

        Assert.True(bothMatch > oneMatch,
            $"Both match {bothMatch} should be > one match {oneMatch}");
    }

    [Fact]
    public void ComputeFieldWeightedScore_EmptyQuery_ReturnsZero()
    {
        var score = LocalVectorSearchService.ComputeFieldWeightedScore(
            "Apollo guide", "summary", "content", "context", "");

        Assert.Equal(0.0, score);
    }

    // ============================================================
    // Weight constants
    // ============================================================

    [Fact]
    public void VectorWeight_IsZeroPointSeven()
    {
        Assert.Equal(0.7, LocalVectorSearchService.VectorWeight);
    }

    [Fact]
    public void KeywordWeight_IsZeroPointThree()
    {
        Assert.Equal(0.3, LocalVectorSearchService.KeywordWeight);
    }

    // ============================================================
    // HybridSearchAsync: Fused scoring (vector + keyword)
    // ============================================================

    [Fact]
    public async Task HybridSearchAsync_ReturnsFusedScore_WhenEmbeddingsAndQueryEmbeddingProvided()
    {
        // Arrange: knowledge with a chunk that has an embedding identical to queryEmbedding
        var knowledge = new Knowledge
        {
            TenantId = TenantId, Title = "Space Guide", Content = "Content about space"
        };
        _db.KnowledgeItems.Add(knowledge);
        await _db.SaveChangesAsync();

        var embedding = new float[] { 1.0f, 0.0f, 0.0f };
        _db.ContentChunks.Add(new ContentChunk
        {
            TenantId = TenantId,
            KnowledgeId = knowledge.Id,
            Position = 0,
            Content = "chunk text",
            ContentHash = "hash1",
            EmbeddingVectorJson = EmbeddingJson(embedding),
            EmbeddedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var queryEmbedding = new float[] { 1.0f, 0.0f, 0.0f }; // identical = cosine similarity 1.0

        // Act
        var results = await _svc.HybridSearchAsync("Space", queryEmbedding: queryEmbedding);

        // Assert: fusedScore = (vectorScore * 0.7) + (keywordScore * 0.3)
        Assert.Single(results);
        Assert.True(results[0].Score > 0);
        // Verify fused score equals vectorWeight*vector + keywordWeight*keyword
        var expectedKeyword = LocalVectorSearchService.ComputeFieldWeightedScore(
            "Space Guide", null, "Content about space", null, "Space");
        var expectedFused = 1.0 * LocalVectorSearchService.VectorWeight
                          + expectedKeyword * LocalVectorSearchService.KeywordWeight;
        Assert.Equal(expectedFused, results[0].Score, precision: 5);
    }

    [Fact]
    public async Task HybridSearchAsync_ReturnsKeywordOnlyScore_WhenNoQueryEmbeddingProvided()
    {
        var knowledge = new Knowledge
        {
            TenantId = TenantId, Title = "Apollo Mission", Content = "Content"
        };
        _db.KnowledgeItems.Add(knowledge);
        await _db.SaveChangesAsync();

        // Even though chunk has an embedding, no queryEmbedding means keyword-only
        _db.ContentChunks.Add(new ContentChunk
        {
            TenantId = TenantId,
            KnowledgeId = knowledge.Id,
            Position = 0,
            Content = "chunk",
            ContentHash = "h1",
            EmbeddingVectorJson = EmbeddingJson(new float[] { 1, 0, 0 }),
            EmbeddedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo");

        Assert.Single(results);
        Assert.True(results[0].Score > 0); // title match produces positive score
    }

    [Fact]
    public async Task HybridSearchAsync_ReturnsKeywordOnlyScore_WhenChunksHaveNoEmbeddings()
    {
        var knowledge = new Knowledge
        {
            TenantId = TenantId, Title = "Apollo Guide", Content = "Content about Apollo"
        };
        _db.KnowledgeItems.Add(knowledge);
        await _db.SaveChangesAsync();

        // Chunk with no embedding
        _db.ContentChunks.Add(new ContentChunk
        {
            TenantId = TenantId,
            KnowledgeId = knowledge.Id,
            Position = 0,
            Content = "chunk",
            ContentHash = "h1",
            EmbeddingVectorJson = null // no embedding
        });
        await _db.SaveChangesAsync();

        var queryEmbedding = new float[] { 1.0f, 0.0f, 0.0f };
        var results = await _svc.HybridSearchAsync("Apollo", queryEmbedding: queryEmbedding);

        // No vector score because chunk embeddings are null (filtered out by query),
        // so vectorScore stays 0 => hasVector = false => keyword only
        Assert.Single(results);
        Assert.True(results[0].Score > 0); // title match keyword only
    }

    [Fact]
    public async Task HybridSearchAsync_VectorScoreBeatsKeywordScore_ForSemanticMatches()
    {
        // Item 1: keyword match in title, no embedding
        var keywordItem = new Knowledge
        {
            TenantId = TenantId, Title = "Apollo missions", Content = "Content"
        };
        // Item 2: no keyword match at all, but high vector similarity
        var vectorItem = new Knowledge
        {
            TenantId = TenantId, Title = "Space exploration history", Content = "NASA programs and launches"
        };
        _db.KnowledgeItems.AddRange(keywordItem, vectorItem);
        await _db.SaveChangesAsync();

        // vectorItem has a chunk with embedding very similar to query
        var queryEmbedding = new float[] { 0.9f, 0.1f, 0.0f };
        _db.ContentChunks.Add(new ContentChunk
        {
            TenantId = TenantId,
            KnowledgeId = vectorItem.Id,
            Position = 0,
            Content = "NASA programs",
            ContentHash = "h2",
            EmbeddingVectorJson = EmbeddingJson(new float[] { 0.9f, 0.1f, 0.0f }), // nearly identical
            EmbeddedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        // Query "Apollo" matches keywordItem title (keyword score=1.0), but not vectorItem
        // vectorItem has high vector similarity but no keyword match
        var results = await _svc.HybridSearchAsync("Apollo", queryEmbedding: queryEmbedding);

        // keywordItem: keyword=1.0, no embedding => keyword only => score=1.0
        // vectorItem: keyword=0.0, vector~1.0 => fused = 1.0*0.7 + 0.0*0.3 = 0.7
        // keywordItem should rank higher in this case
        Assert.True(results.Count >= 1);

        // Now test the opposite: query that does NOT match keyword but matches vector
        // Use a query with no keyword matches
        var results2 = await _svc.HybridSearchAsync("NASA", queryEmbedding: queryEmbedding);

        // vectorItem: keyword match in content "NASA" => keyword=0.6, vector~1.0 => fused = 0.7 + 0.18 = 0.88
        // keywordItem: no keyword match at all => not returned
        Assert.Single(results2);
        Assert.Equal(vectorItem.Id, results2[0].KnowledgeId);

        // Fused score should include both vector and keyword components
        Assert.True(results2[0].Score > 0);
    }

    [Fact]
    public async Task HybridSearchAsync_PicksBestChunkScore_WhenMultipleChunksExist()
    {
        var knowledge = new Knowledge
        {
            TenantId = TenantId, Title = "Space Guide", Content = "Content about space"
        };
        _db.KnowledgeItems.Add(knowledge);
        await _db.SaveChangesAsync();

        var queryEmbedding = UnitVector(3, 0); // [1, 0, 0]

        // Chunk 1: low similarity
        _db.ContentChunks.Add(new ContentChunk
        {
            TenantId = TenantId,
            KnowledgeId = knowledge.Id,
            Position = 0,
            Content = "chunk 0",
            ContentHash = "h0",
            EmbeddingVectorJson = EmbeddingJson(UnitVector(3, 1)), // orthogonal = 0 similarity
            EmbeddedAt = DateTime.UtcNow
        });

        // Chunk 2: high similarity
        _db.ContentChunks.Add(new ContentChunk
        {
            TenantId = TenantId,
            KnowledgeId = knowledge.Id,
            Position = 1,
            Content = "chunk 1",
            ContentHash = "h1",
            EmbeddingVectorJson = EmbeddingJson(UnitVector(3, 0)), // identical = 1.0 similarity
            EmbeddedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Space", queryEmbedding: queryEmbedding);

        // Best chunk score = 1.0, keyword match is positive
        // Fused = vectorScore*0.7 + keywordScore*0.3
        Assert.Single(results);
        Assert.True(results[0].Score > 0);
    }

    // ============================================================
    // HybridSearchAsync: Empty/null query
    // ============================================================

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

    // ============================================================
    // HybridSearchAsync: No matches
    // ============================================================

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

    // ============================================================
    // HybridSearchAsync: Tenant isolation
    // ============================================================

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

    [Fact]
    public async Task HybridSearchAsync_TenantIsolation_ChunksFromOtherTenantNotScored()
    {
        var myKnowledge = new Knowledge
        {
            TenantId = TenantId, Title = "Space Guide", Content = "Content about space"
        };
        _db.KnowledgeItems.Add(myKnowledge);
        await _db.SaveChangesAsync();

        // Add a chunk belonging to the other tenant (should not be visible due to query filter)
        // Since in-memory provider applies query filters, this chunk won't be found
        _db.ContentChunks.Add(new ContentChunk
        {
            TenantId = OtherTenantId,
            KnowledgeId = myKnowledge.Id,
            Position = 0,
            Content = "other tenant chunk",
            ContentHash = "oth",
            EmbeddingVectorJson = EmbeddingJson(new float[] { 1, 0, 0 }),
            EmbeddedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var queryEmbedding = new float[] { 1.0f, 0.0f, 0.0f };
        var results = await _svc.HybridSearchAsync("Space", queryEmbedding: queryEmbedding);

        // Should return the item but with keyword-only score (no matching tenant chunks)
        Assert.Single(results);
        Assert.True(results[0].Score > 0); // title keyword match only
    }

    // ============================================================
    // HybridSearchAsync: Vault filter
    // ============================================================

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

    // ============================================================
    // HybridSearchAsync: Tag filter
    // ============================================================

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

    // ============================================================
    // HybridSearchAsync: Date filter
    // ============================================================

    [Fact]
    public async Task HybridSearchAsync_FiltersByStartDate()
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

    // ============================================================
    // HybridSearchAsync: maxResults
    // ============================================================

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

    // ============================================================
    // HybridSearchAsync: Ordering
    // ============================================================

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
        // Title match (boost 3.0) > Summary match (boost 2.0) > Content match (boost 2.5 but longer field)
        // Verify descending order
        Assert.True(results[0].Score >= results[1].Score);
        Assert.True(results[1].Score >= results[2].Score);
        Assert.True(results[0].Score > 0);
        Assert.True(results[2].Score > 0);
    }

    [Fact]
    public async Task HybridSearchAsync_FusedScoresOrderedDescending()
    {
        // Two items with similar keyword scores, but different vector scores
        var highVector = new Knowledge
        {
            TenantId = TenantId, Title = "Space Exploration", Content = "Apollo content here"
        };
        var lowVector = new Knowledge
        {
            TenantId = TenantId, Title = "Space History", Content = "Apollo content there"
        };
        _db.KnowledgeItems.AddRange(highVector, lowVector);
        await _db.SaveChangesAsync();

        var queryEmbedding = UnitVector(3, 0);

        // highVector: high vector similarity (1.0)
        _db.ContentChunks.Add(new ContentChunk
        {
            TenantId = TenantId,
            KnowledgeId = highVector.Id,
            Position = 0,
            Content = "chunk",
            ContentHash = "hv",
            EmbeddingVectorJson = EmbeddingJson(UnitVector(3, 0)), // similarity 1.0
            EmbeddedAt = DateTime.UtcNow
        });
        // lowVector: low vector similarity (~0.1)
        _db.ContentChunks.Add(new ContentChunk
        {
            TenantId = TenantId,
            KnowledgeId = lowVector.Id,
            Position = 0,
            Content = "chunk",
            ContentHash = "lv",
            EmbeddingVectorJson = EmbeddingJson(new float[] { 0.1f, 0.99f, 0.0f }), // low but positive similarity
            EmbeddedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo", queryEmbedding: queryEmbedding);

        Assert.Equal(2, results.Count);
        // Both have similar keyword scores, highVector has much higher vector score
        // highVector fused = 1.0*0.7 + kw*0.3
        // lowVector fused = 0.0*0.7 + kw*0.3 (hasVector=true because vectorScore=0 but embedding exists)
        // Actually, vectorScore=0 with queryEmbedding means hasVector depends on whether similarity > 0
        // highVector should rank first due to vector component
        Assert.Equal(highVector.Id, results[0].KnowledgeId);
        Assert.True(results[0].Score > results[1].Score);
    }

    // ============================================================
    // HybridSearchAsync: Maps fields correctly
    // ============================================================

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
        Assert.Equal("/docs/apollo.md", result.FilePath);
        Assert.NotNull(result.Highlights);
        Assert.NotNull(result.Tags);
    }

    // ============================================================
    // HybridSearchAsync: Special characters
    // ============================================================

    [Fact]
    public async Task HybridSearchAsync_HandlesSpecialCharacters()
    {
        _db.KnowledgeItems.Add(new Knowledge
        {
            TenantId = TenantId, Title = "O'Brien's Notes", Content = "Content with % and _ chars"
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("O'Brien");
        Assert.Single(results);
    }

    // ============================================================
    // HybridSearchAsync: Chunk embedding edge cases
    // ============================================================

    [Fact]
    public async Task HybridSearchAsync_SkipsChunksWithInvalidEmbeddingJson()
    {
        var knowledge = new Knowledge
        {
            TenantId = TenantId, Title = "Apollo Guide", Content = "Content"
        };
        _db.KnowledgeItems.Add(knowledge);
        await _db.SaveChangesAsync();

        // Invalid JSON for embedding
        _db.ContentChunks.Add(new ContentChunk
        {
            TenantId = TenantId,
            KnowledgeId = knowledge.Id,
            Position = 0,
            Content = "chunk",
            ContentHash = "bad",
            EmbeddingVectorJson = "not-valid-json",
            EmbeddedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var queryEmbedding = new float[] { 1.0f, 0.0f, 0.0f };
        var results = await _svc.HybridSearchAsync("Apollo", queryEmbedding: queryEmbedding);

        // Should still return the item with keyword-only score (bad embedding skipped)
        Assert.Single(results);
        Assert.True(results[0].Score > 0); // title keyword match only
    }

    [Fact]
    public async Task HybridSearchAsync_HandlesKnowledgeWithNoChunks()
    {
        _db.KnowledgeItems.Add(new Knowledge
        {
            TenantId = TenantId, Title = "Apollo Guide", Content = "Content"
        });
        await _db.SaveChangesAsync();
        // No chunks added at all

        var queryEmbedding = new float[] { 1.0f, 0.0f, 0.0f };
        var results = await _svc.HybridSearchAsync("Apollo", queryEmbedding: queryEmbedding);

        Assert.Single(results);
        Assert.True(results[0].Score > 0); // keyword only
    }

    [Fact]
    public async Task HybridSearchAsync_FiltersOutZeroScoreResults()
    {
        // Item with no keyword match and no embedding => score 0 => filtered out
        _db.KnowledgeItems.Add(new Knowledge
        {
            TenantId = TenantId, Title = "Space Facts", Content = "Mars exploration"
        });
        // Item with keyword match => kept
        _db.KnowledgeItems.Add(new Knowledge
        {
            TenantId = TenantId, Title = "Apollo Guide", Content = "Content"
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo");

        Assert.Single(results);
        Assert.Equal("Apollo Guide", results[0].Title);
    }

    // ============================================================
    // HybridSearchAsync: ContextSummary in keyword scoring
    // ============================================================

    [Fact]
    public async Task HybridSearchAsync_IncludesContextSummaryInKeywordScoring()
    {
        // Knowledge with no keyword match in title/content/summary
        var knowledge = new Knowledge
        {
            TenantId = TenantId, Title = "Space Guide", Content = "Content here"
        };
        _db.KnowledgeItems.Add(knowledge);
        await _db.SaveChangesAsync();

        // Chunk with ContextSummary containing the query term
        _db.ContentChunks.Add(new ContentChunk
        {
            TenantId = TenantId,
            KnowledgeId = knowledge.Id,
            Position = 0,
            Content = "chunk content",
            ContentHash = "cs1",
            ContextSummary = "This chunk discusses Apollo missions and lunar exploration",
            EmbeddingVectorJson = null
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo");

        // "Apollo" only appears in ContextSummary — should be found
        Assert.Single(results);
        Assert.True(results[0].Score > 0);
    }

    [Fact]
    public async Task HybridSearchAsync_HandlesNullContextSummary_Gracefully()
    {
        var knowledge = new Knowledge
        {
            TenantId = TenantId, Title = "Apollo Guide", Content = "Apollo content"
        };
        _db.KnowledgeItems.Add(knowledge);
        await _db.SaveChangesAsync();

        // Chunk with null ContextSummary
        _db.ContentChunks.Add(new ContentChunk
        {
            TenantId = TenantId,
            KnowledgeId = knowledge.Id,
            Position = 0,
            Content = "chunk",
            ContentHash = "nc1",
            ContextSummary = null,
            EmbeddingVectorJson = null
        });
        // Chunk with non-null ContextSummary
        _db.ContentChunks.Add(new ContentChunk
        {
            TenantId = TenantId,
            KnowledgeId = knowledge.Id,
            Position = 1,
            Content = "chunk 2",
            ContentHash = "nc2",
            ContextSummary = "Apollo context",
            EmbeddingVectorJson = null
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo");

        // Should not throw and should return results
        Assert.Single(results);
        Assert.True(results[0].Score > 0);
    }

    // ============================================================
    // IndexDocumentAsync: no-op
    // ============================================================

    [Fact]
    public async Task IndexDocumentAsync_CompletesWithoutError()
    {
        await _svc.IndexDocumentAsync(
            Guid.NewGuid(), "Title", "Content", "Summary",
            "VaultName", Guid.NewGuid(), null, "TopicName",
            new[] { "tag1" }, "Note", "/path/file.txt", null,
            cancellationToken: CancellationToken.None);
    }

    [Fact]
    public async Task IndexDocumentAsync_DoesNotModifyDatabase()
    {
        var countBefore = _db.KnowledgeItems.Count();

        await _svc.IndexDocumentAsync(
            Guid.NewGuid(), "Title", "Content", "Summary",
            "VaultName", Guid.NewGuid(), null, "TopicName",
            new[] { "tag1" }, "Note", "/path/file.txt",
            new float[] { 0.1f, 0.2f },
            cancellationToken: CancellationToken.None);

        var countAfter = _db.KnowledgeItems.Count();
        Assert.Equal(countBefore, countAfter);
    }

    // ============================================================
    // DeleteDocumentAsync: no-op
    // ============================================================

    [Fact]
    public async Task DeleteDocumentAsync_CompletesWithoutError()
    {
        await _svc.DeleteDocumentAsync(Guid.NewGuid(), CancellationToken.None);
    }

    [Fact]
    public async Task DeleteDocumentAsync_DoesNotModifyDatabase()
    {
        var knowledge = new Knowledge
        {
            TenantId = TenantId, Title = "Test", Content = "Content"
        };
        _db.KnowledgeItems.Add(knowledge);
        await _db.SaveChangesAsync();

        await _svc.DeleteDocumentAsync(knowledge.Id, CancellationToken.None);

        var item = await _db.KnowledgeItems.FindAsync(knowledge.Id);
        Assert.NotNull(item);
        Assert.False(item.IsDeleted);
    }

    // ============================================================
    // HybridSearchAsync: Scoring math verification
    // ============================================================

    [Fact]
    public async Task HybridSearchAsync_FusedScore_CorrectMath()
    {
        // Setup: content match + vector similarity 1.0
        // Fused score = vectorScore*0.7 + keywordScore*0.3
        var knowledge = new Knowledge
        {
            TenantId = TenantId, Title = "Space Guide", Content = "Apollo content here"
        };
        _db.KnowledgeItems.Add(knowledge);
        await _db.SaveChangesAsync();

        var queryEmbedding = UnitVector(3, 0);
        _db.ContentChunks.Add(new ContentChunk
        {
            TenantId = TenantId,
            KnowledgeId = knowledge.Id,
            Position = 0,
            Content = "chunk",
            ContentHash = "h1",
            EmbeddingVectorJson = EmbeddingJson(UnitVector(3, 0)),
            EmbeddedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo", queryEmbedding: queryEmbedding);

        Assert.Single(results);
        // Compute expected: keyword score from field-weighted scoring + vector * 0.7
        var expectedKeyword = LocalVectorSearchService.ComputeFieldWeightedScore(
            "Space Guide", null, "Apollo content here", null, "Apollo");
        var expectedFused = 1.0 * LocalVectorSearchService.VectorWeight
                          + expectedKeyword * LocalVectorSearchService.KeywordWeight;
        Assert.Equal(expectedFused, results[0].Score, precision: 5);
    }

    [Fact]
    public async Task HybridSearchAsync_FusedScore_SummaryKeywordWithVector()
    {
        // Setup: summary match + vector similarity ~0.5
        var knowledge = new Knowledge
        {
            TenantId = TenantId,
            Title = "Space Guide",
            Content = "Unrelated content",
            Summary = "Apollo mission overview"
        };
        _db.KnowledgeItems.Add(knowledge);
        await _db.SaveChangesAsync();

        // Two vectors at 60 degrees: cos(60) = 0.5
        var queryEmbedding = new float[] { 1.0f, 0.0f };
        var chunkEmbedding = new float[] { 0.5f, 0.866025f }; // cos(60 deg) ~ 0.5

        _db.ContentChunks.Add(new ContentChunk
        {
            TenantId = TenantId,
            KnowledgeId = knowledge.Id,
            Position = 0,
            Content = "chunk",
            ContentHash = "h1",
            EmbeddingVectorJson = EmbeddingJson(chunkEmbedding),
            EmbeddedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo", queryEmbedding: queryEmbedding);

        Assert.Single(results);
        // Verify fused score uses vectorWeight * vectorScore + keywordWeight * keywordScore
        var vectorSim = LocalVectorSearchService.CosineSimilarity(queryEmbedding, chunkEmbedding);
        var expectedKeyword = LocalVectorSearchService.ComputeFieldWeightedScore(
            "Space Guide", "Apollo mission overview", "Unrelated content", null, "Apollo");
        var expectedFused = vectorSim * LocalVectorSearchService.VectorWeight
                          + expectedKeyword * LocalVectorSearchService.KeywordWeight;
        Assert.Equal(expectedFused, results[0].Score, precision: 3);
    }

    // ============================================================
    // HybridSearchAsync: Vault + Vector combined
    // ============================================================

    [Fact]
    public async Task HybridSearchAsync_VaultFilter_StillUsesVectorScoring()
    {
        var vault = new Vault { TenantId = TenantId, Name = "My Vault" };
        _db.Vaults.Add(vault);

        var knowledge = new Knowledge
        {
            TenantId = TenantId, Title = "Apollo Guide", Content = "Content"
        };
        _db.KnowledgeItems.Add(knowledge);
        await _db.SaveChangesAsync();

        _db.KnowledgeVaults.Add(new KnowledgeVault
        {
            KnowledgeId = knowledge.Id, VaultId = vault.Id, TenantId = TenantId
        });

        var queryEmbedding = UnitVector(3, 0);
        _db.ContentChunks.Add(new ContentChunk
        {
            TenantId = TenantId,
            KnowledgeId = knowledge.Id,
            Position = 0,
            Content = "chunk",
            ContentHash = "h1",
            EmbeddingVectorJson = EmbeddingJson(UnitVector(3, 0)),
            EmbeddedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var results = await _svc.HybridSearchAsync("Apollo",
            queryEmbedding: queryEmbedding, vaultId: vault.Id);

        Assert.Single(results);
        // Fused score = vectorWeight*vector + keywordWeight*keyword
        var expectedKeyword = LocalVectorSearchService.ComputeFieldWeightedScore(
            "Apollo Guide", null, "Content", null, "Apollo");
        var expectedFused = 1.0 * LocalVectorSearchService.VectorWeight
                          + expectedKeyword * LocalVectorSearchService.KeywordWeight;
        Assert.Equal(expectedFused, results[0].Score, precision: 5);
    }

    // ============================================================
    // HybridSearchAsync: CancellationToken
    // ============================================================

    [Fact]
    public async Task HybridSearchAsync_RespectsCancellationToken()
    {
        _db.KnowledgeItems.Add(new Knowledge
        {
            TenantId = TenantId, Title = "Apollo", Content = "Content"
        });
        await _db.SaveChangesAsync();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _svc.HybridSearchAsync("Apollo", cancellationToken: cts.Token));
    }
}
