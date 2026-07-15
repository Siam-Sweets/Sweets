using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Models;

namespace PosApp.Data;

/// <summary>
/// Seeds first-run default data: an admin user, default categories,
/// sample products, default tax and discount, store settings.
/// </summary>
public static class DbSeeder
{
    public static async Task SeedAsync(AppDbContext db)
    {
        await db.Database.MigrateAsync();

        if (!await db.Users.AnyAsync())
        {
            var (hash, salt) = HashPin("1234");
            db.Users.Add(new User
            {
                Username = "admin",
                FullName = "Administrator",
                PasswordHash = hash,
                PasswordSalt = salt,
                Role = UserRole.Admin,
                IsActive = true
            });

            var (cashierHash, cashierSalt) = HashPin("1111");
            db.Users.Add(new User
            {
                Username = "cashier",
                FullName = "Default Cashier",
                PasswordHash = cashierHash,
                PasswordSalt = cashierSalt,
                Role = UserRole.Cashier,
                IsActive = true
            });
        }

        if (!await db.Categories.AnyAsync())
        {
            var cats = new[]
            {
                new Category { Name = "Beverages", Color = "#2D7FF9", SortOrder = 1 },
                new Category { Name = "Snacks", Color = "#F59E0B", SortOrder = 2 },
                new Category { Name = "Groceries", Color = "#10B981", SortOrder = 3 },
                new Category { Name = "Household", Color = "#8B5CF6", SortOrder = 4 },
                new Category { Name = "Personal Care", Color = "#EC4899", SortOrder = 5 },
                new Category { Name = "Produce", Color = "#22C55E", SortOrder = 6 }
            };
            db.Categories.AddRange(cats);
            await db.SaveChangesAsync();
        }

        if (!await db.Products.AnyAsync())
        {
            var beverages = await db.Categories.FirstAsync(c => c.Name == "Beverages");
            var snacks = await db.Categories.FirstAsync(c => c.Name == "Snacks");
            var groceries = await db.Categories.FirstAsync(c => c.Name == "Groceries");
            var produce = await db.Categories.FirstAsync(c => c.Name == "Produce");

            db.Products.AddRange(new[]
            {
                new Product { Name = "Mineral Water 500ml", Sku = "BV-001", CategoryId = beverages.Id, Price = 20m, CostPrice = 12m, StockQuantity = 100, LowStockThreshold = 10 },
                new Product { Name = "Cola 330ml", Sku = "BV-002", CategoryId = beverages.Id, Price = 40m, CostPrice = 28m, StockQuantity = 80, LowStockThreshold = 10 },
                new Product { Name = "Orange Juice 1L", Sku = "BV-003", CategoryId = beverages.Id, Price = 180m, CostPrice = 140m, StockQuantity = 24, LowStockThreshold = 5 },
                new Product { Name = "Green Tea 25bag", Sku = "BV-004", CategoryId = beverages.Id, Price = 120m, CostPrice = 85m, StockQuantity = 30, LowStockThreshold = 5 },
                new Product { Name = "Potato Chips 50g", Sku = "SN-001", CategoryId = snacks.Id, Price = 35m, CostPrice = 22m, StockQuantity = 60, LowStockThreshold = 10 },
                new Product { Name = "Chocolate Bar", Sku = "SN-002", CategoryId = snacks.Id, Price = 60m, CostPrice = 40m, StockQuantity = 50, LowStockThreshold = 10 },
                new Product { Name = "Cookies Pack", Sku = "SN-003", CategoryId = snacks.Id, Price = 75m, CostPrice = 50m, StockQuantity = 40, LowStockThreshold = 5 },
                new Product { Name = "Rice 5kg", Sku = "GR-001", CategoryId = groceries.Id, Price = 520m, CostPrice = 460m, StockQuantity = 20, LowStockThreshold = 5 },
                new Product { Name = "Cooking Oil 1L", Sku = "GR-002", CategoryId = groceries.Id, Price = 180m, CostPrice = 155m, StockQuantity = 25, LowStockThreshold = 5 },
                new Product { Name = "Sugar 1kg", Sku = "GR-003", CategoryId = groceries.Id, Price = 110m, CostPrice = 95m, StockQuantity = 30, LowStockThreshold = 5 },
                new Product { Name = "Salt 500g", Sku = "GR-004", CategoryId = groceries.Id, Price = 35m, CostPrice = 25m, StockQuantity = 40, LowStockThreshold = 5 },
                new Product { Name = "Banana (per kg)", Sku = "PR-001", CategoryId = produce.Id, Price = 80m, CostPrice = 55m, StockQuantity = 50, LowStockThreshold = 5, IsWeighted = true, Unit = UnitOfMeasure.Kilogram },
                new Product { Name = "Tomato (per kg)", Sku = "PR-002", CategoryId = produce.Id, Price = 60m, CostPrice = 40m, StockQuantity = 40, LowStockThreshold = 5, IsWeighted = true, Unit = UnitOfMeasure.Kilogram },
                new Product { Name = "Potato (per kg)", Sku = "PR-003", CategoryId = produce.Id, Price = 45m, CostPrice = 30m, StockQuantity = 60, LowStockThreshold = 5, IsWeighted = true, Unit = UnitOfMeasure.Kilogram },
                new Product { Name = "Onion (per kg)", Sku = "PR-004", CategoryId = produce.Id, Price = 70m, CostPrice = 50m, StockQuantity = 50, LowStockThreshold = 5, IsWeighted = true, Unit = UnitOfMeasure.Kilogram }
            });

            // Seed initial stock transactions
            foreach (var p in db.Products.Local)
            {
                if (p.StockQuantity is > 0)
                {
                    db.StockTransactions.Add(new StockTransaction
                    {
                        ProductId = p.Id,
                        Type = StockTransactionType.InitialStock,
                        Quantity = p.StockQuantity.Value,
                        BalanceAfter = p.StockQuantity.Value,
                        UnitCost = p.CostPrice,
                        Note = "Initial stock on first run"
                    });
                }
            }
        }

        if (!await db.Taxes.AnyAsync())
        {
            db.Taxes.AddRange(
                new Tax { Name = "VAT", Rate = 15m, IsIncluded = false, IsDefault = true },
                new Tax { Name = "Service Charge", Rate = 5m, IsIncluded = false }
            );
        }

        if (!await db.Discounts.AnyAsync())
        {
            db.Discounts.AddRange(
                new Discount { Name = "5% Off", Type = DiscountType.Percentage, Value = 5m, Code = "SAVE5", IsActive = true },
                new Discount { Name = "Senior Citizen", Type = DiscountType.Percentage, Value = 10m, IsActive = true },
                new Discount { Name = "৳ 50 Off", Type = DiscountType.FixedAmount, Value = 50m, Code = "BD50", IsActive = true }
            );
        }

        if (!await db.Settings.AnyAsync())
        {
            var defaults = new StoreSettings();
            db.Settings.Add(new Setting { Key = "store:config", Value = System.Text.Json.JsonSerializer.Serialize(defaults) });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Minimal SHA256-based PIN hasher. Salt is 16 random bytes hex-encoded.
    /// </summary>
    public static (string hash, string salt) HashPin(string pin)
    {
        var saltBytes = new byte[16];
        using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
        {
            rng.GetBytes(saltBytes);
        }
        var salt = Convert.ToHexString(saltBytes);
        var hash = ComputeHash(pin, salt);
        return (hash, salt);
    }

    public static string ComputeHash(string pin, string salt)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(pin + ":" + salt);
        return Convert.ToHexString(sha.ComputeHash(bytes));
    }

    public static bool VerifyPin(string pin, string hash, string salt)
        => string.Equals(ComputeHash(pin, salt), hash, StringComparison.OrdinalIgnoreCase);
}
