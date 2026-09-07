namespace Knowz.SelfHosted.Application.Services.GitCommitHistory;

/// <summary>
/// Type of change for a file in a git commit (selfhosted mirror of
/// <c>Knowz.Domain.Enums.GitChangeType</c> — Knowz.Domain is not referenced from selfhosted).
///
/// WorkGroupID: kc-feat-git-commit-knowledge-sync-20260410
/// NodeID: SelfHostedCommitHistoryParity (NODE-4)
/// </summary>
public enum GitCommitChangeType
{
    Added,
    Modified,
    Deleted,
    Renamed
}
