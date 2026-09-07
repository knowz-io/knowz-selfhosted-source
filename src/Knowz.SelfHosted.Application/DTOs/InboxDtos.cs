namespace Knowz.SelfHosted.Application.DTOs;

public record InboxItemResult(Guid Id, bool Created);

public record InboxItemDto(Guid Id, string Body, string Type, Guid? CreatedByUserId, DateTime CreatedAt, DateTime UpdatedAt);

public record InboxListResponse(List<InboxItemDto> Items, int Page, int PageSize, int TotalItems, int TotalPages, string InboxVisibilityScope);

public record ConvertToKnowledgeResult(Guid InboxItemId, Guid KnowledgeId, string Title, bool Converted);

public record BatchConvertResult(int Requested, int Converted, int Failed, List<ConvertToKnowledgeResult> Results);
