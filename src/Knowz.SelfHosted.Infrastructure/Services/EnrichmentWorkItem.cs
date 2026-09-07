namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// In-memory work item for the enrichment Channel.
/// Lightweight -- only carries IDs. The background service loads
/// full entity from DB when processing.
/// </summary>
public record EnrichmentWorkItem(Guid KnowledgeId, Guid TenantId);
