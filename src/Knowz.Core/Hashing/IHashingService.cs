namespace Knowz.Core.Hashing;

/// <summary>
/// Service for computing file and content hashes
/// </summary>
public interface IHashingService
{
    /// <summary>
    /// Compute SHA-256 hash of file content
    /// </summary>
    Task<string> ComputeFileHashAsync(string filePath);

    /// <summary>
    /// Compute SHA-256 hash of string content
    /// </summary>
    string ComputeHash(string content);

    /// <summary>
    /// Compute SHA-256 hash of byte array
    /// </summary>
    string ComputeHash(byte[] data);

    /// <summary>
    /// Compute SHA-256 hash of stream
    /// </summary>
    Task<string> ComputeHashAsync(Stream stream);

    /// <summary>
    /// Compute Merkle root hash for a directory
    /// </summary>
    Task<string> ComputeMerkleRootAsync(string directoryPath, string[]? excludePatterns = null);

    /// <summary>
    /// Build Merkle tree for a set of hashes
    /// </summary>
    MerkleTree BuildMerkleTree(IEnumerable<string> leafHashes);

    /// <summary>
    /// Build Merkle tree from file path to hash mappings, populating LeafHashes for diff support
    /// </summary>
    MerkleTree BuildMerkleTree(Dictionary<string, string> fileHashes);

    /// <summary>
    /// Verify file integrity using stored hash
    /// </summary>
    Task<bool> VerifyFileIntegrityAsync(string filePath, string expectedHash);

    /// <summary>
    /// Compare two Merkle trees to find differences
    /// </summary>
    MerkleTreeDiff CompareMerkleTrees(MerkleTree tree1, MerkleTree tree2);
}
