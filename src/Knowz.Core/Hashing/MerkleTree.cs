namespace Knowz.Core.Hashing;

/// <summary>
/// Represents a Merkle tree structure
/// </summary>
public class MerkleTree
{
    public string RootHash { get; set; } = string.Empty;
    public List<MerkleNode> Nodes { get; set; } = new();
    public Dictionary<string, string> LeafHashes { get; set; } = new();
}

/// <summary>
/// Represents a node in the Merkle tree
/// </summary>
public class MerkleNode
{
    public string Hash { get; set; } = string.Empty;
    public MerkleNode? Left { get; set; }
    public MerkleNode? Right { get; set; }
    public string? FilePath { get; set; } // For leaf nodes
    public bool IsLeaf => Left == null && Right == null;
}

/// <summary>
/// Represents the difference between two Merkle trees
/// </summary>
public class MerkleTreeDiff
{
    public List<string> AddedFiles { get; set; } = new();
    public List<string> ModifiedFiles { get; set; } = new();
    public List<string> DeletedFiles { get; set; } = new();
    public bool HasChanges => AddedFiles.Count > 0 || ModifiedFiles.Count > 0 || DeletedFiles.Count > 0;
}
