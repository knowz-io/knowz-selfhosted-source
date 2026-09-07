namespace Knowz.Core.Interfaces;

/// <summary>
/// Relational persistence boundary. Wraps the EF Core DbContext for the slice of entities
/// consumed by Application-layer services. Future provider swaps (SQL Server → Postgres) live
/// behind this interface. See spec `SelfHostedAbstractionSeams`.
///
/// Exposed DbSets are intentionally narrow — add as services migrate. This is NOT meant to
/// abstract every entity on day one. Adding an entity here is a deliberate decision that the
/// service consuming it benefits from the seam (e.g., for testing or provider-portability).
/// </summary>
/// <remarks>
/// The interface lives in <c>Knowz.Core</c> deliberately to avoid coupling Application-layer
/// consumers to <c>Knowz.SelfHosted.Infrastructure</c>. Concrete entity types referenced here
/// either live in Core or are referenced via <see cref="object"/>/<see cref="IQueryable{T}"/>
/// without requiring the consumer to project from EF entity types.
/// </remarks>
public interface IRelationalStore
{
    /// <summary>
    /// Returns an <see cref="IQueryable{T}"/> for the named entity type. Implementations delegate
    /// to the underlying DbContext's matching <see cref="System.Linq.IQueryable"/>. Returns
    /// <c>null</c> if the entity type is not exposed by this store.
    /// </summary>
    IQueryable<TEntity>? Query<TEntity>() where TEntity : class;

    /// <summary>
    /// Marks an entity as added in the change tracker.
    /// </summary>
    void Add<TEntity>(TEntity entity) where TEntity : class;

    /// <summary>
    /// Marks an entity as updated in the change tracker.
    /// </summary>
    void Update<TEntity>(TEntity entity) where TEntity : class;

    /// <summary>
    /// Marks an entity for removal.
    /// </summary>
    void Remove<TEntity>(TEntity entity) where TEntity : class;

    /// <summary>
    /// Persists all tracked changes. Returns the number of affected rows.
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
