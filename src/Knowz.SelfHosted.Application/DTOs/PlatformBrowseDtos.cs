namespace Knowz.SelfHosted.Application.DTOs;

/// <summary>
/// Thin DTO for a platform vault as returned to the selfhosted browser UI.
/// Only hand-picked fields are surfaced — the full platform response is never passed through.
/// </summary>
public record PlatformVaultDto(
    Guid Id,
    string Name,
    string? Description,
    int KnowledgeCount,
    DateTime? UpdatedAt);

/// <summary>
/// Paged list of platform vaults returned from the browse proxy.
/// </summary>
public record PlatformVaultListDto(
    IReadOnlyList<PlatformVaultDto> Vaults,
    int TotalCount);

/// <summary>
/// Thin summary of a platform knowledge item.
/// </summary>
public record PlatformKnowledgeSummaryDto(
    Guid Id,
    string Title,
    string? Summary,
    DateTime? UpdatedAt,
    string? CreatedBy);

/// <summary>
/// Paged list of platform knowledge items.
/// </summary>
public record PlatformKnowledgeListDto(
    IReadOnlyList<PlatformKnowledgeSummaryDto> Items,
    int Page,
    int PageSize,
    int TotalCount);

/// <summary>
/// Full knowledge detail returned from the preview endpoint.
/// </summary>
public record PlatformKnowledgeDetailDto(
    Guid Id,
    string Title,
    string? Content,
    string? Summary,
    string? Tags,
    DateTime? UpdatedAt,
    string? CreatedBy);
