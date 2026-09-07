using Ardalis.Specification;
using Knowz.Core.Entities;

namespace Knowz.SelfHosted.Application.Specifications;

/// <summary>
/// Search persons by name pattern, ordered and limited.
/// </summary>
public sealed class PersonSearchSpec : Specification<Person>
{
    public PersonSearchSpec(string? query, int limit)
    {
        if (!string.IsNullOrEmpty(query))
            Query.Where(p => p.Name.Contains(query));

        Query.OrderBy(p => p.Name).Take(limit);
    }
}

/// <summary>
/// Search locations by name pattern, ordered and limited.
/// </summary>
public sealed class LocationSearchSpec : Specification<Location>
{
    public LocationSearchSpec(string? query, int limit)
    {
        if (!string.IsNullOrEmpty(query))
            Query.Where(l => l.Name.Contains(query));

        Query.OrderBy(l => l.Name).Take(limit);
    }
}

/// <summary>
/// Search events by name pattern, ordered and limited.
/// </summary>
public sealed class EventSearchSpec : Specification<Event>
{
    public EventSearchSpec(string? query, int limit)
    {
        if (!string.IsNullOrEmpty(query))
            Query.Where(e => e.Name.Contains(query));

        Query.OrderBy(e => e.Name).Take(limit);
    }
}
