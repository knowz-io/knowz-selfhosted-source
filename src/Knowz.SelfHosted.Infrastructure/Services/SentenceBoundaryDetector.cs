using System.Text.RegularExpressions;

namespace Knowz.SelfHosted.Infrastructure.Services;

internal static class SentenceBoundaryDetector
{
    private const char Placeholder = '\x00';

    // Common abbreviations that end with a period but are not sentence boundaries
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mr", "Mrs", "Ms", "Dr", "Prof", "Sr", "Jr", "St", "vs", "etc",
        "Inc", "Corp", "Ltd", "Gov", "Gen", "Col", "Sgt", "Pvt",
        "Dept", "Est", "Vol", "Rev", "Fig", "Approx", "Assn"
    };

    // Matches: abbreviation period (word boundary followed by known abbreviation + period)
    private static readonly Regex AbbreviationRegex = new(
        @"(?<=\b(?:" + string.Join("|", Abbreviations) + @"))\.",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches: single uppercase letter period (initials like "J. K.")
    private static readonly Regex InitialRegex = new(
        @"(?<=\b[A-Z])\.",
        RegexOptions.Compiled);

    // Matches: digit period digit (decimals like "3.14")
    private static readonly Regex DecimalRegex = new(
        @"(?<=\d)\.(?=\d)",
        RegexOptions.Compiled);

    // Matches: URLs — periods inside http(s)://... sequences
    private static readonly Regex UrlRegex = new(
        @"https?://\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches: ellipsis (three or more dots)
    private static readonly Regex EllipsisRegex = new(
        @"\.{2,}",
        RegexOptions.Compiled);

    // Actual sentence boundary: period, exclamation, or question mark followed by whitespace
    private static readonly Regex SentenceEndRegex = new(
        @"([.!?])\s+",
        RegexOptions.Compiled);

    public static List<string> SplitIntoSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [text ?? string.Empty];

        // Pass 1: protect false boundaries by replacing periods with placeholder
        var working = text;

        // Protect URLs first (they contain multiple periods)
        working = UrlRegex.Replace(working, m => m.Value.Replace('.', Placeholder));

        // Protect ellipsis
        working = EllipsisRegex.Replace(working, m => m.Value.Replace('.', Placeholder));

        // Protect decimal numbers
        working = DecimalRegex.Replace(working, Placeholder.ToString());

        // Protect abbreviations
        working = AbbreviationRegex.Replace(working, Placeholder.ToString());

        // Protect initials
        working = InitialRegex.Replace(working, Placeholder.ToString());

        // Pass 2: split at real sentence boundaries
        var parts = SentenceEndRegex.Split(working);

        // Reassemble: Split with capture group produces [text, delimiter, text, delimiter, ...]
        var sentences = new List<string>();
        var current = "";

        for (int i = 0; i < parts.Length; i++)
        {
            if (i % 2 == 0)
            {
                // Text part
                current += parts[i];
            }
            else
            {
                // Delimiter (captured punctuation)
                current += parts[i];
                var restored = current.Replace(Placeholder, '.').Trim();
                if (restored.Length > 0)
                    sentences.Add(restored);
                current = "";
            }
        }

        // Last segment (text after the final sentence-ending punctuation)
        if (current.Length > 0)
        {
            var restored = current.Replace(Placeholder, '.').Trim();
            if (restored.Length > 0)
                sentences.Add(restored);
        }

        return sentences.Count > 0 ? sentences : [text];
    }
}
