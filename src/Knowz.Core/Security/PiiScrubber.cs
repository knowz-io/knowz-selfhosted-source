using System.Text.Json;
using System.Text.RegularExpressions;

namespace Knowz.Core.Security;

/// <summary>
/// Static utility for scrubbing PII from text and JSON content.
/// Thread-safe - all methods are pure functions with no shared state.
/// </summary>
public static class PiiScrubber
{
    /// <summary>
    /// Regex timeout to prevent ReDoS attacks on malformed input.
    /// </summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Maximum depth for JSON scrubbing to prevent stack overflow.
    /// </summary>
    private const int MaxJsonDepth = 10;

    #region Pattern Definitions

    /// <summary>
    /// Email: standard RFC 5322 simplified pattern
    /// </summary>
    private static readonly Regex EmailPattern = new(
        @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    /// <summary>
    /// Phone: US/international formats (10-15 digits with common separators)
    /// </summary>
    private static readonly Regex PhonePattern = new(
        @"(?:\+?1[-.\s]?)?\(?[0-9]{3}\)?[-.\s]?[0-9]{3}[-.\s]?[0-9]{4}",
        RegexOptions.Compiled,
        RegexTimeout);

    /// <summary>
    /// SSN: XXX-XX-XXXX format
    /// </summary>
    private static readonly Regex SsnPattern = new(
        @"\b\d{3}-\d{2}-\d{4}\b",
        RegexOptions.Compiled,
        RegexTimeout);

    /// <summary>
    /// Credit Card: 13-19 digits with common separators
    /// </summary>
    private static readonly Regex CreditCardPattern = new(
        @"\b(?:\d{4}[-\s]?){3,4}\d{1,4}\b",
        RegexOptions.Compiled,
        RegexTimeout);

    /// <summary>
    /// IP Address: IPv4 pattern
    /// </summary>
    private static readonly Regex IpAddressPattern = new(
        @"\b(?:\d{1,3}\.){3}\d{1,3}\b",
        RegexOptions.Compiled,
        RegexTimeout);

    #endregion

    #region Placeholder Constants

    private const string EmailPlaceholder = "[EMAIL]";
    private const string PhonePlaceholder = "[PHONE]";
    private const string SsnPlaceholder = "[SSN]";
    private const string CreditCardPlaceholder = "[CC]";
    private const string IpAddressPlaceholder = "[IP]";

    #endregion

    /// <summary>
    /// Scrub PII from arbitrary text content.
    /// </summary>
    /// <param name="text">Text potentially containing PII</param>
    /// <returns>Text with PII replaced by placeholders</returns>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text ?? string.Empty;

        var result = text;
        var patterns = new (Regex pattern, string placeholder)[]
        {
            (EmailPattern, EmailPlaceholder),
            (PhonePattern, PhonePlaceholder),
            (SsnPattern, SsnPlaceholder),
            (CreditCardPattern, CreditCardPlaceholder),
            (IpAddressPattern, IpAddressPlaceholder)
        };

        foreach (var (pattern, placeholder) in patterns)
        {
            try
            {
                result = pattern.Replace(result, placeholder);
            }
            catch (RegexMatchTimeoutException)
            {
                // Regex took too long on this pattern - return what we've scrubbed so far.
                // Earlier patterns have already been applied to 'result', so partial scrub is preserved.
                return result;
            }
        }

        return result;
    }

    /// <summary>
    /// Scrub PII from JSON content (tool results, entity extractions).
    /// Preserves JSON structure while scrubbing values.
    /// </summary>
    /// <param name="json">JSON string potentially containing PII</param>
    /// <returns>JSON with PII values replaced by placeholders</returns>
    public static string ScrubJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return json ?? string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var scrubbed = ScrubJsonElement(doc.RootElement, 0);
            return JsonSerializer.Serialize(scrubbed);
        }
        catch (JsonException)
        {
            // Not valid JSON - treat as plain text
            return Scrub(json);
        }
    }

    /// <summary>
    /// Check if text contains potential PII (for logging/alerting).
    /// Does not modify the text.
    /// </summary>
    /// <param name="text">Text to check</param>
    /// <returns>True if PII patterns detected</returns>
    public static bool ContainsPii(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            return EmailPattern.IsMatch(text) ||
                   PhonePattern.IsMatch(text) ||
                   SsnPattern.IsMatch(text) ||
                   CreditCardPattern.IsMatch(text) ||
                   IpAddressPattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            // Assume PII present if we can't check
            return true;
        }
    }

    /// <summary>
    /// Truncate text and scrub PII in a single operation.
    /// Useful for tool result summaries.
    /// </summary>
    /// <param name="text">Text to truncate and scrub</param>
    /// <param name="maxLength">Maximum length</param>
    /// <returns>Truncated and scrubbed text</returns>
    public static string TruncateAndScrub(string? text, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text ?? string.Empty;

        var scrubbed = Scrub(text);

        if (scrubbed.Length <= maxLength)
            return scrubbed;

        return scrubbed[..maxLength] + "...";
    }

    #region Private Helpers

    private static object? ScrubJsonElement(JsonElement element, int depth)
    {
        if (depth > MaxJsonDepth)
            return "[MAX_DEPTH_EXCEEDED]";

        return element.ValueKind switch
        {
            JsonValueKind.String => Scrub(element.GetString()),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray()
                .Select(e => ScrubJsonElement(e, depth + 1))
                .ToList(),
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => ScrubJsonElement(p.Value, depth + 1)),
            _ => element.ToString()
        };
    }

    #endregion
}
