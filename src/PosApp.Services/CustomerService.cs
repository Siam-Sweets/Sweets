using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Data;

namespace PosApp.Services;

public class CustomerService : ICustomerService
{
    private readonly AppDbContext _db;
    public CustomerService(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<Customer>> SearchCustomersAsync(string? query, bool includeInactive = false)
    {
        var customers = _db.Customers.AsNoTracking().AsQueryable();
        if (!includeInactive) customers = customers.Where(c => c.IsActive);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            customers = customers.Where(c => EF.Functions.Like(c.Name, $"%{term}%")
                || (c.Phone != null && EF.Functions.Like(c.Phone, $"%{term}%"))
                || (c.Email != null && EF.Functions.Like(c.Email, $"%{term}%")));
        }
        return await customers.OrderByDescending(c => c.IsActive).ThenBy(c => c.Name).ToListAsync();
    }

    public async Task<Customer?> GetCustomerAsync(int id)
    {
        if (id <= 0) return null;
        return await _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
    }

    public async Task<Customer> CreateOrUpdateCustomerAsync(Customer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);
        var name = customer.Name?.Trim() ?? string.Empty;
        var phone = Normalize(customer.Phone);
        var email = Normalize(customer.Email);
        var address = Normalize(customer.Address);
        var taxId = Normalize(customer.TaxId);
        ValidateLength(name, 100, "Customer name", required: true);
        ValidateLength(phone, 20, "Phone");
        ValidateLength(email, 100, "Email");
        ValidateLength(address, 300, "Address");
        ValidateLength(taxId, 20, "Tax ID");
        if (customer.LoyaltyPoints < 0m || customer.StoreCredit < 0m || customer.LoyaltyRate < 0m)
            throw new InvalidOperationException("Customer balances and loyalty rate cannot be negative.");

        if (customer.Id == 0)
        {
            var created = new Customer
            {
                Name = name, Phone = phone, Email = email, Address = address, TaxId = taxId,
                LoyaltyPoints = customer.LoyaltyPoints, StoreCredit = customer.StoreCredit,
                LoyaltyRate = customer.LoyaltyRate, IsActive = true,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            };
            _db.Customers.Add(created);
            await _db.SaveChangesAsync();
            customer.Id = created.Id;
            customer.IsActive = true;
            customer.CreatedAt = created.CreatedAt;
            customer.UpdatedAt = created.UpdatedAt;
            return created;
        }

        var tracked = await _db.Customers.FindAsync(customer.Id)
            ?? throw new InvalidOperationException("Customer not found.");
        tracked.Name = name;
        tracked.Phone = phone;
        tracked.Email = email;
        tracked.Address = address;
        tracked.TaxId = taxId;
        tracked.LoyaltyPoints = customer.LoyaltyPoints;
        tracked.StoreCredit = customer.StoreCredit;
        tracked.LoyaltyRate = customer.LoyaltyRate;
        tracked.IsActive = customer.IsActive;
        tracked.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return tracked;
    }

    public async Task SetCustomerActiveAsync(int id, bool isActive)
    {
        if (id <= 0) throw new InvalidOperationException("Select a valid customer.");
        var customer = await _db.Customers.FindAsync(id)
            ?? throw new InvalidOperationException("Customer not found.");
        customer.IsActive = isActive;
        customer.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<Sale>> GetCustomerHistoryAsync(int customerId)
    {
        if (customerId <= 0) return Array.Empty<Sale>();
        return await _db.Sales.AsNoTracking()
            .Include(s => s.Items)
            .Where(s => s.CustomerId == customerId &&
                        (s.Status == SaleStatus.Completed || s.Status == SaleStatus.Refunded))
            .OrderByDescending(s => s.SaleDate)
            .ToListAsync();
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ValidateLength(string? value, int max, string field, bool required = false)
    {
        if (required && string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{field} is required.");
        if (value?.Length > max)
            throw new InvalidOperationException($"{field} cannot exceed {max} characters.");
    }
}
