namespace Knowz.SelfHosted.Application.DTOs;

using Knowz.SelfHosted.Infrastructure.Data.Entities;

/// <summary>
/// Arguments to begin a new platform sync audit row.
/// </summary>
public record PlatformSyncRunStart(
    Guid UserId,
    string? UserEmail,
    PlatformSyncOperation Operation,
    PlatformSyncDirection Direction,
    Guid? VaultSyncLinkId = null,
    Guid? KnowledgeId = null);

/// <summary>
/// Outcome carried back to <see cref="Knowz.SelfHosted.Application.Interfaces.IPlatformAuditLog.CompleteAsync"/>.
/// <see cref="Status"/> must be <see cref="PlatformSyncRunStatus.Succeeded"/> or
/// <see cref="PlatformSyncRunStatus.Partial"/> — failures go through FailAsync so error sanitization runs.
/// </summary>
public record PlatformSyncRunResult(
    int ItemCount,
    long BytesTransferred,
    PlatformSyncRunStatus Status);

/// <summary>
/// Display-safe projection of a <see cref="PlatformSyncRun"/>.
/// Never contains API keys, URLs with auth, or raw platform response bodies.
/// </summary>
public record PlatformSyncRunDto(
    Guid Id,
    Guid? VaultSyncLinkId,
    Guid UserId,
    string? UserEmail,
    PlatformSyncOperation Operation,
    PlatformSyncDirection Direction,
    Guid? KnowledgeId,
    int ItemCount,
    long BytesTransferred,
    PlatformSyncRunStatus Status,
    string? ErrorMessage,
    DateTime StartedAt,
    DateTime? CompletedAt);
