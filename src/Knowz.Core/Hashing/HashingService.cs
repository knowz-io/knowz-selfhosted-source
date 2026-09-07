using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Knowz.Core.Hashing;

/// <summary>
/// Implementation of hashing service using SHA-256
/// </summary>
public class HashingService : IHashingService
{
    private readonly ILogger<HashingService> _logger;

    public HashingService(ILogger<HashingService> logger)
    {
        _logger = logger;
    }

    public async Task<string> ComputeFileHashAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            return await ComputeHashAsync(stream);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error computing hash for file: {FilePath}", filePath);
            throw;
        }
    }

    public string ComputeHash(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(content);
        return ComputeHash(bytes);
    }

    public string ComputeHash(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(data);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public async Task<string> ComputeHashAsync(Stream stream)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = await sha256.ComputeHashAsync(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public async Task<string> ComputeMerkleRootAsync(string directoryPath, string[]? excludePatterns = null)
    {
        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");
        }

        var leafHashes = new Dictionary<string, string>();

        // Get all files in directory
        var files = Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories)
            .OrderBy(f => f) // Ensure consistent ordering
            .ToList();

        // Apply exclusion patterns
        if (excludePatterns != null && excludePatterns.Length > 0)
        {
            files = files.Where(f => !IsExcluded(f, excludePatterns)).ToList();
        }

        // Compute hash for each file
        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(directoryPath, file);
            var fileHash = await ComputeFileHashAsync(file);
            leafHashes[relativePath] = fileHash;
        }

        // Build and return Merkle tree with file path mappings
        var tree = BuildMerkleTree(leafHashes);
        return tree.RootHash;
    }

    public MerkleTree BuildMerkleTree(IEnumerable<string> leafHashes)
    {
        var tree = new MerkleTree();
        var hashes = leafHashes.ToList();

        if (hashes.Count == 0)
        {
            tree.RootHash = ComputeHash(string.Empty);
            return tree;
        }

        // Create leaf nodes
        var currentLevel = hashes.Select(hash => new MerkleNode { Hash = hash }).ToList();
        tree.Nodes.AddRange(currentLevel);

        BuildTreeFromNodes(currentLevel, tree);
        return tree;
    }

    public MerkleTree BuildMerkleTree(Dictionary<string, string> fileHashes)
    {
        var tree = new MerkleTree();

        if (fileHashes.Count == 0)
        {
            tree.RootHash = ComputeHash(string.Empty);
            return tree;
        }

        // Populate LeafHashes from input dictionary
        foreach (var kvp in fileHashes)
        {
            tree.LeafHashes[kvp.Key] = kvp.Value;
        }

        // Create leaf nodes with FilePath set
        var currentLevel = fileHashes.Select(kvp => new MerkleNode
        {
            Hash = kvp.Value,
            FilePath = kvp.Key
        }).ToList();
        tree.Nodes.AddRange(currentLevel);

        BuildTreeFromNodes(currentLevel, tree);
        return tree;
    }

    private void BuildTreeFromNodes(List<MerkleNode> currentLevel, MerkleTree tree)
    {
        while (currentLevel.Count > 1)
        {
            var nextLevel = new List<MerkleNode>();

            for (int i = 0; i < currentLevel.Count; i += 2)
            {
                var left = currentLevel[i];
                var right = (i + 1 < currentLevel.Count) ? currentLevel[i + 1] : left;

                var parentHash = ComputeHash(left.Hash + right.Hash);
                var parent = new MerkleNode
                {
                    Hash = parentHash,
                    Left = left,
                    Right = right
                };

                nextLevel.Add(parent);
                tree.Nodes.Add(parent);
            }

            currentLevel = nextLevel;
        }

        tree.RootHash = currentLevel[0].Hash;
    }

    public async Task<bool> VerifyFileIntegrityAsync(string filePath, string expectedHash)
    {
        try
        {
            var actualHash = await ComputeFileHashAsync(filePath);
            return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying file integrity: {FilePath}", filePath);
            return false;
        }
    }

    public MerkleTreeDiff CompareMerkleTrees(MerkleTree tree1, MerkleTree tree2)
    {
        var diff = new MerkleTreeDiff();

        // Quick check - if roots are same, trees are identical
        if (tree1.RootHash == tree2.RootHash)
        {
            return diff;
        }

        var hashes1 = tree1.LeafHashes;
        var hashes2 = tree2.LeafHashes;

        // Find added files (in tree2 but not in tree1)
        foreach (var kvp in hashes2)
        {
            if (!hashes1.ContainsKey(kvp.Key))
            {
                diff.AddedFiles.Add(kvp.Key);
            }
        }

        // Find deleted files (in tree1 but not in tree2)
        foreach (var kvp in hashes1)
        {
            if (!hashes2.ContainsKey(kvp.Key))
            {
                diff.DeletedFiles.Add(kvp.Key);
            }
        }

        // Find modified files (in both but different hash)
        foreach (var kvp in hashes1)
        {
            if (hashes2.TryGetValue(kvp.Key, out var hash2) && kvp.Value != hash2)
            {
                diff.ModifiedFiles.Add(kvp.Key);
            }
        }

        return diff;
    }

    private bool IsExcluded(string filePath, string[] excludePatterns)
    {
        foreach (var pattern in excludePatterns)
        {
            if (filePath.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
