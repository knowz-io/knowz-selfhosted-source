using Knowz.Core.Interfaces;
using Knowz.Core.Models;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// SelfHostedAbstractionSeams: verifies the IEmbeddingService and IVectorStore adapter
/// wraps over the legacy IOpenAIService and ISearchService. Pure-delegation contract —
/// adapters must not transform inputs or alter outputs.
/// </summary>
public class AbstractionSeamTests
{
    // ===== IEmbeddingService / EmbeddingServiceAdapter =====

    [Fact]
    public void EmbeddingServiceAdapter_Dimensions_DefaultsTo1536_WhenConfigMissing()
    {
        var inner = Substitute.For<IOpenAIService>();
        var config = new ConfigurationBuilder().Build();

        var adapter = new EmbeddingServiceAdapter(inner, config);

        Assert.Equal(1536, adapter.Dimensions);
    }

    [Fact]
    public void EmbeddingServiceAdapter_Dimensions_ReadsConfigValue()
    {
        var inner = Substitute.For<IOpenAIService>();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Embedding:Dimensions"] = "384",
            })
            .Build();

        var adapter = new EmbeddingServiceAdapter(inner, config);

        Assert.Equal(384, adapter.Dimensions);
    }

    [Fact]
    public async Task EmbeddingServiceAdapter_GenerateEmbedding_DelegatesToInner()
    {
        var inner = Substitute.For<IOpenAIService>();
        var expected = new float[] { 0.1f, 0.2f, 0.3f };
        inner.GenerateEmbeddingAsync("hello", Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<float[]?>(expected));

        var config = new ConfigurationBuilder().Build();
        var adapter = new EmbeddingServiceAdapter(inner, config);

        var result = await adapter.GenerateEmbeddingAsync("hello");

        Assert.Same(expected, result);
        await inner.Received(1).GenerateEmbeddingAsync("hello", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmbeddingServiceAdapter_GenerateEmbeddings_BatchPreservesOrder()
    {
        var inner = Substitute.For<IOpenAIService>();
        inner.GenerateEmbeddingAsync("a", Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<float[]?>(new float[] { 1f }));
        inner.GenerateEmbeddingAsync("b", Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<float[]?>(new float[] { 2f }));
        inner.GenerateEmbeddingAsync("c", Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<float[]?>((float[]?)null));

        var config = new ConfigurationBuilder().Build();
        var adapter = new EmbeddingServiceAdapter(inner, config);

        var results = await adapter.GenerateEmbeddingsAsync(new[] { "a", "b", "c" });

        Assert.Equal(3, results.Count);
        Assert.Equal(1f, results[0]![0]);
        Assert.Equal(2f, results[1]![0]);
        Assert.Null(results[2]);
    }

    // ===== IVectorStore / VectorStoreAdapter =====

    [Fact]
    public async Task VectorStoreAdapter_IndexEmbedding_DelegatesToInnerIndexDocument()
    {
        var inner = Substitute.For<ISearchService>();
        var adapter = new VectorStoreAdapter(inner);
        var docId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var vec = new float[] { 0.5f, 0.5f };
        var metadata = new Dictionary<string, object>
        {
            ["title"] = "Test Doc",
            ["content"] = "Body text",
            ["vaultId"] = vaultId,
            ["vaultName"] = "MyVault",
            ["tags"] = new List<string> { "alpha", "beta" },
            ["knowledgeType"] = "Note",
        };

        await adapter.IndexEmbeddingAsync(docId, vec, metadata);

        await inner.Received(1).IndexDocumentAsync(
            docId,
            "Test Doc",
            "Body text",
            Arg.Any<string?>(),
            "MyVault",
            vaultId,
            Arg.Any<List<Guid>?>(),
            Arg.Any<string?>(),
            Arg.Is<IEnumerable<string>?>(t => t != null && t.Contains("alpha")),
            "Note",
            Arg.Any<string?>(),
            vec,
            Arg.Any<int?>(),
            Arg.Any<DateTime?>(),
            Arg.Any<DateTime?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VectorStoreAdapter_DeleteByDocumentId_DelegatesToInnerDelete()
    {
        var inner = Substitute.For<ISearchService>();
        var adapter = new VectorStoreAdapter(inner);
        var docId = Guid.NewGuid();

        await adapter.DeleteByDocumentIdAsync(docId);

        await inner.Received(1).DeleteDocumentAsync(docId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VectorStoreAdapter_Search_MapsHybridSearchResults()
    {
        var inner = Substitute.For<ISearchService>();
        var kid1 = Guid.NewGuid();
        var kid2 = Guid.NewGuid();
        inner.HybridSearchAsync(
            Arg.Any<string>(),
            Arg.Any<float[]?>(),
            Arg.Any<Guid?>(),
            Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<bool>(),
            Arg.Any<DateTime?>(),
            Arg.Any<DateTime?>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<SearchResultItem>
            {
                new() { KnowledgeId = kid1, Title = "First", Content = "Body 1", Score = 0.95 },
                new() { KnowledgeId = kid2, Title = "Second", Content = "Body 2", Score = 0.87 },
            }));

        var adapter = new VectorStoreAdapter(inner);
        var queryVec = new float[] { 1f, 0f };

        var results = await adapter.SearchAsync(queryVec, topK: 10);

        Assert.Equal(2, results.Count);
        Assert.Equal(kid1, results[0].DocumentId);
        Assert.Equal(0.95f, results[0].Score, precision: 2);
        Assert.Equal("First", (string)results[0].Metadata["title"]);
        Assert.Equal(kid2, results[1].DocumentId);
    }
}
