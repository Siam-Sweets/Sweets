using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Data;

namespace PosApp.Services;

public class CustomerService : ICustomerService
{
    private readonly AppDbContext _db;
    public CustomerService(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<Customer>> SearchCustomersAsync(string? query)
    {
        var q = _db.Customers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            q = q.Where(c => c.Name.Contains(term)
                || (c.Phone != null && c.Phone.Contains(term))
                || (c.Email != null && c.Email.Contains(term)));
        }
        return await q.OrderBy(c => c.Name).ToListAsync();
    }

    public async Task<Customer?> GetCustomerAsync(int id)
        => await _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);

    public async Task<Customer> CreateOrUpdateCustomerAsync(Customer customer)
    {
        if (customer.Id == 0)
        {
            customer.CreatedAt = DateTime.UtcNow;
            _db.Customers.Add(customer);
        }
        else
        {
            customer.UpdatedAt = DateTime.UtcNow;
            _db.Customers.Update(customer);
        }
        await _db.SaveChangesAsync();
        return customer;
    }

    public async Task DeleteCustomerAsync(int id)
    {
        var inUse = await _db.Sales.AnyAsync(s => s.CustomerId == id);
        if (inUse) throw new InvalidOperationException("Customer has sales history; consider deactivating instead.");
        var customer = await _db.Customers.FindAsync(id);
        if (customer != null)
        {
            _db.Customers.Remove(customer);
            await _db.SaveChangesAsync();
        }
    }

    public async Task<IReadOnlyList<Sale>> GetCustomerHistoryAsync(int customerId)
    {
        return await _db.Sales.AsNoTracking()
            .Include(s => s.Items)
            .Where(s => s.CustomerId == customerId)
            .OrderByDescending(s => s.SaleDate)
            .ToListAsync();
    }

}
