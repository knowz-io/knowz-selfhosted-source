namespace Knowz.SelfHosted.Application.DTOs;

/// <summary>
/// Read-model DTO for a single commit that modified a knowledge item.
/// Hydrated from the commit child Knowledge row's PlatformData JSON.
///
/// WorkGroupID: kc-feat-commit-knowledge-link-20260410-230500
/// NodeID: SelfHostedKnowledgeCommitHistoryQuery
/// </summary>
public sealed record CommitHistoryEntry(
    Guid KnowledgeId,
    string Sha,
    string ShortSha,
    string Title,
    string AuthorName,
    DateTime CommittedAt,
    int ChangedFileCount,
    int LinesAdded,
    int LinesDeleted,
    string Content);

/// <summary>Paginated envelope for <see cref="CommitHistoryEntry"/> results.</summary>
public sealed record CommitHistoryResponse(
    IReadOnlyList<CommitHistoryEntry> Items,
    int Total,
    int Page,
    int PageSize);
