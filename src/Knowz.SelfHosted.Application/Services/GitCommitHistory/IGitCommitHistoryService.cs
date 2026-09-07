namespace Knowz.SelfHosted.Application.Services.GitCommitHistory;

/// <summary>
/// Selfhosted mirror of platform <c>IGitCommitHistoryService</c>. Idempotent entry point
/// for commit-history ingestion. Called from <c>GitSyncService</c> after file processing
/// when <c>GitRepository.TrackCommitHistory</c> is true.
///
/// Creates the parent <c>KnowledgeType.CommitHistory</c> row (if missing), creates per-commit
/// <c>KnowledgeType.Commit</c> child rows, elaborates each child in-process via
/// <see cref="ICommitElaborationLlmClient"/>, writes <c>KnowledgeRelationship.PartOf</c> edges
/// child → parent, updates the parent's rolling-window content, and returns the newest SHA
/// the caller should persist on <c>GitRepository.LastCommitHistorySyncSha</c>.
///
/// WorkGroupID: kc-feat-git-commit-knowledge-sync-20260410
/// NodeID: SelfHostedCommitHistoryParity (NODE-4)
/// </summary>
public interface IGitCommitHistoryService
{
    Task<string?> ProcessCommitsAsync(
        Guid repositoryId,
        IEnumerable<CommitDescriptor> commits,
        Guid vaultId,
        CancellationToken ct);
}
