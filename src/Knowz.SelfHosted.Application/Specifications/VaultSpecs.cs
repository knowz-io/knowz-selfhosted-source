using Ardalis.Specification;
using Knowz.Core.Entities;

namespace Knowz.SelfHosted.Application.Specifications;

/// <summary>
/// List all vaults ordered by name.
/// </summary>
public sealed class VaultListSpec : Specification<Vault>
{
    public VaultListSpec()
    {
        Query.OrderBy(v => v.Name);
    }
}

/// <summary>
/// Get a vault by ID with its knowledge items count.
/// </summary>
public sealed class VaultByIdSpec : Specification<Vault>, ISingleResultSpecification<Vault>
{
    public VaultByIdSpec(Guid id)
    {
        Query.Where(v => v.Id == id);
    }
}
