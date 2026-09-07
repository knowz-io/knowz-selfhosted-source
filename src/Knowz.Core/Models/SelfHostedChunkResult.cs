namespace Knowz.Core.Models;

public record SelfHostedChunkResult(
    int Position,
    string Content,
    string EmbeddingText,
    int CharCount,
    int OverlapChars = 0);
