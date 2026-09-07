namespace Knowz.SelfHosted.Application.Services.GitCommitHistory;

/// <summary>
/// In-process regex-based secret scanner for commit metadata (commit messages, file paths, author fields).
/// Detects common high-entropy patterns: AWS/Azure/GCP/Stripe/OpenAI API keys, JWTs, PEM blocks, JDBC URLs.
///
/// Selfhosted parity mirror of platform <c>Knowz.Application.Interfaces.ICommitSecretScanner</c>.
/// Used inline inside <see cref="GitCommitHistoryService"/> (no Service Bus / Function layer
/// in selfhosted — elaboration happens in-process).
///
/// CRIT-2 mitigation: secret/credential leakage prevention.
///
/// WorkGroupID: kc-feat-git-commit-knowledge-sync-20260410
/// NodeID: SelfHostedCommitHistoryParity (NODE-4)
/// </summary>
public interface ICommitSecretScanner
{
    /// <summary>
    /// Scan the supplied text for secrets. Returns a redacted copy of the text with
    /// matches replaced by "[REDACTED:{pattern_id}]" markers, plus a list of match descriptors.
    /// Match descriptors contain pattern ID and span ONLY — never the matched text itself
    /// (log-poisoning mitigation).
    /// </summary>
    SecretScanResult Scan(string? text);
}

/// <summary>Result of a secret scan pass.</summary>
public sealed record SecretScanResult(
    bool HasMatches,
    IReadOnlyList<SecretMatch> Matches,
    string RedactedText);

/// <summary>
/// A single secret-scanner hit. PatternId identifies the rule. Start/End are character
/// offsets in the ORIGINAL text. The matched text itself is NEVER carried.
/// </summary>
public sealed record SecretMatch(
    string PatternId,
    int Start,
    int End);
