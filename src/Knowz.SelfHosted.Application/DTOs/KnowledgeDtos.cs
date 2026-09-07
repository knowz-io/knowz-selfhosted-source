namespace Knowz.SelfHosted.Application.DTOs;

public record KnowledgeItemResponse(
    Guid Id,
    string Title,
    string Content,
    string? Summary,
    string? BriefSummary,
    string? SummaryRefinementGuidance,
    string Type,
    string? Source,
    string? FilePath,
    TopicRef? Topic,
    IEnumerable<string> Tags,
    IEnumerable<VaultRef> Vaults,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool IsIndexed,
    DateTime? IndexedAt);

public record KnowledgeListItem(
    Guid Id,
    string Title,
    string Summary,
    string Type,
    string? FilePath,
    Guid? VaultId,
    string? VaultName,
    Guid? CreatedByUserId,
    string? CreatedByUserName,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool IsIndexed);

public record CreatorRef(Guid Id, string Name);

public record KnowledgeListResponse(
    List<KnowledgeListItem> Items,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages);

public record CreateKnowledgeResult(Guid Id, string Title, bool Created);

public record UpdateKnowledgeResult(Guid Id, string Title, bool Updated);

public record DeleteResult(Guid Id, bool Deleted);

public record ReprocessResult(Guid Id, string Title, bool Reprocessed);

public record KnowledgeStatsResponse(
    int TotalKnowledgeItems,
    List<TypeCount> ByType,
    List<VaultCount> ByVault,
    DateRange? DateRange);

public record BulkGetResponse(
    List<KnowledgeItemResponse> Items,
    int RequestedCount,
    int ReturnedCount);

public record CountResponse(int Count);

public record BatchMoveResult(int RequestedCount, int MovedCount, List<Guid> MovedIds, List<Guid> NotFoundIds);

public record TopicRef(Guid Id, string Name);

public record VaultRef(Guid Id, string Name, bool IsPrimary);

public record TypeCount(string Type, int Count);

public record VaultCount(string Vault, int Count);

public record DateRange(DateTime Earliest, DateTime Latest);
