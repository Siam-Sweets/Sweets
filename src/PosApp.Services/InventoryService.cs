using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Enums;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Data;

namespace PosApp.Services;

public class InventoryService : IInventoryService
{
    private readonly AppDbContext _db;
    public InventoryService(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<Product>> SearchProductsAsync(
        string? query,
        int? categoryId = null,
        ProductSearchField searchField = ProductSearchField.All,
        bool includeInactive = false)
    {
        var q = _db.Products.AsNoTracking().Include(p => p.Category).AsQueryable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim().ToLowerInvariant();
            q = searchField switch
            {
                // Product-name lookup behaves like a normal catalog search:
                // it ignores letter casing and matches only from the beginning
                // of the name, so "ba" finds "Banana" but not "25bag".
                ProductSearchField.Name => q.Where(p =>
                    p.Name.ToLower().StartsWith(term)),
                ProductSearchField.Code => q.Where(p =>
                    p.Sku != null && p.Sku.ToLower().Contains(term)),
                ProductSearchField.Barcode => q.Where(p =>
                    p.Barcode != null && p.Barcode.ToLower().Contains(term)),
                _ => q.Where(p =>
                    p.Name.ToLower().StartsWith(term) ||
                    (p.Sku != null && p.Sku.ToLower().Contains(term)) ||
                    (p.Barcode != null && p.Barcode.ToLower().Contains(term)))
            };
        }
        if (categoryId.HasValue) q = q.Where(p => p.CategoryId == categoryId.Value);
        if (!includeInactive) q = q.Where(p => p.IsActive);
        return await q.OrderBy(p => p.Name).ToListAsync();
    }

    public async Task<Product?> GetProductBySkuAsync(string sku)
    {
        if (string.IsNullOrWhiteSpace(sku)) return null;
        var term = sku.Trim().ToLowerInvariant();
        return await _db.Products.AsNoTracking()
            .Include(p => p.Category)
            .FirstOrDefaultAsync(p =>
                p.IsActive &&
                ((p.Sku != null && p.Sku.ToLower() == term) ||
                 (p.Barcode != null && p.Barcode.ToLower() == term)));
    }

    public async Task<Product> CreateOrUpdateProductAsync(Product product, int? userId = null)
    {
        ArgumentNullException.ThrowIfNull(product);
        NormalizeAndValidateProduct(product);
        await EnsureIdentifiersUniqueAsync(product);

        if (product.Id == 0)
        {
            // Never attach the view model's navigation graph. Product rows loaded for
            // the catalog contain no-tracking Category instances, and attaching those
            // instances can conflict with categories already tracked by this long-lived
            // desktop DbContext.
            var created = new Product();
            CopyEditableProductValues(product, created);
            created.CreatedAt = DateTime.UtcNow;
            created.UpdatedAt = null;
            _db.Products.Add(created);

            if (created.StockQuantity is > 0)
            {
                _db.StockTransactions.Add(new StockTransaction
                {
                    // The database generates the product key. Using the clean tracked
                    // product lets EF propagate that key to the stock transaction.
                    Product = created,
                    Type = StockTransactionType.InitialStock,
                    Quantity = created.StockQuantity.Value,
                    BalanceAfter = created.StockQuantity.Value,
                    UnitCost = created.CostPrice,
                    Note = "Initial stock on product create",
                    UserId = userId
                });
            }

            await _db.SaveChangesAsync();
            product.Id = created.Id;
            product.CreatedAt = created.CreatedAt;
            product.UpdatedAt = created.UpdatedAt;
            return product;
        }

        // Reuse EF's one tracked instance for this key instead of calling Update on
        // the detached UI object. This prevents duplicate Product and Category keys
        // from being attached when weighted status or other fields are changed.
        var tracked = _db.Products.Local.FirstOrDefault(existing => existing.Id == product.Id);
        tracked ??= await _db.Products.FirstOrDefaultAsync(existing => existing.Id == product.Id);
        if (tracked is null)
            throw new InvalidOperationException("Product not found");

        if (tracked.Category?.Id != product.CategoryId)
            tracked.Category = null;

        var previousStock = tracked.StockQuantity;
        CopyEditableProductValues(product, tracked);
        tracked.UpdatedAt = DateTime.UtcNow;

        if (previousStock != tracked.StockQuantity)
        {
            var previousBalance = previousStock ?? 0m;
            var newBalance = tracked.StockQuantity ?? 0m;
            _db.StockTransactions.Add(new StockTransaction
            {
                ProductId = tracked.Id,
                Type = StockTransactionType.Adjustment,
                Quantity = newBalance - previousBalance,
                BalanceAfter = newBalance,
                UnitCost = tracked.CostPrice,
                UserId = userId,
                Note = tracked.StockQuantity.HasValue
                    ? "Stock changed in product editor"
                    : "Stock tracking disabled in product editor"
            });
        }

        await _db.SaveChangesAsync();
        product.UpdatedAt = tracked.UpdatedAt;
        return product;
    }

    public async Task SetProductWeightedAsync(int productId, bool isWeighted)
    {
        var tracked = _db.Products.Local.FirstOrDefault(product => product.Id == productId);
        tracked ??= await _db.Products.FirstOrDefaultAsync(product => product.Id == productId);
        if (tracked is null)
            throw new InvalidOperationException("Product not found");

        tracked.IsWeighted = isWeighted;
        tracked.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    private static void CopyEditableProductValues(Product source, Product target)
    {
        target.Name = source.Name;
        target.Description = source.Description;
        target.Sku = source.Sku;
        target.Barcode = source.Barcode;
        target.CategoryId = source.CategoryId;
        target.Price = source.Price;
        target.CostPrice = source.CostPrice;
        target.TaxRate = source.TaxRate;
        target.Unit = source.Unit;
        target.StockQuantity = source.StockQuantity;
        target.LowStockThreshold = source.LowStockThreshold;
        target.ImagePath = source.ImagePath;
        target.IsWeighted = source.IsWeighted;
        target.IsActive = source.IsActive;
        target.AllowDiscount = source.AllowDiscount;
    }

    private async Task EnsureIdentifiersUniqueAsync(Product product)
    {
        if (!string.IsNullOrWhiteSpace(product.Sku) && await _db.Products.AnyAsync(p =>
                p.Id != product.Id && p.Sku != null && p.Sku.ToLower() == product.Sku.ToLower()))
            throw new InvalidOperationException("SKU is already used by another product.");
        if (!string.IsNullOrWhiteSpace(product.Barcode) && await _db.Products.AnyAsync(p =>
                p.Id != product.Id && p.Barcode != null && p.Barcode.ToLower() == product.Barcode.ToLower()))
            throw new InvalidOperationException("Barcode is already used by another product.");
    }

    private static void NormalizeAndValidateProduct(Product product)
    {
        product.Name = product.Name?.Trim() ?? string.Empty;
        product.Sku = string.IsNullOrWhiteSpace(product.Sku) ? null : product.Sku.Trim();
        product.Barcode = string.IsNullOrWhiteSpace(product.Barcode) ? null : product.Barcode.Trim();
        product.Description = string.IsNullOrWhiteSpace(product.Description) ? null : product.Description.Trim();
        if (product.Name.Length is < 1 or > 100)
            throw new InvalidOperationException("Product name is required and cannot exceed 100 characters.");
        if (product.CategoryId <= 0) throw new InvalidOperationException("Select a category.");
        if (product.Price < 0m || product.CostPrice < 0m)
            throw new InvalidOperationException("Price and cost cannot be negative.");
        if (product.TaxRate is < 0m or > 100m)
            throw new InvalidOperationException("Tax must be between 0 and 100.");
        if (product.StockQuantity < 0m || product.LowStockThreshold < 0m)
            throw new InvalidOperationException("Stock and low-stock threshold cannot be negative.");
        if (product.Sku?.Length > 64 || product.Barcode?.Length > 64)
            throw new InvalidOperationException("SKU and barcode cannot exceed 64 characters.");
    }

    public async Task AdjustStockAsync(int productId, decimal delta, StockTransactionType type,
        string? note = null, int? userId = null, decimal? unitCost = null)
    {
        var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == productId);
        if (product == null) throw new InvalidOperationException("Product not found");
        if (!product.StockQuantity.HasValue)
            throw new InvalidOperationException("This product is not stock-tracked");

        var balance = product.StockQuantity.Value + delta;
        if (balance < 0) throw new InvalidOperationException("Insufficient stock");

        product.StockQuantity = balance;
        product.UpdatedAt = DateTime.UtcNow;

        _db.StockTransactions.Add(new StockTransaction
        {
            ProductId = productId,
            Type = type,
            Quantity = delta,
            BalanceAfter = balance,
            UnitCost = unitCost,
            Note = note,
            UserId = userId
        });
        await _db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<StockTransaction>> GetStockHistoryAsync(int productId)
    {
        return await _db.StockTransactions.AsNoTracking()
            .Where(t => t.ProductId == productId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<Product>> GetLowStockProductsAsync()
    {
        // EF Core's SQLite provider cannot ORDER BY decimal values. Load the
        // active inventory first, then compare and order decimals in memory.
        var products = await _db.Products.AsNoTracking()
            .Where(p => p.IsActive)
            .ToListAsync();

        return products
            .Where(p => p.StockQuantity.HasValue && p.LowStockThreshold.HasValue
                        && p.StockQuantity.Value <= p.LowStockThreshold.Value)
            .OrderBy(p => p.StockQuantity)
            .ThenBy(p => p.Name)
            .ToList();
    }

    public async Task<IReadOnlyList<Category>> ListCategoriesAsync()
    {
        return await _db.Categories.AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToListAsync();
    }

    public async Task<Category> CreateOrUpdateCategoryAsync(Category category)
    {
        ArgumentNullException.ThrowIfNull(category);
        var name = category.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 100)
            throw new InvalidOperationException("Category name is required and cannot exceed 100 characters.");
        if (await _db.Categories.AsNoTracking().AnyAsync(c =>
                c.Id != category.Id && c.Name.ToLower() == name.ToLower()))
            throw new InvalidOperationException("Another category already uses this name.");

        var description = string.IsNullOrWhiteSpace(category.Description) ? null : category.Description.Trim();
        var color = string.IsNullOrWhiteSpace(category.Color) ? "#64748B" : category.Color.Trim();
        if (color.Length != 7 || color[0] != '#' || !color[1..].All(Uri.IsHexDigit))
            throw new InvalidOperationException("Category color must use #RRGGBB format.");

        if (category.Id == 0)
        {
            var created = new Category
            {
                Name = name,
                Description = description,
                Color = color.ToUpperInvariant(),
                SortOrder = category.SortOrder,
                IsActive = category.IsActive,
                CreatedAt = DateTime.UtcNow
            };
            _db.Categories.Add(created);
            await _db.SaveChangesAsync();
            category.Id = created.Id;
            category.Name = created.Name;
            category.Description = created.Description;
            category.Color = created.Color;
            category.CreatedAt = created.CreatedAt;
            return category;
        }

        var tracked = await _db.Categories.FindAsync(category.Id)
            ?? throw new InvalidOperationException("Category not found");
        tracked.Name = name;
        tracked.Description = description;
        tracked.Color = color.ToUpperInvariant();
        tracked.SortOrder = category.SortOrder;
        tracked.IsActive = category.IsActive;
        tracked.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        category.Name = tracked.Name;
        category.Description = tracked.Description;
        category.Color = tracked.Color;
        category.UpdatedAt = tracked.UpdatedAt;
        return category;
    }

    public async Task DeleteCategoryAsync(int id)
    {
        var inUse = await _db.Products.AnyAsync(p => p.CategoryId == id);
        if (inUse) throw new InvalidOperationException("Category in use by products");
        var cat = await _db.Categories.FindAsync(id);
        if (cat != null)
        {
            _db.Categories.Remove(cat);
            await _db.SaveChangesAsync();
        }
    }

    public async Task ApplyInventoryCountAsync(
        IReadOnlyList<InventoryCountEntry> entries,
        string? note = null,
        int? userId = null)
    {
        if (entries.Count == 0)
            throw new InvalidOperationException("Enter at least one counted quantity.");
        if (entries.Any(entry => entry.CountedQuantity < 0m))
            throw new InvalidOperationException("Counted quantities cannot be negative.");

        var counts = entries
            .GroupBy(entry => entry.ProductId)
            .ToDictionary(group => group.Key, group => group.Last().CountedQuantity);
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            foreach (var count in counts)
            {
                var product = await _db.Products.FindAsync(count.Key)
                    ?? throw new InvalidOperationException($"Product {count.Key} was not found.");
                if (!product.StockQuantity.HasValue)
                    throw new InvalidOperationException($"{product.Name} is not stock-tracked.");

                var delta = count.Value - product.StockQuantity.Value;
                if (delta == 0m) continue;
                product.StockQuantity = count.Value;
                product.UpdatedAt = DateTime.UtcNow;
                _db.StockTransactions.Add(new StockTransaction
                {
                    Product = product,
                    Type = StockTransactionType.Adjustment,
                    Quantity = delta,
                    BalanceAfter = count.Value,
                    UnitCost = product.CostPrice,
                    Note = string.IsNullOrWhiteSpace(note) ? "Physical inventory count" : note.Trim(),
                    UserId = userId
                });
            }
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            _db.ChangeTracker.Clear();
            throw;
        }
    }
}
