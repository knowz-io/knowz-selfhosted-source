namespace Knowz.SelfHosted.Application.DTOs;

public record TagListItem(Guid Id, string Name, int KnowledgeCount, DateTime CreatedAt);
