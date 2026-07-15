using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using PosApp.Core.Interfaces;

namespace PosApp.Data.Repositories;

/// <summary>
/// EF Core-backed generic repository. Service classes consume it through
/// <see cref="IRepository{T}"/> to keep persistence swappable.
/// </summary>
public class EfRepository<T> : IRepository<T> where T : class
{
    private readonly AppDbContext _db;
    private readonly DbSet<T> _set;

    public EfRepository(AppDbContext db)
    {
        _db = db;
        _set = db.Set<T>();
    }

    public async Task<T?> GetByIdAsync(int id) => await _set.FindAsync(id);

    public async Task<IReadOnlyList<T>> ListAsync() => await _set.AsNoTracking().ToListAsync();

    public async Task<IReadOnlyList<T>> FindAsync(Expression<Func<T, bool>> predicate)
        => await _set.AsNoTracking().Where(predicate).ToListAsync();

    public IQueryable<T> Query() => _set.AsQueryable();

    public async Task AddAsync(T entity) => await _set.AddAsync(entity);

    public Task UpdateAsync(T entity)
    {
        _set.Update(entity);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(T entity)
    {
        _set.Remove(entity);
        return Task.CompletedTask;
    }

    public Task<int> SaveChangesAsync() => _db.SaveChangesAsync();
}
