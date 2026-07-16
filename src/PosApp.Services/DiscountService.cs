using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Data;

namespace PosApp.Services;

public sealed class DiscountService : IDiscountService
{
    private readonly AppDbContext _db;
    public DiscountService(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<Discount>> GetAllAsync()
        => await _db.Discounts.AsNoTracking().OrderBy(d => d.Name).ToListAsync();

    public async Task<IReadOnlyList<Discount>> GetActiveAsync(DateTime? at = null)
    {
        var moment = at ?? DateTime.Now;
        return await _db.Discounts.AsNoTracking()
            .Where(d => d.IsActive &&
                        (!d.ValidFrom.HasValue || d.ValidFrom.Value <= moment) &&
                        (!d.ValidTo.HasValue || d.ValidTo.Value >= moment) &&
                        (!d.MaxUses.HasValue || d.UsedCount < d.MaxUses.Value))
            .OrderBy(d => d.Name)
            .ToListAsync();
    }

    public async Task<Discount> SaveAsync(Discount discount)
    {
        if (string.IsNullOrWhiteSpace(discount.Name))
            throw new InvalidOperationException("Promotion name is required.");
        if (discount.Value < 0m || discount.Type == DiscountType.Percentage && discount.Value > 100m)
            throw new InvalidOperationException("Enter a valid promotion value.");
        if (discount.ValidFrom.HasValue && discount.ValidTo.HasValue && discount.ValidTo < discount.ValidFrom)
            throw new InvalidOperationException("The end date cannot be before the start date.");

        if (discount.Id == 0)
        {
            discount.CreatedAt = DateTime.UtcNow;
            _db.Discounts.Add(discount);
        }
        else
        {
            _db.Discounts.Update(discount);
        }
        await _db.SaveChangesAsync();
        return discount;
    }

    public async Task DeactivateAsync(int id)
    {
        var discount = await _db.Discounts.FindAsync(id)
            ?? throw new InvalidOperationException("Promotion not found.");
        discount.IsActive = false;
        await _db.SaveChangesAsync();
    }
}
