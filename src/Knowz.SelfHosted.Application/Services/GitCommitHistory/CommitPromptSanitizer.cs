using System.Text.RegularExpressions;

namespace Knowz.SelfHosted.Application.Services.GitCommitHistory;

/// <summary>
/// Untrusted-field sanitizer used exclusively by the commit-history path in selfhosted.
/// Deliberate copy-port of platform's <c>Knowz.Shared.Security.PromptSanitizer</c> — the
/// <c>Knowz.Shared</c> assembly is not referenced from selfhosted, and the shared utility
/// touches features (alias validation, JSON escape rules) that are broader than what
/// this ingestion path needs. Rather than pull a large assembly in transitively, we ship
/// a focused, single-use copy.
///
/// Responsibilities:
///   - <see cref="ContainsInjectionAttempt"/>: detect known prompt-injection phrases and
///     dangerous patterns (audit-only — does not block).
///   - <see cref="SanitizeText"/>: strip dangerous patterns + replace injection phrases.
///   - <see cref="SanitizeContentForPrompt"/>: same with a higher default length.
///
/// Patterns MUST be kept in sync with platform <c>PromptSanitizer</c>. Drift between the
/// two copies is tracked in the Group B debt list.
///
/// WorkGroupID: kc-feat-git-commit-knowledge-sync-20260410
/// NodeID: SelfHostedCommitHistoryParity (NODE-4)
/// </summary>
internal static class CommitPromptSanitizer
{
    private static readonly string[] DangerousPatterns =
    {
        "```",
        "###",
        "---",
        "SYSTEM:",
        "USER:",
        "ASSISTANT:",
        "ignore previous",
        "ignore all",
        "disregard",
        "new instructions",
        "[[",
        "]]",
        "{{",
        "}}",
        "<|",
        "|>",
        "</s>",
        "<s>",
    };

    private static readonly Regex InjectionPattern = new(
        @"(ignore|disregard|forget|override|bypass|skip|new\s+instruction|system\s*:)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool ContainsInjectionAttempt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var pattern in DangerousPatterns)
        {
            if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return InjectionPattern.IsMatch(text);
    }

    public static string SanitizeText(string? text, int maxLength = 500)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sanitized = text.Trim();

        if (sanitized.Length > maxLength)
        {
            sanitized = sanitized[..maxLength];
        }

        foreach (var pattern in DangerousPatterns)
        {
            sanitized = sanitized.Replace(pattern, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        sanitized = InjectionPattern.Replace(sanitized, "[removed]");

        sanitized = EscapeForJson(sanitized);

        return sanitized.Trim();
    }

    public static string SanitizeContentForPrompt(string? text, int maxLength = 5000)
        => SanitizeText(text, maxLength);

    private static string EscapeForJson(string text)
    {
        return text
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }
}
