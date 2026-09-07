namespace Knowz.SelfHosted.Application.Services.GitCommitHistory;

/// <summary>
/// Walker-produced descriptor for a single commit. Mirrors platform's
/// <c>Knowz.Shared.DTOs.Git.CommitDescriptor</c> — selfhosted has no Knowz.Shared reference
/// so the type is defined locally.
///
/// Path + line-count stats only — NO diff body field, by design.
/// CRIT-2 enforcement at the DTO layer: the shape itself prevents raw diffs from being
/// carried through the ingestion path.
///
/// WorkGroupID: kc-feat-git-commit-knowledge-sync-20260410
/// NodeID: SelfHostedCommitHistoryParity (NODE-4)
/// </summary>
public sealed record CommitDescriptor(
    string Sha,
    IReadOnlyList<string> ParentShas,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthoredAt,
    DateTimeOffset CommittedAt,
    string Message,
    IReadOnlyList<CommitChangedFile> ChangedFiles);

/// <summary>
/// File change descriptor: path + line-count stats only. No diff body.
/// </summary>
public sealed record CommitChangedFile(
    string Path,
    int LinesAdded,
    int LinesDeleted,
    GitCommitChangeType Type);

/// <summary>
/// In-process elaboration request. Selfhosted has no Service Bus, so the producer
/// (<see cref="GitCommitHistoryService"/>) hands this record directly to the in-process
/// elaboration entry point. Shape intentionally mirrors platform's
/// <c>GitCommitElaborationMessage</c> so the selfhosted + platform code paths stay comparable.
///
/// CRIT-2: no raw-diff field.
/// CRIT-3: TenantId is carried so downstream logic can reseed tenant context.
/// CRIT-5: KnowledgeId refers to the pre-created stub (idempotency anchor).
/// </summary>
public sealed record CommitElaborationRequest(
    Guid TenantId,
    Guid KnowledgeId,
    Guid ParentKnowledgeId,
    Guid RepositoryId,
    Guid VaultId,
    string CommitSha,
    string CommitMessage,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthoredAt,
    IReadOnlyList<CommitChangedFile> ChangedFiles);
