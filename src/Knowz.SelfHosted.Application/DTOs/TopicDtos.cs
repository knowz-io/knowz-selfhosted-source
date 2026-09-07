namespace Knowz.SelfHosted.Application.DTOs;

public record TopicListItem(Guid Id, string Name, string? Description, int KnowledgeCount);

public record TopicListResponse(List<TopicListItem> Topics, int TotalCount);

public record TopicDetailResponse(
    Guid Id,
    string Name,
    string? Description,
    IEnumerable<KnowledgeListItem> KnowledgeItems);
