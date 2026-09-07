using System.Text.RegularExpressions;

namespace Knowz.SelfHosted.Infrastructure.Services;

internal enum SectionType
{
    Prose,
    CodeBlock,
    Table,
    Attachment,
    Comment
}

internal record ContentSection(
    string? Heading,
    int Level,
    string Content,
    SectionType SectionType);

internal static class ContentStructureParser
{
    // Attachment/comment section markers produced by GetAllAttachmentTextAsync()
    private static readonly Regex AttachmentMarkerRegex = new(
        @"^---\s+Attachment:\s+.+\s+---$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex CommentMarkerRegex = new(
        @"^---\s+Comment by\s+.+\s+---$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex CommentAttachmentMarkerRegex = new(
        @"^---\s+Comment Attachment:\s+.+\s+---$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // Matches any of the three section marker types
    private static readonly Regex AnySectionMarkerRegex = new(
        @"^---\s+(?:Attachment:|Comment by|Comment Attachment:)\s+.+\s+---$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex HeadingRegex = new(
        @"^(#{1,6})\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex FencedCodeBlockRegex = new(
        @"^(```|~~~).*?\n[\s\S]*?\n\1\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static bool ContainsStructure(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        return HeadingRegex.IsMatch(content)
            || AnySectionMarkerRegex.IsMatch(content)
            || FencedCodeBlockRegex.IsMatch(content);
    }

    public static List<ContentSection> ParseSections(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [new ContentSection(null, 0, content ?? string.Empty, SectionType.Prose)];

        var sections = new List<ContentSection>();
        var lines = content.Split('\n');

        // First pass: identify fenced code block line ranges (they are atomic)
        var codeBlockRanges = FindCodeBlockRanges(lines);

        // Second pass: walk lines and split at structural boundaries
        var currentLines = new List<string>();
        string? currentHeading = null;
        int currentLevel = 0;
        var currentType = SectionType.Prose;

        for (int i = 0; i < lines.Length; i++)
        {
            // Check if this line starts a code block
            var codeRange = codeBlockRanges.FirstOrDefault(r => r.Start == i);
            if (codeRange != default)
            {
                // Flush any accumulated content before the code block
                FlushSection(sections, currentLines, currentHeading, currentLevel, currentType);
                currentLines = new List<string>();

                // Collect the entire code block as one atomic section
                var codeLines = new List<string>();
                for (int j = codeRange.Start; j <= codeRange.End && j < lines.Length; j++)
                {
                    codeLines.Add(lines[j]);
                }
                var codeContent = string.Join("\n", codeLines);
                sections.Add(new ContentSection(null, 0, codeContent, SectionType.CodeBlock));

                // Skip past the code block
                i = codeRange.End;
                currentHeading = null;
                currentLevel = 0;
                currentType = SectionType.Prose;
                continue;
            }

            // Skip lines that are inside code blocks (shouldn't happen given above logic, but safety)
            if (codeBlockRanges.Any(r => i > r.Start && i <= r.End))
                continue;

            var line = lines[i];
            var trimmedLine = line.TrimEnd('\r');

            // Check for attachment/comment section markers
            var markerType = GetMarkerType(trimmedLine);
            if (markerType.HasValue)
            {
                // Flush previous section
                FlushSection(sections, currentLines, currentHeading, currentLevel, currentType);
                currentLines = new List<string> { line };
                currentHeading = trimmedLine;
                currentLevel = 7; // Attachment/comment sections get level 7
                currentType = markerType.Value;
                continue;
            }

            // Check for markdown headings
            var headingMatch = HeadingRegex.Match(trimmedLine);
            if (headingMatch.Success)
            {
                // Flush previous section
                FlushSection(sections, currentLines, currentHeading, currentLevel, currentType);
                currentLines = new List<string> { line };
                currentHeading = headingMatch.Groups[2].Value;
                currentLevel = headingMatch.Groups[1].Value.Length;
                currentType = SectionType.Prose;
                continue;
            }

            // Regular content line — add to current section
            currentLines.Add(line);
        }

        // Flush final section
        FlushSection(sections, currentLines, currentHeading, currentLevel, currentType);

        return sections.Count > 0 ? sections : [new ContentSection(null, 0, content, SectionType.Prose)];
    }

    private static SectionType? GetMarkerType(string line)
    {
        if (AttachmentMarkerRegex.IsMatch(line))
            return SectionType.Attachment;
        if (CommentMarkerRegex.IsMatch(line))
            return SectionType.Comment;
        if (CommentAttachmentMarkerRegex.IsMatch(line))
            return SectionType.Attachment;
        return null;
    }

    private static void FlushSection(
        List<ContentSection> sections,
        List<string> lines,
        string? heading,
        int level,
        SectionType sectionType)
    {
        if (lines.Count == 0)
            return;

        var content = string.Join("\n", lines);
        if (string.IsNullOrWhiteSpace(content))
            return;

        // Detect tables within prose sections
        if (sectionType == SectionType.Prose && IsTableContent(lines))
        {
            sections.Add(new ContentSection(heading, level, content, SectionType.Table));
        }
        else
        {
            sections.Add(new ContentSection(heading, level, content, sectionType));
        }
    }

    private static bool IsTableContent(List<string> lines)
    {
        // A table is a block where the majority of non-empty lines start with '|'
        var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (nonEmpty.Count < 2)
            return false;

        var tableLines = nonEmpty.Count(l => l.TrimStart().StartsWith('|'));
        return tableLines >= nonEmpty.Count * 0.8; // 80%+ lines are table rows
    }

    private static List<(int Start, int End)> FindCodeBlockRanges(string[] lines)
    {
        var ranges = new List<(int Start, int End)>();
        int i = 0;

        while (i < lines.Length)
        {
            var trimmed = lines[i].TrimEnd('\r');
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                var fence = trimmed.StartsWith("```") ? "```" : "~~~";
                int start = i;
                i++;

                // Find closing fence
                while (i < lines.Length)
                {
                    var closeTrimmed = lines[i].TrimEnd('\r');
                    if (closeTrimmed.TrimStart() == fence || closeTrimmed.TrimStart().StartsWith(fence))
                    {
                        // Verify it's just the fence (possibly with trailing whitespace)
                        var stripped = closeTrimmed.Trim();
                        if (stripped == fence)
                        {
                            ranges.Add((start, i));
                            break;
                        }
                    }
                    i++;
                }
            }
            i++;
        }

        return ranges;
    }
}
