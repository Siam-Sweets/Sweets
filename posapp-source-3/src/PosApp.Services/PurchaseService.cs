using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Data;

namespace PosApp.Services;

public class PurchaseService : IPurchaseService
{
    private readonly AppDbContext _db;

    public PurchaseService(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<Supplier>> SearchSuppliersAsync(string? query = null)
    {
        var suppliers = _db.Suppliers.AsNoTracking().Where(s => s.IsActive);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            suppliers = suppliers.Where(s =>
                s.Name.Contains(term) ||
                (s.Phone != null && s.Phone.Contains(term)) ||
                (s.Email != null && s.Email.Contains(term)));
        }
        return await suppliers.OrderBy(s => s.Name).ToListAsync();
    }

    public async Task<Supplier> CreateOrUpdateSupplierAsync(Supplier supplier)
    {
        if (string.IsNullOrWhiteSpace(supplier.Name))
            throw new InvalidOperationException("Supplier name is required.");

        supplier.Name = supplier.Name.Trim();
        if (supplier.Id == 0)
        {
            supplier.CreatedAt = DateTime.UtcNow;
            supplier.IsActive = true;
            _db.Suppliers.Add(supplier);
        }
        else
        {
            supplier.UpdatedAt = DateTime.UtcNow;
            _db.Suppliers.Update(supplier);
        }
        await _db.SaveChangesAsync();
        return supplier;
    }

    public async Task DeactivateSupplierAsync(int supplierId)
    {
        var supplier = await _db.Suppliers.FindAsync(supplierId);
        if (supplier == null) return;
        supplier.IsActive = false;
        supplier.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<PurchaseDocument>> GetPurchasesAsync(DateTime from, DateTime to)
    {
        return await _db.PurchaseDocuments.AsNoTracking()
            .Include(p => p.Supplier)
            .Include(p => p.Items)
            .Where(p => p.DocumentDate >= from && p.DocumentDate <= to)
            .OrderByDescending(p => p.DocumentDate)
            .ToListAsync();
    }

    public async Task<PurchaseDocument> PostPurchaseAsync(PurchaseDraft draft)
    {
        if (draft.Lines.Count == 0)
            throw new InvalidOperationException("Add at least one product to the purchase.");
        if (draft.UserId <= 0)
            throw new InvalidOperationException("A signed-in user is required.");
        if (draft.Lines.Any(line =>
                line.Quantity <= 0m || line.UnitCost < 0m || line.TaxRate is < 0m or > 100m))
            throw new InvalidOperationException(
                "Purchase quantities must be positive, costs cannot be negative, and tax must be between 0 and 100.");

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            Supplier? supplier = null;
            if (draft.SupplierId.HasValue)
            {
                supplier = await _db.Suppliers.FindAsync(draft.SupplierId.Value)
                    ?? throw new InvalidOperationException("Supplier not found.");
            }

            var date = draft.DocumentDate.Kind == DateTimeKind.Utc
                ? draft.DocumentDate.ToLocalTime().Date
                : draft.DocumentDate.Date;
            var dayStart = date.ToUniversalTime();
            var dayEnd = date.AddDays(1).ToUniversalTime();
            var next = await _db.PurchaseDocuments.CountAsync(p =>
                p.DocumentDate >= dayStart && p.DocumentDate < dayEnd) + 1;
            var document = new PurchaseDocument
            {
                DocumentNumber = $"PUR-{date:yyyyMMdd}-{next:D4}",
                ExternalReference = string.IsNullOrWhiteSpace(draft.ExternalReference)
                    ? null
                    : draft.ExternalReference.Trim(),
                Supplier = supplier,
                UserId = draft.UserId,
                DocumentDate = draft.DocumentDate,
                StockDate = draft.StockDate,
                Status = PurchaseStatus.Posted,
                Subtotal = draft.Subtotal,
                TaxTotal = draft.TaxTotal,
                Total = draft.Total,
                Note = string.IsNullOrWhiteSpace(draft.Note) ? null : draft.Note.Trim()
            };

            foreach (var line in draft.Lines)
            {
                var product = await _db.Products.FindAsync(line.ProductId)
                    ?? throw new InvalidOperationException($"Product not found: {line.ProductName}");
                if (!product.StockQuantity.HasValue)
                    throw new InvalidOperationException($"{product.Name} is not stock-tracked.");

                var oldQuantity = product.StockQuantity.Value;
                var newQuantity = oldQuantity + line.Quantity;
                if (newQuantity <= 0m)
                    throw new InvalidOperationException($"Invalid resulting stock for {product.Name}.");

                var oldValue = Math.Max(0m, oldQuantity) * product.CostPrice;
                var incomingValue = line.Quantity * line.UnitCost;
                product.StockQuantity = newQuantity;
                product.CostPrice = (oldValue + incomingValue) / newQuantity;
                product.UpdatedAt = DateTime.UtcNow;

                document.Items.Add(new PurchaseItem
                {
                    PurchaseDocument = document,
                    Product = product,
                    ProductName = product.Name,
                    Sku = product.Sku,
                    Quantity = line.Quantity,
                    UnitCost = line.UnitCost,
                    TaxRate = line.TaxRate
                });

                _db.StockTransactions.Add(new StockTransaction
                {
                    Product = product,
                    Type = StockTransactionType.Purchase,
                    Quantity = line.Quantity,
                    BalanceAfter = newQuantity,
                    UnitCost = line.UnitCost,
                    Note = $"Purchase {document.DocumentNumber}" +
                           (supplier == null ? "" : $" - {supplier.Name}"),
                    UserId = draft.UserId,
                    CreatedAt = draft.StockDate
                });
            }

            _db.PurchaseDocuments.Add(document);
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
            return document;
        }
        catch
        {
            await transaction.RollbackAsync();
            _db.ChangeTracker.Clear();
            throw;
        }
    }
}
