using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Data;

namespace PosApp.Services;

/// <summary>
/// Processes POS sales: validates stock, generates receipt numbers,
/// writes sale + sale items + payments + stock transactions atomically,
/// and supports suspend/recall/void/refund.
/// </summary>
public class SaleService : ISaleService
{
    private readonly AppDbContext _db;
    private readonly IInventoryService _inventory;

    public SaleService(AppDbContext db, IInventoryService inventory)
    {
        _db = db;
        _inventory = inventory;
    }

    public async Task<string> GenerateReceiptNumberAsync()
    {
        var date = DateTime.Today;
        var prefix = date.ToString("yyyyMMdd");
        var count = await _db.Sales.CountAsync(s => s.SaleDate.Date == date);
        return $"{prefix}-{(count + 1):D4}";
    }

    public async Task<Sale> SuspendAsync(SaleDraft draft)
    {
        Sale? sale = null;
        if (draft.SuspendedSaleId.HasValue)
        {
            sale = await _db.Sales
                .Include(s => s.Items)
                .FirstOrDefaultAsync(s => s.Id == draft.SuspendedSaleId.Value
                                          && s.Status == SaleStatus.Suspended);
        }

        if (sale == null)
        {
            sale = new Sale
            {
                ReceiptNumber = await GenerateReceiptNumberAsync(),
                Status = SaleStatus.Suspended
            };
            _db.Sales.Add(sale);
        }
        else
        {
            // Re-suspending a recalled sale updates the original draft instead
            // of creating duplicate suspended records.
            _db.SaleItems.RemoveRange(sale.Items.ToList());
            sale.Items.Clear();
            sale.UpdatedAt = DateTime.UtcNow;
        }

        sale.CustomerId = draft.CustomerId;
        sale.UserId = draft.UserId;
        sale.SaleDate = DateTime.UtcNow;
        sale.Subtotal = draft.Subtotal;
        sale.DiscountTotal = draft.DiscountTotal;
        sale.TaxTotal = draft.TaxTotal;
        sale.Note = draft.Note;

        foreach (var line in draft.Lines)
        {
            sale.Items.Add(BuildSaleItem(line, sale));
        }
        await _db.SaveChangesAsync();
        return sale;
    }

    public async Task<Sale> RecallSuspendedAsync(int saleId)
    {
        var sale = await _db.Sales
            .Include(s => s.Items)
            .FirstOrDefaultAsync(s => s.Id == saleId && s.Status == SaleStatus.Suspended)
            ?? throw new InvalidOperationException("Suspended sale not found");
        return sale;
    }

    public async Task<IReadOnlyList<Sale>> GetSuspendedSalesAsync()
    {
        return await _db.Sales.AsNoTracking()
            .Include(s => s.Items)
            .Include(s => s.Customer)
            .Where(s => s.Status == SaleStatus.Suspended)
            .OrderByDescending(s => s.SaleDate)
            .ToListAsync();
    }

    public async Task<Sale> CheckoutAsync(SaleDraft draft)
    {
        if (draft.Lines.Count == 0)
            throw new InvalidOperationException("Cannot checkout an empty cart");
        if (draft.Payments.Count == 0)
            throw new InvalidOperationException("A payment is required to complete the sale");

        var appliedPayment = draft.Payments.Sum(p => p.Amount);
        if (appliedPayment < draft.Total)
            throw new InvalidOperationException("The payment does not cover the sale total");

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var receiptNo = draft.SuspendedSaleId.HasValue
                ? (await _db.Sales.FindAsync(draft.SuspendedSaleId.Value))?.ReceiptNumber ?? await GenerateReceiptNumberAsync()
                : await GenerateReceiptNumberAsync();

            if (draft.SuspendedSaleId.HasValue)
            {
                // Delete the suspended shadow record; we replace it with the completed sale.
                var suspended = await _db.Sales
                    .Include(s => s.Items)
                    .FirstOrDefaultAsync(s => s.Id == draft.SuspendedSaleId.Value);
                if (suspended != null)
                {
                    _db.SaleItems.RemoveRange(suspended.Items);
                    _db.Sales.Remove(suspended);
                }
            }

            var sale = new Sale
            {
                ReceiptNumber = receiptNo,
                CustomerId = draft.CustomerId,
                UserId = draft.UserId,
                Status = SaleStatus.Completed,
                SaleDate = DateTime.UtcNow,
                Subtotal = draft.Subtotal,
                DiscountTotal = draft.DiscountTotal,
                TaxTotal = draft.TaxTotal,
                AmountPaid = draft.AmountTendered > 0m ? draft.AmountTendered : appliedPayment,
                Change = Math.Max(0m, (draft.AmountTendered > 0m ? draft.AmountTendered : appliedPayment) - draft.Total),
                Note = draft.Note
            };

            foreach (var payment in draft.Payments)
            {
                sale.Payments.Add(new SalePayment
                {
                    Sale = sale,
                    Method = payment.Method,
                    Amount = payment.Amount,
                    Reference = payment.Reference
                });
            }

            var stockLinks = new List<(StockTransaction Transaction, SaleItem Item)>();

            foreach (var line in draft.Lines)
            {
                var item = BuildSaleItem(line, sale);
                sale.Items.Add(item);

                // Decrement stock
                if (line.Quantity > 0)
                {
                    var product = await _db.Products.FindAsync(line.ProductId);
                    if (product != null && product.StockQuantity.HasValue)
                    {
                        var balance = product.StockQuantity.Value - line.Quantity;
                        if (balance < 0)
                            throw new InvalidOperationException($"Insufficient stock for {line.ProductName}");
                        product.StockQuantity = balance;
                        product.UpdatedAt = DateTime.UtcNow;
                        var stockTransaction = new StockTransaction
                        {
                            ProductId = product.Id,
                            Type = StockTransactionType.Sale,
                            Quantity = -line.Quantity,
                            BalanceAfter = balance,
                            Note = $"Sale {receiptNo}"
                        };
                        _db.StockTransactions.Add(stockTransaction);
                        stockLinks.Add((stockTransaction, item));
                    }
                }
            }

            // Loyalty points accrual
            if (draft.CustomerId.HasValue)
            {
                var customer = await _db.Customers.FindAsync(draft.CustomerId.Value);
                if (customer != null && customer.LoyaltyRate > 0)
                {
                    customer.LoyaltyPoints += draft.Subtotal * customer.LoyaltyRate / 100m;
                    customer.UpdatedAt = DateTime.UtcNow;
                }
            }

            _db.Sales.Add(sale);
            await _db.SaveChangesAsync();

            // Sale/line IDs are database-generated and these ledger references
            // are not modeled as EF navigation properties. Link them after the
            // first save, inside the same database transaction.
            foreach (var link in stockLinks)
            {
                link.Transaction.SaleId = sale.Id;
                link.Transaction.SaleItemId = link.Item.Id;
            }
            if (stockLinks.Count > 0)
                await _db.SaveChangesAsync();

            await transaction.CommitAsync();
            return sale;
        }
        catch
        {
            await transaction.RollbackAsync();
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    private static SaleItem BuildSaleItem(SaleDraftLine line, Sale sale)
    {
        return new SaleItem
        {
            Sale = sale,
            ProductId = line.ProductId,
            ProductName = line.ProductName,
            Sku = line.Sku,
            Quantity = line.Quantity,
            UnitPrice = line.UnitPrice,
            TaxRate = line.TaxRate,
            DiscountAmount = line.DiscountAmount,
            DiscountReason = line.DiscountReason
        };
    }

    public async Task<Sale?> GetSaleByIdAsync(int id)
    {
        return await _db.Sales.AsNoTracking()
            .Include(s => s.Items)
            .Include(s => s.Payments)
            .Include(s => s.Customer)
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.Id == id);
    }

    public async Task<IReadOnlyList<Sale>> GetSalesAsync(DateTime from, DateTime to, SaleStatus? status = null)
    {
        var q = _db.Sales.AsNoTracking()
            .Include(s => s.Items)
            .Include(s => s.Customer)
            .Include(s => s.User)
            .Where(s => s.SaleDate >= from && s.SaleDate <= to);
        if (status.HasValue) q = q.Where(s => s.Status == status.Value);
        return await q.OrderByDescending(s => s.SaleDate).ToListAsync();
    }

    public async Task<Sale> VoidSaleAsync(int saleId, int userId)
    {
        var sale = await _db.Sales
            .Include(s => s.Items)
            .FirstOrDefaultAsync(s => s.Id == saleId)
            ?? throw new InvalidOperationException("Sale not found");

        if (sale.Status != SaleStatus.Completed)
            throw new InvalidOperationException("Only completed sales can be voided");

        // Restore stock
        foreach (var item in sale.Items)
        {
            var product = await _db.Products.FindAsync(item.ProductId);
            if (product != null && product.StockQuantity.HasValue)
            {
                product.StockQuantity += item.Quantity;
                _db.StockTransactions.Add(new StockTransaction
                {
                    ProductId = product.Id,
                    Type = StockTransactionType.Return,
                    Quantity = item.Quantity,
                    BalanceAfter = product.StockQuantity.Value,
                    SaleId = sale.Id,
                    Note = $"Void of sale {sale.ReceiptNumber}"
                });
            }
        }

        sale.Status = SaleStatus.Voided;
        sale.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return sale;
    }

    public async Task<Sale> RefundSaleAsync(int saleId, int userId, string? reason = null)
    {
        var original = await _db.Sales
            .Include(s => s.Items)
            .Include(s => s.Payments)
            .FirstOrDefaultAsync(s => s.Id == saleId)
            ?? throw new InvalidOperationException("Sale not found");

        if (original.Status != SaleStatus.Completed)
            throw new InvalidOperationException("Only completed sales can be refunded");

        // Restore stock
        foreach (var item in original.Items)
        {
            var product = await _db.Products.FindAsync(item.ProductId);
            if (product != null && product.StockQuantity.HasValue)
            {
                product.StockQuantity += item.Quantity;
                _db.StockTransactions.Add(new StockTransaction
                {
                    ProductId = product.Id,
                    Type = StockTransactionType.Return,
                    Quantity = item.Quantity,
                    BalanceAfter = product.StockQuantity.Value,
                    Note = $"Refund of sale {original.ReceiptNumber}"
                });
            }
        }

        var refund = new Sale
        {
            ReceiptNumber = await GenerateReceiptNumberAsync(),
            UserId = userId,
            CustomerId = original.CustomerId,
            Status = SaleStatus.Refunded,
            SaleDate = DateTime.UtcNow,
            Subtotal = -original.Subtotal,
            DiscountTotal = -original.DiscountTotal,
            TaxTotal = -original.TaxTotal,
            AmountPaid = -original.AmountPaid,
            RefundedSaleId = original.Id,
            Note = reason ?? $"Refund of {original.ReceiptNumber}"
        };

        foreach (var item in original.Items)
        {
            refund.Items.Add(new SaleItem
            {
                Sale = refund,
                ProductId = item.ProductId,
                ProductName = item.ProductName,
                Sku = item.Sku,
                Quantity = -item.Quantity,
                UnitPrice = item.UnitPrice,
                TaxRate = item.TaxRate,
                DiscountAmount = -item.DiscountAmount,
                IsRefunded = true
            });
        }

        original.Status = SaleStatus.Refunded;
        original.UpdatedAt = DateTime.UtcNow;

        // Issue store credit if customer attached
        if (original.CustomerId.HasValue)
        {
            var customer = await _db.Customers.FindAsync(original.CustomerId.Value);
            if (customer != null)
            {
                customer.StoreCredit += original.Total;
            }
        }

        _db.Sales.Add(refund);
        await _db.SaveChangesAsync();
        return refund;
    }
}
