using Knowz.Core.Entities;

namespace Knowz.Core.Interfaces;

public interface IVersioningService
{
    Task<KnowledgeVersion> CreateVersionAsync(Guid knowledgeId, Guid? userId, string? changeDescription, CancellationToken ct);
    Task<List<KnowledgeVersion>> GetVersionsAsync(Guid knowledgeId, CancellationToken ct);
    Task<KnowledgeVersion?> GetVersionAsync(Guid knowledgeId, int versionNumber, CancellationToken ct);
    Task<bool> RestoreVersionAsync(Guid knowledgeId, int versionNumber, Guid? userId, CancellationToken ct);
}
