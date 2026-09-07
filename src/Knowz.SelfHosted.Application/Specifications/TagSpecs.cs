using Ardalis.Specification;
using Knowz.Core.Entities;

namespace Knowz.SelfHosted.Application.Specifications;

/// <summary>
/// Find tags by a list of names (batch lookup for tag creation).
/// </summary>
public sealed class TagsByNamesSpec : Specification<Tag>
{
    public TagsByNamesSpec(IEnumerable<string> names)
    {
        Query.Where(t => names.Contains(t.Name));
    }
}
