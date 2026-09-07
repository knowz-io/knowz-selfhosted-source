using System.Text;
using System.Text.RegularExpressions;

namespace Knowz.SelfHosted.Application.Services.GitCommitHistory;

/// <summary>
/// In-process regex-based secret scanner. CRIT-2 mitigation: detects common
/// high-entropy patterns (AWS/Azure/GCP/Stripe/OpenAI API keys, JWTs, PEM blocks,
/// JDBC/connection strings, GitHub PATs) in commit messages and file paths.
///
/// Returns a <see cref="SecretScanResult"/> carrying the redacted text (with
/// "[REDACTED:{pattern_id}]" markers) and a list of pattern IDs hit.
///
/// The matched text itself is NEVER returned — only offsets + pattern IDs.
/// This prevents log poisoning (secrets being persisted in audit rows).
///
/// Used inline by the selfhosted commit-history ingestion path
/// (<see cref="GitCommitHistoryService"/> and <see cref="CommitElaborationPromptBuilder"/>).
/// Selfhosted has no Service Bus / Function layer, so there is no two-stage Stage A/B
/// split — the scanner runs inline once before the LLM call (when available).
///
/// This file is a deliberate copy-port of platform's
/// <c>Knowz.Application.Services.CommitSecretScanner</c> — the `Knowz.Shared` assembly
/// is not referenced from selfhosted, so the scanner is duplicated. Patterns must be kept
/// in sync with the platform implementation.
///
/// WorkGroupID: kc-feat-git-commit-knowledge-sync-20260410
/// NodeID: SelfHostedCommitHistoryParity (NODE-4)
/// </summary>
public sealed class CommitSecretScanner : ICommitSecretScanner
{
    // Patterns ordered most-specific first so that a narrow match (e.g. AWS access key)
    // takes precedence over a generic high-entropy match.
    private static readonly (string PatternId, Regex Regex)[] Patterns = new (string, Regex)[]
    {
        ("aws-access-key", new Regex(@"\b(AKIA|ASIA|ABIA|ACCA)[A-Z0-9]{16}\b", RegexOptions.Compiled)),
        ("aws-secret-key", new Regex(@"(?i)aws(.{0,20})?(secret|key)(.{0,20})?['""]?[0-9a-zA-Z/+=]{40}['""]?", RegexOptions.Compiled)),
        ("openai-key", new Regex(@"\bsk-(?:proj-)?[A-Za-z0-9_\-]{20,}\b", RegexOptions.Compiled)),
        ("anthropic-key", new Regex(@"\bsk-ant-[A-Za-z0-9_\-]{20,}\b", RegexOptions.Compiled)),
        ("github-pat", new Regex(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b", RegexOptions.Compiled)),
        ("github-finegrained-pat", new Regex(@"\bgithub_pat_[A-Za-z0-9_]{82,}\b", RegexOptions.Compiled)),
        ("stripe-key", new Regex(@"\b(?:sk|rk)_(?:live|test)_[0-9a-zA-Z]{20,}\b", RegexOptions.Compiled)),
        ("slack-token", new Regex(@"\bxox[baprs]-[A-Za-z0-9-]{10,}\b", RegexOptions.Compiled)),
        ("gcp-api-key", new Regex(@"\bAIza[0-9A-Za-z\-_]{35}\b", RegexOptions.Compiled)),
        ("twilio-sid", new Regex(@"\bAC[0-9a-f]{32}\b", RegexOptions.Compiled)),
        ("azure-storage-key", new Regex(@"\bDefaultEndpointsProtocol=https?;AccountName=[A-Za-z0-9]+;AccountKey=[A-Za-z0-9+/=]{88}\b", RegexOptions.Compiled)),
        ("azure-ad-client-secret", new Regex(@"\b[A-Za-z0-9_\-~]{34}\.[A-Za-z0-9_\-~]{8,}\b", RegexOptions.Compiled)),
        ("jwt", new Regex(@"\beyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\b", RegexOptions.Compiled)),
        ("pem-block", new Regex(@"-----BEGIN (?:RSA |DSA |EC |OPENSSH |ENCRYPTED |PGP )?(?:PRIVATE KEY|CERTIFICATE)-----[\s\S]*?-----END (?:RSA |DSA |EC |OPENSSH |ENCRYPTED |PGP )?(?:PRIVATE KEY|CERTIFICATE)-----", RegexOptions.Compiled)),
        ("jdbc-password", new Regex(@"(?i)jdbc:[a-z0-9]+://[^\s;""']*?(?:password|pwd)=[^\s;""']+", RegexOptions.Compiled)),
        ("sql-server-password", new Regex(@"(?i)(?:Server|Data Source)=[^;""']+;.*?(?:Password|Pwd)=[^;""']+", RegexOptions.Compiled)),
        ("generic-secret-assignment", new Regex(@"(?i)(?:api[_\-]?key|apikey|secret|token|password|passwd|pwd)[\s]*[:=][\s]*['""]?[A-Za-z0-9_\-+/=]{16,}['""]?", RegexOptions.Compiled)),
        ("high-entropy-base64", new Regex(@"\b[A-Za-z0-9+/]{40,}={0,2}\b", RegexOptions.Compiled))
    };

    public SecretScanResult Scan(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new SecretScanResult(HasMatches: false, Matches: Array.Empty<SecretMatch>(), RedactedText: text ?? string.Empty);
        }

        var spans = new List<(int Start, int End, string PatternId)>();

        foreach (var (patternId, regex) in Patterns)
        {
            foreach (Match match in regex.Matches(text))
            {
                if (!match.Success || match.Length == 0)
                {
                    continue;
                }

                bool overlaps = false;
                foreach (var (start, end, _) in spans)
                {
                    if (match.Index < end && (match.Index + match.Length) > start)
                    {
                        overlaps = true;
                        break;
                    }
                }

                if (!overlaps)
                {
                    spans.Add((match.Index, match.Index + match.Length, patternId));
                }
            }
        }

        if (spans.Count == 0)
        {
            return new SecretScanResult(HasMatches: false, Matches: Array.Empty<SecretMatch>(), RedactedText: text);
        }

        spans.Sort((a, b) => a.Start.CompareTo(b.Start));

        var sb = new StringBuilder(text.Length);
        var matches = new List<SecretMatch>(spans.Count);
        int cursor = 0;
        foreach (var (start, end, patternId) in spans)
        {
            if (start > cursor)
            {
                sb.Append(text, cursor, start - cursor);
            }
            sb.Append("[REDACTED:");
            sb.Append(patternId);
            sb.Append(']');
            matches.Add(new SecretMatch(patternId, start, end));
            cursor = end;
        }

        if (cursor < text.Length)
        {
            sb.Append(text, cursor, text.Length - cursor);
        }

        return new SecretScanResult(HasMatches: true, Matches: matches, RedactedText: sb.ToString());
    }
}
