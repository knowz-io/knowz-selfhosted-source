namespace Knowz.SelfHosted.Application.DTOs;

public record EntityItem(Guid Id, string Name, DateTime CreatedAt);

public record EntitySearchResponse(string EntityType, List<EntityItem> Entities);
