using Ardalis.Specification;
using Knowz.Core.Entities;

namespace Knowz.SelfHosted.Application.Specifications;

/// <summary>
/// List topics ordered by name with a limit.
/// </summary>
public sealed class TopicListSpec : Specification<Topic>
{
    public TopicListSpec(int limit)
    {
        Query.OrderBy(t => t.Name).Take(limit);
    }
}

/// <summary>
/// Get a single topic by ID with its knowledge items eagerly loaded.
/// </summary>
public sealed class TopicByIdWithKnowledgeSpec : Specification<Topic>, ISingleResultSpecification<Topic>
{
    public TopicByIdWithKnowledgeSpec(Guid id)
    {
        Query
            .Where(t => t.Id == id)
            .Include(t => t.KnowledgeItems);
    }
}
