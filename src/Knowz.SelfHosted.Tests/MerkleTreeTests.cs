using Knowz.Core.Hashing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// Unit tests for MerkleTree leaf hash population and tree comparison.
/// Tests the dictionary overload of BuildMerkleTree and CompareMerkleTrees.
/// </summary>
public class MerkleTreeTests
{
    private readonly HashingService _service;

    public MerkleTreeTests()
    {
        _service = new HashingService(NullLogger<HashingService>.Instance);
    }

    // --- BuildMerkleTree with Dictionary input ---

    [Fact]
    public void BuildMerkleTree_WithDictionary_PopulatesLeafHashes()
    {
        var fileHashes = new Dictionary<string, string>
        {
            ["file1.txt"] = "aaa111",
            ["dir/file2.txt"] = "bbb222",
            ["file3.txt"] = "ccc333"
        };

        var tree = _service.BuildMerkleTree(fileHashes);

        Assert.Equal(3, tree.LeafHashes.Count);
        Assert.Equal("aaa111", tree.LeafHashes["file1.txt"]);
        Assert.Equal("bbb222", tree.LeafHashes["dir/file2.txt"]);
        Assert.Equal("ccc333", tree.LeafHashes["file3.txt"]);
    }

    [Fact]
    public void BuildMerkleTree_WithDictionary_SetsFilePathOnLeafNodes()
    {
        var fileHashes = new Dictionary<string, string>
        {
            ["readme.md"] = "abc123",
            ["src/main.cs"] = "def456"
        };

        var tree = _service.BuildMerkleTree(fileHashes);

        var leafNodes = tree.Nodes.Where(n => n.IsLeaf).ToList();
        Assert.Equal(2, leafNodes.Count);
        Assert.All(leafNodes, n => Assert.NotNull(n.FilePath));
        Assert.Contains(leafNodes, n => n.FilePath == "readme.md");
        Assert.Contains(leafNodes, n => n.FilePath == "src/main.cs");
    }

    [Fact]
    public void BuildMerkleTree_WithDictionary_ComputesValidRootHash()
    {
        var fileHashes = new Dictionary<string, string>
        {
            ["a.txt"] = "hash_a",
            ["b.txt"] = "hash_b"
        };

        var tree = _service.BuildMerkleTree(fileHashes);

        Assert.NotEmpty(tree.RootHash);
        // Root hash should be hash of concatenated child hashes
        var expectedRoot = _service.ComputeHash("hash_a" + "hash_b");
        Assert.Equal(expectedRoot, tree.RootHash);
    }

    [Fact]
    public void BuildMerkleTree_WithEmptyDictionary_ReturnsEmptyHashTree()
    {
        var fileHashes = new Dictionary<string, string>();

        var tree = _service.BuildMerkleTree(fileHashes);

        Assert.Empty(tree.LeafHashes);
        Assert.Empty(tree.Nodes);
    }

    [Fact]
    public void BuildMerkleTree_WithSingleEntry_RootEqualsLeafHash()
    {
        var fileHashes = new Dictionary<string, string>
        {
            ["only.txt"] = "single_hash"
        };

        var tree = _service.BuildMerkleTree(fileHashes);

        Assert.Equal("single_hash", tree.RootHash);
        Assert.Single(tree.LeafHashes);
    }

    // --- Backward compatibility: IEnumerable<string> overload ---

    [Fact]
    public void BuildMerkleTree_WithStringEnumerable_StillWorks()
    {
        var hashes = new[] { "hash1", "hash2", "hash3" };

        var tree = _service.BuildMerkleTree(hashes);

        Assert.NotEmpty(tree.RootHash);
        // Original overload should NOT populate LeafHashes (no file paths available)
        Assert.Empty(tree.LeafHashes);
    }

    // --- CompareMerkleTrees ---

    [Fact]
    public void CompareMerkleTrees_IdenticalTrees_NoDifferences()
    {
        var fileHashes = new Dictionary<string, string>
        {
            ["a.txt"] = "hash_a",
            ["b.txt"] = "hash_b"
        };

        var tree1 = _service.BuildMerkleTree(fileHashes);
        var tree2 = _service.BuildMerkleTree(fileHashes);

        var diff = _service.CompareMerkleTrees(tree1, tree2);

        Assert.False(diff.HasChanges);
        Assert.Empty(diff.AddedFiles);
        Assert.Empty(diff.DeletedFiles);
        Assert.Empty(diff.ModifiedFiles);
    }

    [Fact]
    public void CompareMerkleTrees_DetectsAddedFiles()
    {
        var hashes1 = new Dictionary<string, string>
        {
            ["a.txt"] = "hash_a"
        };
        var hashes2 = new Dictionary<string, string>
        {
            ["a.txt"] = "hash_a",
            ["b.txt"] = "hash_b"
        };

        var tree1 = _service.BuildMerkleTree(hashes1);
        var tree2 = _service.BuildMerkleTree(hashes2);

        var diff = _service.CompareMerkleTrees(tree1, tree2);

        Assert.True(diff.HasChanges);
        Assert.Single(diff.AddedFiles);
        Assert.Contains("b.txt", diff.AddedFiles);
        Assert.Empty(diff.DeletedFiles);
        Assert.Empty(diff.ModifiedFiles);
    }

    [Fact]
    public void CompareMerkleTrees_DetectsDeletedFiles()
    {
        var hashes1 = new Dictionary<string, string>
        {
            ["a.txt"] = "hash_a",
            ["b.txt"] = "hash_b"
        };
        var hashes2 = new Dictionary<string, string>
        {
            ["a.txt"] = "hash_a"
        };

        var tree1 = _service.BuildMerkleTree(hashes1);
        var tree2 = _service.BuildMerkleTree(hashes2);

        var diff = _service.CompareMerkleTrees(tree1, tree2);

        Assert.True(diff.HasChanges);
        Assert.Single(diff.DeletedFiles);
        Assert.Contains("b.txt", diff.DeletedFiles);
        Assert.Empty(diff.AddedFiles);
        Assert.Empty(diff.ModifiedFiles);
    }

    [Fact]
    public void CompareMerkleTrees_DetectsModifiedFiles()
    {
        var hashes1 = new Dictionary<string, string>
        {
            ["a.txt"] = "hash_a_v1",
            ["b.txt"] = "hash_b"
        };
        var hashes2 = new Dictionary<string, string>
        {
            ["a.txt"] = "hash_a_v2",
            ["b.txt"] = "hash_b"
        };

        var tree1 = _service.BuildMerkleTree(hashes1);
        var tree2 = _service.BuildMerkleTree(hashes2);

        var diff = _service.CompareMerkleTrees(tree1, tree2);

        Assert.True(diff.HasChanges);
        Assert.Single(diff.ModifiedFiles);
        Assert.Contains("a.txt", diff.ModifiedFiles);
        Assert.Empty(diff.AddedFiles);
        Assert.Empty(diff.DeletedFiles);
    }

    [Fact]
    public void CompareMerkleTrees_DetectsMultipleChangeTypes()
    {
        var hashes1 = new Dictionary<string, string>
        {
            ["unchanged.txt"] = "hash_same",
            ["modified.txt"] = "hash_old",
            ["deleted.txt"] = "hash_del"
        };
        var hashes2 = new Dictionary<string, string>
        {
            ["unchanged.txt"] = "hash_same",
            ["modified.txt"] = "hash_new",
            ["added.txt"] = "hash_add"
        };

        var tree1 = _service.BuildMerkleTree(hashes1);
        var tree2 = _service.BuildMerkleTree(hashes2);

        var diff = _service.CompareMerkleTrees(tree1, tree2);

        Assert.True(diff.HasChanges);
        Assert.Single(diff.AddedFiles);
        Assert.Contains("added.txt", diff.AddedFiles);
        Assert.Single(diff.DeletedFiles);
        Assert.Contains("deleted.txt", diff.DeletedFiles);
        Assert.Single(diff.ModifiedFiles);
        Assert.Contains("modified.txt", diff.ModifiedFiles);
    }
}
