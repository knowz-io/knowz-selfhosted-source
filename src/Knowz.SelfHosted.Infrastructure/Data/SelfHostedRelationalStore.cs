namespace Knowz.SelfHosted.Infrastructure.Data;

using Knowz.Core.Interfaces;

/// <summary>
/// Default adapter that exposes <see cref="IRelationalStore"/> over <see cref="SelfHostedDbContext"/>.
/// Implements <c>SelfHostedAbstractionSeams</c> spec — pure delegating wrap, zero behavior change.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Query{TEntity}"/> uses <see cref="Microsoft.EntityFrameworkCore.DbContext.Set{TEntity}"/>
/// which returns an <see cref="IQueryable{T}"/> for any tracked entity type — including the global
/// query filters configured on <see cref="SelfHostedDbContext"/> (e.g. soft-delete + tenant scope).
/// Consumers therefore get the same filter semantics as direct DbContext access.
/// </para>
/// <para>
/// This adapter is lifetime-equivalent to the DbContext it wraps (scoped). The intent is for a
/// service that already injects <see cref="SelfHostedDbContext"/> to be able to swap in
/// <see cref="IRelationalStore"/> for testability without a behavior change.
/// </para>
/// </remarks>
internal sealed class SelfHostedRelationalStore : IRelationalStore
{
    private readonly SelfHostedDbContext _db;

    public SelfHostedRelationalStore(SelfHostedDbContext db)
    {
        _db = db;
    }

    public IQueryable<TEntity>? Query<TEntity>() where TEntity : class
    {
        try
        {
            return _db.Set<TEntity>();
        }
        catch (InvalidOperationException)
        {
            // EF throws when TEntity is not part of the model — return null per contract.
            return null;
        }
    }

    public void Add<TEntity>(TEntity entity) where TEntity : class => _db.Add(entity);

    public void Update<TEntity>(TEntity entity) where TEntity : class => _db.Update(entity);

    public void Remove<TEntity>(TEntity entity) where TEntity : class => _db.Remove(entity);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => _db.SaveChangesAsync(cancellationToken);
}
