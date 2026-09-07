namespace Knowz.SelfHosted.Application.Services.GitCommitHistory;

/// <summary>
/// Selfhosted mirror of platform <c>ICommitElaborationPromptBuilder</c>. Builds sanitized
/// LLM prompts for commit elaboration following the DETECT → SANITIZE → ASSEMBLE pattern.
///
/// CRIT-1 mitigation: prompt injection defense (XML delimiters, system-prompt directive,
/// untrusted-field sanitization via <see cref="CommitPromptSanitizer"/>).
/// CRIT-2 mitigation: pre-LLM redaction via <see cref="ICommitSecretScanner"/>.
///
/// WorkGroupID: kc-feat-git-commit-knowledge-sync-20260410
/// NodeID: SelfHostedCommitHistoryParity (NODE-4)
/// </summary>
public interface ICommitElaborationPromptBuilder
{
    PromptBuildResult Build(CommitElaborationRequest request);
}

/// <summary>
/// Result of prompt assembly. System + user prompts are LLM-ready. <paramref name="EstimatedTokens"/>
/// is a conservative character-count-based estimate. <paramref name="InjectionFlaggedFields"/> is a
/// list of field names that triggered injection detection (never contains matching text — audit safe).
/// <paramref name="SecretPatternIdsRedacted"/> lists pattern IDs that hit during Stage A scanning.
/// </summary>
public sealed record PromptBuildResult(
    string SystemPrompt,
    string UserPrompt,
    int EstimatedTokens,
    IReadOnlyList<string> InjectionFlaggedFields,
    IReadOnlyList<string> SecretPatternIdsRedacted);
