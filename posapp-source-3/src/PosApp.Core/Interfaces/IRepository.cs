using System.Linq.Expressions;
using PosApp.Core.Entities;

namespace PosApp.Core.Interfaces;

/// <summary>
/// Generic repository contract. Keeps the data layer swappable
/// and the services testable without EF Core.
/// </summary>
public interface IRepository<T> where T : class
{
    Task<T?> GetByIdAsync(int id);
    Task<IReadOnlyList<T>> ListAsync();
    Task<IReadOnlyList<T>> FindAsync(Expression<Func<T, bool>> predicate);
    IQueryable<T> Query();
    Task AddAsync(T entity);
    Task UpdateAsync(T entity);
    Task RemoveAsync(T entity);
    Task<int> SaveChangesAsync();
}
