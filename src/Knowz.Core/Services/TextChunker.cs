namespace Knowz.Core.Services;

/// <summary>
/// A chunk of text produced by the TextChunker.
/// </summary>
public record TextChunk(int Index, string Content);

/// <summary>
/// Splits long text into overlapping chunks for embedding and search indexing.
/// Attempts to split on paragraph/sentence boundaries when possible.
/// </summary>
public static class TextChunker
{
    /// <summary>
    /// Default maximum characters per chunk (~1000 tokens at 4 chars/token).
    /// </summary>
    public const int DefaultMaxChunkChars = 4000;

    /// <summary>
    /// Default overlap between chunks in characters (~100 tokens).
    /// </summary>
    public const int DefaultOverlapChars = 400;

    /// <summary>
    /// Minimum content length before chunking kicks in.
    /// Content shorter than this is returned as a single chunk.
    /// </summary>
    public const int ChunkingThreshold = 5000;

    /// <summary>
    /// Splits text into overlapping chunks. Returns a single chunk for short text.
    /// </summary>
    public static List<TextChunk> ChunkText(
        string text,
        int maxChunkChars = DefaultMaxChunkChars,
        int overlapChars = DefaultOverlapChars)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [new TextChunk(0, text ?? string.Empty)];

        if (text.Length <= ChunkingThreshold)
            return [new TextChunk(0, text)];

        var chunks = new List<TextChunk>();
        var position = 0;
        var index = 0;

        while (position < text.Length)
        {
            var end = Math.Min(position + maxChunkChars, text.Length);

            // If not at the end, try to find a good break point
            if (end < text.Length)
            {
                end = FindBreakPoint(text, position, end);
            }

            var chunk = text[position..end].Trim();
            if (chunk.Length > 0)
            {
                chunks.Add(new TextChunk(index, chunk));
                index++;
            }

            // If this chunk reached the end of text, we're done
            if (end >= text.Length)
                break;

            // Move forward, applying overlap
            var advance = end - position;
            position += Math.Max(advance - overlapChars, 1);
        }

        return chunks;
    }

    /// <summary>
    /// Prepends the title to each chunk for better embedding context.
    /// </summary>
    public static List<TextChunk> ChunkWithTitle(string title, string content,
        int maxChunkChars = DefaultMaxChunkChars, int overlapChars = DefaultOverlapChars)
    {
        var chunks = ChunkText(content, maxChunkChars, overlapChars);

        if (chunks.Count <= 1)
            return chunks; // Single chunk doesn't need title prefix

        return chunks
            .Select(c => new TextChunk(c.Index, $"{title}\n\n{c.Content}"))
            .ToList();
    }

    private static int FindBreakPoint(string text, int start, int end)
    {
        // Try paragraph break (double newline)
        var paragraphBreak = text.LastIndexOf("\n\n", end, end - start, StringComparison.Ordinal);
        if (paragraphBreak > start + (end - start) / 2)
            return paragraphBreak + 2; // Include the break

        // Try single newline
        var lineBreak = text.LastIndexOf('\n', end - 1, end - start);
        if (lineBreak > start + (end - start) / 2)
            return lineBreak + 1;

        // Try sentence end (. ! ?)
        for (var i = end - 1; i > start + (end - start) / 2; i--)
        {
            if (text[i] is '.' or '!' or '?' && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))
                return i + 1;
        }

        // Try space
        var space = text.LastIndexOf(' ', end - 1, end - start);
        if (space > start + (end - start) / 2)
            return space + 1;

        // No good break point, just cut at max
        return end;
    }
}
