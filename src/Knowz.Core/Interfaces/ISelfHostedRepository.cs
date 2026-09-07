using Ardalis.Specification;

namespace Knowz.Core.Interfaces;

/// <summary>
/// Generic repository for self-hosted entities with Ardalis.Specification support.
/// Decoupled from Knowz.Domain.BaseEntity — uses ISelfHostedEntity constraint.
/// </summary>
public interface ISelfHostedRepository<T> where T : class, ISelfHostedEntity
{
    Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<List<T>> ListAsync(ISpecification<T> spec, CancellationToken ct = default);
    Task<List<TResult>> ListAsync<TResult>(ISpecification<T, TResult> spec, CancellationToken ct = default);
    Task<T?> FirstOrDefaultAsync(ISpecification<T> spec, CancellationToken ct = default);
    Task<int> CountAsync(ISpecification<T> spec, CancellationToken ct = default);
    Task<T> AddAsync(T entity, CancellationToken ct = default);
    Task UpdateAsync(T entity, CancellationToken ct = default);
    Task SoftDeleteAsync(T entity, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
