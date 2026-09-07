using Ardalis.Specification;
using Knowz.Core.Entities;
using Knowz.Core.Enums;

namespace Knowz.SelfHosted.Application.Specifications;

/// <summary>
/// Get a single knowledge item by ID with all related entities eagerly loaded.
/// </summary>
public sealed class KnowledgeByIdWithRelationsSpec : Specification<Knowledge>, ISingleResultSpecification<Knowledge>
{
    public KnowledgeByIdWithRelationsSpec(Guid id)
    {
        Query
            .Where(k => k.Id == id)
            .Include(k => k.Tags)
            .Include(k => k.KnowledgeVaults).ThenInclude(kv => kv.Vault)
            .Include(k => k.Topic);
    }
}

/// <summary>
/// List knowledge items with optional filters, sorting, and pagination.
/// </summary>
public sealed class KnowledgeListSpec : Specification<Knowledge>
{
    public KnowledgeListSpec(
        int page, int pageSize,
        string sortBy, string sortDir,
        KnowledgeType? type = null,
        string? titleLike = null,
        string? filePathLike = null,
        DateTime? startDate = null,
        DateTime? endDate = null)
    {
        if (type.HasValue)
            Query.Where(k => k.Type == type.Value);

        if (!string.IsNullOrEmpty(titleLike))
            Query.Where(k => k.Title.Contains(titleLike));

        if (!string.IsNullOrEmpty(filePathLike))
            Query.Where(k => k.FilePath != null && k.FilePath.Contains(filePathLike));

        if (startDate.HasValue)
            Query.Where(k => k.CreatedAt >= startDate.Value);

        if (endDate.HasValue)
            Query.Where(k => k.CreatedAt <= endDate.Value);

        // Sorting
        var desc = sortDir.Equals("desc", StringComparison.OrdinalIgnoreCase);
        switch (sortBy.ToLowerInvariant())
        {
            case "title":
                _ = desc ? Query.OrderByDescending(k => k.Title) : Query.OrderBy(k => k.Title);
                break;
            case "updated":
                _ = desc ? Query.OrderByDescending(k => k.UpdatedAt) : Query.OrderBy(k => k.UpdatedAt);
                break;
            default: // "created"
                _ = desc ? Query.OrderByDescending(k => k.CreatedAt) : Query.OrderBy(k => k.CreatedAt);
                break;
        }

        // Pagination
        Query.Skip((page - 1) * pageSize).Take(pageSize);
    }
}

/// <summary>
/// Count-only spec using the same filters as KnowledgeListSpec (no sort/paging).
/// </summary>
public sealed class KnowledgeCountSpec : Specification<Knowledge>
{
    public KnowledgeCountSpec(
        KnowledgeType? type = null,
        string? titleLike = null,
        string? filePathLike = null,
        DateTime? startDate = null,
        DateTime? endDate = null)
    {
        if (type.HasValue)
            Query.Where(k => k.Type == type.Value);

        if (!string.IsNullOrEmpty(titleLike))
            Query.Where(k => k.Title.Contains(titleLike));

        if (!string.IsNullOrEmpty(filePathLike))
            Query.Where(k => k.FilePath != null && k.FilePath.Contains(filePathLike));

        if (startDate.HasValue)
            Query.Where(k => k.CreatedAt >= startDate.Value);

        if (endDate.HasValue)
            Query.Where(k => k.CreatedAt <= endDate.Value);
    }
}

/// <summary>
/// Get knowledge items by a list of IDs (bulk get).
/// </summary>
public sealed class KnowledgeByIdsSpec : Specification<Knowledge>
{
    public KnowledgeByIdsSpec(IEnumerable<Guid> ids)
    {
        Query
            .Where(k => ids.Contains(k.Id))
            .Include(k => k.Tags)
            .Include(k => k.KnowledgeVaults).ThenInclude(kv => kv.Vault);
    }
}

/// <summary>
/// Get knowledge item with its primary vault and ancestors (for search indexing).
/// </summary>
public sealed class KnowledgeWithVaultSpec : Specification<Knowledge>, ISingleResultSpecification<Knowledge>
{
    public KnowledgeWithVaultSpec(Guid id)
    {
        Query
            .Where(k => k.Id == id)
            .Include(k => k.Tags)
            .Include(k => k.KnowledgeVaults).ThenInclude(kv => kv.Vault)
            .Include(k => k.Topic);
    }
}
