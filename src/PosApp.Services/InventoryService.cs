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
            var term = query.Trim();
            q = searchField switch
            {
                ProductSearchField.Name => q.Where(p => p.Name.Contains(term)),
                ProductSearchField.Code => q.Where(p =>
                    p.Sku != null && p.Sku.Contains(term)),
                ProductSearchField.Barcode => q.Where(p =>
                    p.Barcode != null && p.Barcode.Contains(term)),
                _ => q.Where(p =>
                    p.Name.Contains(term) ||
                    (p.Sku != null && p.Sku.Contains(term)) ||
                    (p.Barcode != null && p.Barcode.Contains(term)))
            };
        }
        if (categoryId.HasValue) q = q.Where(p => p.CategoryId == categoryId.Value);
        q = q.Where(p => p.IsActive);
        return await q.OrderBy(p => p.Name).ToListAsync();
    }

    public async Task<Product?> GetProductBySkuAsync(string sku)
    {
        if (string.IsNullOrWhiteSpace(sku)) return null;
        var term = sku.Trim();
        return await _db.Products.AsNoTracking()
            .Include(p => p.Category)
            .FirstOrDefaultAsync(p =>
                p.IsActive && (p.Sku == term || p.Barcode == term));
    }

    public async Task<Product> CreateOrUpdateProductAsync(Product product)
    {
        if (product.Id == 0)
        {
            product.CreatedAt = DateTime.UtcNow;
            _db.Products.Add(product);
            if (product.StockQuantity is > 0)
            {
                _db.StockTransactions.Add(new StockTransaction
                {
                    // The database generates the product key. Using the navigation
                    // property lets EF propagate that key to the stock transaction.
                    Product = product,
                    Type = StockTransactionType.InitialStock,
                    Quantity = product.StockQuantity.Value,
                    BalanceAfter = product.StockQuantity.Value,
                    UnitCost = product.CostPrice,
                    Note = "Initial stock on product create"
                });
            }
        }
        else
        {
            product.UpdatedAt = DateTime.UtcNow;
            _db.Products.Update(product);
        }
        await _db.SaveChangesAsync();
        return product;
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
