using System.Text.RegularExpressions;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.Core.Models;

namespace Knowz.SelfHosted.Infrastructure.Services;

public class SelfHostedChunkingService : ISelfHostedChunkingService
{
    private static readonly Regex SentenceEndRegex = new(@"[.!?]\s+", RegexOptions.Compiled);
    private static readonly Regex MarkdownHeaderRegex = new(@"^#{1,6}\s+", RegexOptions.Compiled);

    /// <summary>
    /// Approximate token count: 1 token ~ 4 characters.
    /// </summary>
    internal static int ApproxTokenCount(string s) => s.Length / 4;

    public List<SelfHostedChunkResult> ChunkContent(
        string content,
        SelfHostedChunkingStrategy strategy,
        SelfHostedChunkingOptions? options = null)
    {
        options ??= new SelfHostedChunkingOptions();
        ResolveTokenSizing(options);

        if (string.IsNullOrWhiteSpace(content) || content.Length <= options.MinChars)
        {
            return [new SelfHostedChunkResult(0, content ?? string.Empty, content ?? string.Empty, (content ?? string.Empty).Length)];
        }

        return strategy switch
        {
            SelfHostedChunkingStrategy.Prose => ChunkProse(content, options),
            SelfHostedChunkingStrategy.Sentence => ChunkSentence(content, options),
            SelfHostedChunkingStrategy.Recursive => ChunkRecursive(content, options),
            SelfHostedChunkingStrategy.Markdown => ChunkMarkdown(content, options),
            _ => ChunkRecursive(content, options)
        };
    }

    public List<SelfHostedChunkResult> ChunkWithContext(
        string content,
        string title,
        string? summary = null,
        IEnumerable<string>? tags = null,
        SelfHostedChunkingStrategy strategy = SelfHostedChunkingStrategy.Prose,
        SelfHostedChunkingOptions? options = null)
    {
        var chunks = ChunkContent(content, strategy, options);

        // Single-chunk content: no header (EmbeddingText == Content)
        if (chunks.Count <= 1)
            return chunks;

        // Multi-chunk: prepend context header to EmbeddingText
        var header = BuildContextHeader(title, summary, tags);

        return chunks
            .Select(c => c with { EmbeddingText = $"{header}\n\n{c.Content}" })
            .ToList();
    }

    public SelfHostedChunkingStrategy DetermineStrategy(KnowledgeType knowledgeType)
    {
        return knowledgeType switch
        {
            KnowledgeType.Note => SelfHostedChunkingStrategy.Markdown,
            KnowledgeType.Document => SelfHostedChunkingStrategy.Markdown,
            KnowledgeType.QuestionAnswer => SelfHostedChunkingStrategy.Markdown,
            KnowledgeType.Journal => SelfHostedChunkingStrategy.Markdown,
            KnowledgeType.Transcript => SelfHostedChunkingStrategy.Sentence,
            KnowledgeType.Video => SelfHostedChunkingStrategy.Sentence,
            KnowledgeType.Audio => SelfHostedChunkingStrategy.Sentence,
            KnowledgeType.Code => SelfHostedChunkingStrategy.Recursive,
            KnowledgeType.Link => SelfHostedChunkingStrategy.Recursive,
            KnowledgeType.File => SelfHostedChunkingStrategy.Recursive,
            KnowledgeType.Prompt => SelfHostedChunkingStrategy.Recursive,
            KnowledgeType.Image => SelfHostedChunkingStrategy.Recursive,
            _ => SelfHostedChunkingStrategy.Recursive
        };
    }

    internal static string BuildContextHeader(string title, string? summary, IEnumerable<string>? tags)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(title))
            parts.Add(title);

        var tagList = tags?.Take(10).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        if (tagList?.Count > 0)
            parts.Add($"Tags: {string.Join(", ", tagList)}");

        if (!string.IsNullOrWhiteSpace(summary))
            parts.Add(summary);

        return string.Join("\n", parts);
    }

    /// <summary>
    /// Converts MaxTokens to char-based sizing (MaxChars/OverlapChars/MinChars)
    /// when MaxTokens is set, using the chars/4 approximation.
    /// </summary>
    private static void ResolveTokenSizing(SelfHostedChunkingOptions options)
    {
        if (options.MaxTokens > 0)
        {
            options.MaxChars = options.MaxTokens * 4;
            options.OverlapChars = options.OverlapTokens * 4;
            options.MinChars = options.MinTokens * 4;
        }
    }

    // --- Strategy implementations ---

    private static List<SelfHostedChunkResult> ChunkMarkdown(string content, SelfHostedChunkingOptions options)
    {
        var lines = content.Split('\n');
        var sections = new List<string>();
        var currentSection = new List<string>();

        foreach (var line in lines)
        {
            if (MarkdownHeaderRegex.IsMatch(line.TrimStart()))
            {
                // Flush current section
                if (currentSection.Count > 0)
                {
                    var sectionText = string.Join("\n", currentSection).Trim();
                    if (!string.IsNullOrWhiteSpace(sectionText))
                        sections.Add(sectionText);
                    currentSection.Clear();
                }
            }
            currentSection.Add(line);
        }

        // Flush final section
        if (currentSection.Count > 0)
        {
            var sectionText = string.Join("\n", currentSection).Trim();
            if (!string.IsNullOrWhiteSpace(sectionText))
                sections.Add(sectionText);
        }

        // If only one section (no headers or single header), check if oversized
        if (sections.Count <= 1)
        {
            var singleContent = sections.Count == 1 ? sections[0] : content;
            if (singleContent.Length > options.MaxChars)
                return ChunkRecursive(singleContent, options);
            return [new SelfHostedChunkResult(0, singleContent, singleContent, singleContent.Length)];
        }

        // Merge small sections (under MinTokens) with their neighbors
        var merged = new List<string>();
        var pending = "";

        foreach (var section in sections)
        {
            if (pending.Length == 0)
            {
                pending = section;
            }
            else
            {
                var combined = $"{pending}\n\n{section}";
                // If pending is small, try to merge
                if (ApproxTokenCount(pending) < options.MinTokens)
                {
                    pending = combined;
                }
                else
                {
                    merged.Add(pending);
                    pending = section;
                }
            }
        }
        if (pending.Length > 0)
        {
            // If the last pending section is still small, merge with previous
            if (merged.Count > 0 && ApproxTokenCount(pending) < options.MinTokens)
            {
                merged[^1] = $"{merged[^1]}\n\n{pending}";
            }
            else
            {
                merged.Add(pending);
            }
        }

        // Split oversized sections via recursive, collect final chunks
        var results = new List<SelfHostedChunkResult>();
        var position = 0;

        foreach (var section in merged)
        {
            if (section.Length > options.MaxChars)
            {
                var subChunks = ChunkRecursive(section, options);
                foreach (var sub in subChunks)
                {
                    results.Add(sub with { Position = position++ });
                }
            }
            else
            {
                results.Add(new SelfHostedChunkResult(position++, section, section, section.Length));
            }
        }

        return results;
    }

    private static List<SelfHostedChunkResult> ChunkProse(string content, SelfHostedChunkingOptions options)
    {
        // Split by double-newline into paragraphs
        var paragraphs = content.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        if (paragraphs.Count == 0)
            return [new SelfHostedChunkResult(0, content, content, content.Length)];

        var results = new List<SelfHostedChunkResult>();
        var currentChunk = "";
        var position = 0;

        foreach (var paragraph in paragraphs)
        {
            // If a single paragraph exceeds MaxChars, split it at sentence boundaries
            if (paragraph.Length > options.MaxChars)
            {
                // Flush current accumulated chunk first
                if (currentChunk.Length > 0)
                {
                    results.Add(CreateChunkResult(position++, currentChunk.Trim(), results, options.OverlapChars));
                    currentChunk = "";
                }

                var sentenceChunks = SplitAtSentenceBoundaries(paragraph, options.MaxChars);
                foreach (var sc in sentenceChunks)
                {
                    results.Add(CreateChunkResult(position++, sc.Trim(), results, options.OverlapChars));
                }
                continue;
            }

            // Merge small paragraphs
            var newLength = currentChunk.Length > 0 ? currentChunk.Length + 2 + paragraph.Length : paragraph.Length;
            if (newLength > options.MaxChars)
            {
                // Flush current chunk
                if (currentChunk.Length > 0)
                {
                    results.Add(CreateChunkResult(position++, currentChunk.Trim(), results, options.OverlapChars));
                }
                currentChunk = paragraph;
            }
            else
            {
                currentChunk = currentChunk.Length > 0 ? $"{currentChunk}\n\n{paragraph}" : paragraph;
            }
        }

        // Flush final chunk
        if (currentChunk.Length > 0)
        {
            results.Add(CreateChunkResult(position, currentChunk.Trim(), results, options.OverlapChars));
        }

        return results;
    }

    private static List<SelfHostedChunkResult> ChunkRecursive(string content, SelfHostedChunkingOptions options)
    {
        var separators = new[] { "\n\n", "\n", ". ", " " };
        var segments = RecursiveSplit(content, separators, 0, options.MaxChars);

        return MergeSegments(segments, options);
    }

    private static List<SelfHostedChunkResult> ChunkSentence(string content, SelfHostedChunkingOptions options)
    {
        // Split at sentence endings: [.!?] followed by whitespace
        var sentences = SentenceEndRegex.Split(content)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        if (sentences.Count == 0)
            return [new SelfHostedChunkResult(0, content, content, content.Length)];

        return MergeSegments(sentences, options);
    }

    // --- Helpers ---

    private static List<string> RecursiveSplit(string text, string[] separators, int sepIndex, int maxChars)
    {
        if (text.Length <= maxChars || sepIndex >= separators.Length)
            return [text];

        var sep = separators[sepIndex];
        var parts = text.Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        if (parts.Count <= 1)
        {
            // This separator didn't help; try the next one
            return RecursiveSplit(text, separators, sepIndex + 1, maxChars);
        }

        var result = new List<string>();
        foreach (var part in parts)
        {
            if (part.Length <= maxChars)
            {
                result.Add(part);
            }
            else
            {
                // Recursively split oversized parts with the next separator
                result.AddRange(RecursiveSplit(part, separators, sepIndex + 1, maxChars));
            }
        }

        return result;
    }

    private static List<SelfHostedChunkResult> MergeSegments(List<string> segments, SelfHostedChunkingOptions options)
    {
        var results = new List<SelfHostedChunkResult>();
        var currentChunk = "";
        var position = 0;

        foreach (var segment in segments)
        {
            // If a single segment exceeds MaxChars, force-split it
            if (segment.Length > options.MaxChars)
            {
                if (currentChunk.Length > 0)
                {
                    results.Add(CreateChunkResult(position++, currentChunk.Trim(), results, options.OverlapChars));
                    currentChunk = "";
                }

                // Force-split at MaxChars boundaries
                var remaining = segment;
                while (remaining.Length > options.MaxChars)
                {
                    var splitPoint = FindWordBoundary(remaining, options.MaxChars);
                    results.Add(CreateChunkResult(position++, remaining[..splitPoint].Trim(), results, options.OverlapChars));
                    remaining = remaining[splitPoint..].Trim();
                }
                if (remaining.Length > 0)
                    currentChunk = remaining;
                continue;
            }

            var newLength = currentChunk.Length > 0 ? currentChunk.Length + 1 + segment.Length : segment.Length;
            if (newLength > options.MaxChars)
            {
                if (currentChunk.Length > 0)
                {
                    results.Add(CreateChunkResult(position++, currentChunk.Trim(), results, options.OverlapChars));
                }
                currentChunk = segment;
            }
            else
            {
                currentChunk = currentChunk.Length > 0 ? $"{currentChunk} {segment}" : segment;
            }
        }

        if (currentChunk.Length > 0)
        {
            results.Add(CreateChunkResult(position, currentChunk.Trim(), results, options.OverlapChars));
        }

        return results;
    }

    private static SelfHostedChunkResult CreateChunkResult(int position, string content,
        List<SelfHostedChunkResult> previousChunks, int overlapChars)
    {
        var actualOverlap = 0;
        if (position > 0 && previousChunks.Count > 0 && overlapChars > 0)
        {
            var prevContent = previousChunks[^1].Content;
            var overlapText = prevContent.Length > overlapChars
                ? prevContent[^overlapChars..]
                : prevContent;

            // Check how many chars from the end of prev are at the start of current
            if (content.StartsWith(overlapText, StringComparison.Ordinal))
            {
                actualOverlap = overlapText.Length;
            }
        }

        return new SelfHostedChunkResult(position, content, content, content.Length, actualOverlap);
    }

    private static List<string> SplitAtSentenceBoundaries(string text, int maxChars)
    {
        var sentences = SentenceEndRegex.Split(text)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        if (sentences.Count <= 1)
        {
            // Can't split further at sentence boundaries; force-split
            return ForceSplit(text, maxChars);
        }

        var results = new List<string>();
        var current = "";

        foreach (var sentence in sentences)
        {
            var newLength = current.Length > 0 ? current.Length + 1 + sentence.Length : sentence.Length;
            if (newLength > maxChars && current.Length > 0)
            {
                results.Add(current);
                current = sentence;
            }
            else
            {
                current = current.Length > 0 ? $"{current} {sentence}" : sentence;
            }
        }

        if (current.Length > 0)
            results.Add(current);

        return results;
    }

    private static List<string> ForceSplit(string text, int maxChars)
    {
        var results = new List<string>();
        var remaining = text;

        while (remaining.Length > maxChars)
        {
            var splitPoint = FindWordBoundary(remaining, maxChars);
            results.Add(remaining[..splitPoint].Trim());
            remaining = remaining[splitPoint..].Trim();
        }

        if (remaining.Length > 0)
            results.Add(remaining);

        return results;
    }

    private static int FindWordBoundary(string text, int maxPos)
    {
        if (maxPos >= text.Length)
            return text.Length;

        // Look backwards for a space
        var space = text.LastIndexOf(' ', maxPos - 1, Math.Min(maxPos, maxPos / 2));
        return space > 0 ? space + 1 : maxPos;
    }
}
