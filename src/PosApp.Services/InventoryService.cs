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
        ProductSearchField searchField = ProductSearchField.All)
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
        q = q.Where(p => p.IsActive);
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

    public async Task<Product> CreateOrUpdateProductAsync(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);

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
                    Note = "Initial stock on product create"
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

        CopyEditableProductValues(product, tracked);
        tracked.UpdatedAt = DateTime.UtcNow;

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
        if (category.Id == 0)
        {
            category.CreatedAt = DateTime.UtcNow;
            _db.Categories.Add(category);
        }
        else
        {
            category.UpdatedAt = DateTime.UtcNow;
            _db.Categories.Update(category);
        }
        await _db.SaveChangesAsync();
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
