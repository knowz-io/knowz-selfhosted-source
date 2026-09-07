using Ardalis.Specification;
using Ardalis.Specification.EntityFrameworkCore;
using Knowz.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Infrastructure.Data;

/// <summary>
/// Generic repository for self-hosted entities using Ardalis.Specification.
/// Self-contained — no coupling to Knowz.Domain or Knowz.Infrastructure.
/// </summary>
public class SelfHostedRepository<T> : ISelfHostedRepository<T> where T : class, ISelfHostedEntity
{
    private readonly SelfHostedDbContext _db;
    private readonly DbSet<T> _dbSet;
    private readonly ISpecificationEvaluator _specEvaluator;

    public SelfHostedRepository(SelfHostedDbContext db)
    {
        _db = db;
        _dbSet = db.Set<T>();
        _specEvaluator = SpecificationEvaluator.Default;
    }

    public async Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _dbSet.FindAsync(new object[] { id }, ct);
    }

    public async Task<List<T>> ListAsync(ISpecification<T> spec, CancellationToken ct = default)
    {
        return await _specEvaluator.GetQuery(_dbSet, spec).ToListAsync(ct);
    }

    public async Task<List<TResult>> ListAsync<TResult>(ISpecification<T, TResult> spec, CancellationToken ct = default)
    {
        return await _specEvaluator.GetQuery(_dbSet, spec).ToListAsync(ct);
    }

    public async Task<T?> FirstOrDefaultAsync(ISpecification<T> spec, CancellationToken ct = default)
    {
        return await _specEvaluator.GetQuery(_dbSet, spec).FirstOrDefaultAsync(ct);
    }

    public async Task<int> CountAsync(ISpecification<T> spec, CancellationToken ct = default)
    {
        return await _specEvaluator.GetQuery(_dbSet, spec, true).CountAsync(ct);
    }

    public async Task<T> AddAsync(T entity, CancellationToken ct = default)
    {
        entity.CreatedAt = DateTime.UtcNow;
        entity.UpdatedAt = DateTime.UtcNow;
        await _dbSet.AddAsync(entity, ct);
        return entity;
    }

    public Task UpdateAsync(T entity, CancellationToken ct = default)
    {
        entity.UpdatedAt = DateTime.UtcNow;
        _dbSet.Update(entity);
        return Task.CompletedTask;
    }

    public Task SoftDeleteAsync(T entity, CancellationToken ct = default)
    {
        entity.IsDeleted = true;
        entity.UpdatedAt = DateTime.UtcNow;
        _dbSet.Update(entity);
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _db.SaveChangesAsync(ct);
    }
}
