using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Models;

namespace PosApp.Data;

/// <summary>
/// Seeds first-run default data: an admin user, default categories,
/// default tax and discount, and store settings. Sample products are added
/// separately only when the store owner enables them during setup.
/// </summary>
public static class DbSeeder
{
    public static async Task SeedAsync(AppDbContext db)
    {
        // Use EnsureCreatedAsync for the local single-DB scenario.
        // It creates all tables from the current model in one shot
        // without needing EF Core migration files (which we don't ship).
        // On subsequent runs it's a no-op if the DB schema is already current.
        await db.Database.EnsureCreatedAsync();

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

        // A restored or manually repaired database can contain only some of the
        // built-in categories. Ensure each category independently so setup-time
        // sample-product creation can always resolve every required category.
        var categoryDefinitions = new[]
        {
            (Name: "Beverages", Color: "#2D7FF9", SortOrder: 1),
            (Name: "Snacks", Color: "#F59E0B", SortOrder: 2),
            (Name: "Groceries", Color: "#10B981", SortOrder: 3),
            (Name: "Household", Color: "#8B5CF6", SortOrder: 4),
            (Name: "Personal Care", Color: "#EC4899", SortOrder: 5),
            (Name: "Produce", Color: "#22C55E", SortOrder: 6)
        };
        var categories = await db.Categories.ToListAsync();
        var categoriesAdded = false;
        foreach (var definition in categoryDefinitions)
        {
            if (categories.Any(c => string.Equals(c.Name, definition.Name, StringComparison.OrdinalIgnoreCase)))
                continue;

            var category = new Category
            {
                Name = definition.Name,
                Color = definition.Color,
                SortOrder = definition.SortOrder
            };
            db.Categories.Add(category);
            categories.Add(category);
            categoriesAdded = true;
        }

        if (categoriesAdded)
            await db.SaveChangesAsync();

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
                new Discount { Name = "50 Off", Type = DiscountType.FixedAmount, Value = 50m, Code = "BD50", IsActive = true }
            );
        }

        if (!await db.Settings.AnyAsync(s => s.Key == "store:config"))
        {
            var defaults = new StoreSettings();
            db.Settings.Add(new Setting { Key = "store:config", Value = System.Text.Json.JsonSerializer.Serialize(defaults) });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Adds the built-in sample catalog once. This is called only after the
    /// first-run setup toggle has been explicitly enabled by the store owner.
    /// </summary>
    public static async Task<bool> SeedSampleProductsAsync(AppDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (await db.Products.AnyAsync())
            return false;

        var categories = await db.Categories.ToListAsync();
        if (categories.Count == 0)
            throw new InvalidOperationException("Default categories must exist before sample products can be added.");

        Category CategoryNamed(string name) => categories.First(category =>
            string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase));

        var beverages = CategoryNamed("Beverages");
        var snacks = CategoryNamed("Snacks");
        var groceries = CategoryNamed("Groceries");
        var produce = CategoryNamed("Produce");

        var products = new[]
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
        };

        db.Products.AddRange(products);

        foreach (var product in products)
        {
            if (product.StockQuantity is > 0)
            {
                db.StockTransactions.Add(new StockTransaction
                {
                    // Use the navigation property so EF propagates the generated
                    // product key to the stock transaction during SaveChanges.
                    Product = product,
                    Type = StockTransactionType.InitialStock,
                    Quantity = product.StockQuantity.Value,
                    BalanceAfter = product.StockQuantity.Value,
                    UnitCost = product.CostPrice,
                    Note = "Initial stock on first run"
                });
            }
        }

        await db.SaveChangesAsync();
        return true;
    }

    private const int Pbkdf2Iterations = 120_000;

    public static (string hash, string salt) HashPin(string pin)
    {
        ValidatePin(pin);
        var saltBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var derived = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            pin,
            saltBytes,
            Pbkdf2Iterations,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            32);
        return ($"PBKDF2${Pbkdf2Iterations}${Convert.ToBase64String(derived)}", Convert.ToBase64String(saltBytes));
    }

    public static bool VerifyPin(string pin, string hash, string salt)
    {
        if (string.IsNullOrEmpty(pin) || string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(salt))
            return false;
        if (pin.Length > 12 || hash.Length > 256 || salt.Length > 256)
            return false;
        if (hash.StartsWith("PBKDF2$", StringComparison.Ordinal))
        {
            var parts = hash.Split('$');
            if (parts.Length != 3 || !int.TryParse(parts[1], out var iterations)
                                  || iterations is < 10_000 or > 1_000_000)
                return false;
            try
            {
                var saltBytes = Convert.FromBase64String(salt);
                var expected = Convert.FromBase64String(parts[2]);
                if (saltBytes.Length is < 8 or > 128 || expected.Length != 32)
                    return false;
                var actual = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
                    pin, saltBytes, iterations,
                    System.Security.Cryptography.HashAlgorithmName.SHA256,
                    expected.Length);
                return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch (FormatException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return false;
            }
        }

        // Backward-compatible verification for databases created before v1.4.0.
        var legacy = ComputeLegacyHash(pin, salt);
        try
        {
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(legacy), Convert.FromHexString(hash));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool IsLegacyHash(string hash)
        => !string.IsNullOrWhiteSpace(hash) && !hash.StartsWith("PBKDF2$", StringComparison.Ordinal);

    public static void ValidatePin(string pin)
    {
        if (string.IsNullOrEmpty(pin) || pin.Length is < 4 or > 12 || pin.Any(ch => !char.IsDigit(ch)))
            throw new InvalidOperationException("PIN must contain 4 to 12 digits.");
    }

    private static string ComputeLegacyHash(string pin, string salt)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(pin + ":" + salt);
        return Convert.ToHexString(sha.ComputeHash(bytes));
    }
}
