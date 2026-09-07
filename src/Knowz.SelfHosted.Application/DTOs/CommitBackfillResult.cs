namespace Knowz.SelfHosted.Application.DTOs;

/// <summary>
/// Result of running <see cref="Services.GitCommitHistory.CommitRelinkService.RelinkRepositoryAsync"/>
/// over the existing commit-knowledge rows for a single repository.
///
/// <para>
/// <b>Processed</b> — total commit rows visited (regardless of whether they produced new edges).
/// <b>Linked</b> — total NEW <c>References</c> edges created by this invocation. Idempotent re-runs
/// with no new files between them will return <c>Linked = 0</c>.
/// <b>Skipped</b> — commit rows whose <c>PlatformData</c> JSON is missing the
/// <c>changedFilePaths</c> key. These rows were ingested before NODE-3 landed and cannot be
/// relinked from this endpoint — the repository must be re-synced to repopulate the key.
/// </para>
///
/// WorkGroupID: kc-feat-commit-history-polish-20260411-051000
/// NodeID: NODE-3 CommitBackfillEndpoint
/// </summary>
public sealed record CommitBackfillResult(
    int Processed,
    int Linked,
    int Skipped);
