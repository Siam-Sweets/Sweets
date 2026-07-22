using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Data;

namespace PosApp.Services;

public sealed class StoreService : IStoreService
{
    private readonly AppDbContext _db;
    private readonly IStoreContext _context;

    public StoreService(AppDbContext db, IStoreContext context)
    {
        _db = db;
        _context = context;
    }

    public async Task InitializeAsync()
    {
        var stores = await _db.Stores.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        var selected = stores.FirstOrDefault(x => x.Id == _context.StoreId && x.IsActive)
                       ?? stores.FirstOrDefault(x => x.IsActive)
                       ?? throw new InvalidOperationException("No active store is available.");
        _context.SetCurrentStore(selected);
    }

    public async Task<Store> GetCurrentStoreAsync()
        => await _db.Stores.AsNoTracking().FirstOrDefaultAsync(x => x.Id == _context.StoreId)
           ?? throw new InvalidOperationException("The selected store no longer exists.");

    public async Task<IReadOnlyList<Store>> GetStoresAsync(bool includeInactive = true)
    {
        var query = _db.Stores.AsNoTracking();
        if (!includeInactive) query = query.Where(x => x.IsActive);
        return await query.OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync();
    }

    public async Task<Store> SaveStoreAsync(Store store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var name = Normalize(store.Name, 100, "Store name", true);
        var code = Normalize(store.Code, 24, "Store code", true).ToUpperInvariant();
        var address = Normalize(store.Address, 500, "Address", false);
        var phone = Normalize(store.Phone, 30, "Phone", false);

        var duplicate = await _db.Stores.AnyAsync(x => x.Id != store.Id && x.Code == code);
        if (duplicate) throw new InvalidOperationException("That store code is already in use.");

        if (store.Id == 0)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();
            try
            {
                var created = new Store
                {
                    Name = name,
                    Code = code,
                    Address = address,
                    Phone = phone,
                    IsActive = true
                };
                _db.Stores.Add(created);
                await _db.SaveChangesAsync();
                await SeedNewStoreAsync(created);
                await transaction.CommitAsync();
                return created;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        var existing = await _db.Stores.FirstOrDefaultAsync(x => x.Id == store.Id)
                       ?? throw new InvalidOperationException("Store not found.");
        existing.Name = name;
        existing.Code = code;
        existing.Address = address;
        existing.Phone = phone;
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await UpdateStoreConfigurationNameAsync(existing);
        SettingsService.InvalidateStoreCache(existing.Id);
        return existing;
    }

    public async Task SetStoreActiveAsync(int storeId, bool isActive)
    {
        var store = await _db.Stores.FirstOrDefaultAsync(x => x.Id == storeId)
                    ?? throw new InvalidOperationException("Store not found.");
        if (!isActive)
        {
            if (store.Id == _context.StoreId)
                throw new InvalidOperationException("Switch to another store before deactivating the current store.");
            if (await _db.Stores.CountAsync(x => x.IsActive) <= 1)
                throw new InvalidOperationException("At least one store must remain active.");
        }
        store.IsActive = isActive;
        store.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task SelectStoreAsync(int storeId)
    {
        var store = await _db.Stores.AsNoTracking().FirstOrDefaultAsync(x => x.Id == storeId)
                    ?? throw new InvalidOperationException("Store not found.");
        if (!store.IsActive) throw new InvalidOperationException("The selected store is inactive.");
        _context.SetCurrentStore(store);
    }

    private async Task SeedNewStoreAsync(Store store)
    {
        var sourceUsers = await _db.Users.AsNoTracking().ToListAsync();
        if (!sourceUsers.Any(x => x.IsActive && x.Role == UserRole.Admin))
            throw new InvalidOperationException("The current store must have an active administrator before another store can be created.");
        foreach (var source in sourceUsers)
        {
            _db.Users.Add(new User
            {
                StoreId = store.Id,
                Username = source.Username,
                FullName = source.FullName,
                PasswordHash = source.PasswordHash,
                PasswordSalt = source.PasswordSalt,
                Role = source.Role,
                IsActive = source.IsActive,
                Email = source.Email
            });
        }

        var categories = new[]
        {
            ("Beverages", "#2D7FF9", 1), ("Snacks", "#F59E0B", 2),
            ("Groceries", "#10B981", 3), ("Household", "#8B5CF6", 4),
            ("Personal Care", "#EC4899", 5), ("Produce", "#22C55E", 6)
        };
        foreach (var (name, color, sort) in categories)
            _db.Categories.Add(new Category { StoreId = store.Id, Name = name, Color = color, SortOrder = sort });

        _db.Taxes.AddRange(
            new Tax { StoreId = store.Id, Name = "VAT", Rate = 15m, IsDefault = true, IsActive = true },
            new Tax { StoreId = store.Id, Name = "Service Charge", Rate = 5m, IsActive = true });
        _db.Discounts.AddRange(
            new Discount { StoreId = store.Id, Name = "5% Off", Type = DiscountType.Percentage, Value = 5m, Code = "SAVE5", IsActive = true },
            new Discount { StoreId = store.Id, Name = "Senior Citizen", Type = DiscountType.Percentage, Value = 10m, IsActive = true },
            new Discount { StoreId = store.Id, Name = "50 Off", Type = DiscountType.FixedAmount, Value = 50m, Code = "BD50", IsActive = true });

        var settings = new StoreSettings
        {
            StoreName = store.Name,
            Address = store.Address ?? string.Empty,
            Phone = store.Phone ?? string.Empty
        };
        _db.Settings.AddRange(
            new Setting { StoreId = store.Id, Key = "store:config", Value = JsonSerializer.Serialize(settings) },
            new Setting { StoreId = store.Id, Key = SetupService.SetupCompleteKey, Value = "true" });
        await _db.SaveChangesAsync();
    }

    private async Task UpdateStoreConfigurationNameAsync(Store store)
    {
        var setting = await _db.Settings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.StoreId == store.Id && x.Key == "store:config");
        var settings = new StoreSettings();
        if (!string.IsNullOrWhiteSpace(setting?.Value))
        {
            try { settings = JsonSerializer.Deserialize<StoreSettings>(setting.Value) ?? new StoreSettings(); }
            catch { settings = new StoreSettings(); }
        }
        settings.StoreName = store.Name;
        settings.Address = store.Address ?? string.Empty;
        settings.Phone = store.Phone ?? string.Empty;
        if (setting == null)
        {
            _db.Settings.Add(new Setting
            {
                StoreId = store.Id,
                Key = "store:config",
                Value = JsonSerializer.Serialize(settings)
            });
        }
        else
        {
            setting.Value = JsonSerializer.Serialize(settings);
            setting.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
    }

    private static string Normalize(string? value, int max, string field, bool required)
    {
        var text = value?.Trim() ?? string.Empty;
        if (required && text.Length == 0) throw new InvalidOperationException($"{field} is required.");
        if (text.Length > max) throw new InvalidOperationException($"{field} cannot exceed {max} characters.");
        return text;
    }
}
