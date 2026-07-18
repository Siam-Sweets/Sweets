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
        => await _db.Discounts.AsNoTracking().OrderByDescending(d => d.IsActive).ThenBy(d => d.Name).ToListAsync();

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
        Validate(discount);
        var normalizedCode = Normalize(discount.Code);
        if (normalizedCode != null && await _db.Discounts.AsNoTracking().AnyAsync(d =>
                d.Id != discount.Id && d.Code != null && d.Code.ToLower() == normalizedCode.ToLower()))
            throw new InvalidOperationException("Another promotion already uses this code.");

        if (discount.Id == 0)
        {
            discount.Name = discount.Name.Trim();
            discount.Code = normalizedCode;
            discount.CreatedAt = DateTime.UtcNow;
            _db.Discounts.Add(discount);
            await _db.SaveChangesAsync();
            return discount;
        }

        var tracked = await _db.Discounts.FindAsync(discount.Id)
            ?? throw new InvalidOperationException("Promotion not found.");
        tracked.Name = discount.Name.Trim();
        tracked.Description = Normalize(discount.Description);
        tracked.Code = normalizedCode;
        tracked.Type = discount.Type;
        tracked.Value = discount.Value;
        tracked.ValidFrom = discount.ValidFrom;
        tracked.ValidTo = discount.ValidTo;
        tracked.MaxUses = discount.MaxUses;
        tracked.IsActive = discount.IsActive;
        await _db.SaveChangesAsync();
        return tracked;
    }

    public async Task SetActiveAsync(int id, bool isActive)
    {
        var discount = await _db.Discounts.FindAsync(id)
            ?? throw new InvalidOperationException("Promotion not found.");
        discount.IsActive = isActive;
        await _db.SaveChangesAsync();
    }

    public Task DeactivateAsync(int id) => SetActiveAsync(id, false);

    private static void Validate(Discount discount)
    {
        if (string.IsNullOrWhiteSpace(discount.Name))
            throw new InvalidOperationException("Promotion name is required.");
        if (discount.Value <= 0m || discount.Type == DiscountType.Percentage && discount.Value > 100m)
            throw new InvalidOperationException("Enter a valid promotion value.");
        if (discount.ValidFrom.HasValue && discount.ValidTo.HasValue && discount.ValidTo < discount.ValidFrom)
            throw new InvalidOperationException("The end date cannot be before the start date.");
        if (discount.MaxUses < 0)
            throw new InvalidOperationException("Maximum uses cannot be negative.");
        if (discount.UsedCount < 0)
            throw new InvalidOperationException("Used count cannot be negative.");
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
