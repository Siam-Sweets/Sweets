using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Core.Utilities;
using PosApp.Data;

namespace PosApp.Services;

/// <summary>Atomic sale, suspension, void, refund, payment, promotion, and stock processing.</summary>
public class SaleService : ISaleService
{
    private readonly AppDbContext _db;

    public SaleService(AppDbContext db, IInventoryService inventory)
    {
        _db = db;
        _ = inventory; // retained for constructor compatibility with existing DI registrations
    }

    public Task<string> GenerateReceiptNumberAsync()
    {
        var now = DateTime.Now;
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        return Task.FromResult($"{now:yyyyMMdd-HHmmssfff}-{suffix}");
    }

    public async Task<Sale> SuspendAsync(SaleDraft draft)
    {
        ValidateDraftLines(draft);
        Sale? sale = null;
        if (draft.SuspendedSaleId.HasValue)
        {
            sale = await _db.Sales.Include(s => s.Items)
                .FirstOrDefaultAsync(s => s.Id == draft.SuspendedSaleId.Value && s.Status == SaleStatus.Suspended);
        }

        if (sale == null)
        {
            sale = new Sale { ReceiptNumber = await GenerateReceiptNumberAsync(), Status = SaleStatus.Suspended };
            _db.Sales.Add(sale);
        }
        else
        {
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
        sale.Note = Clean(draft.Note);
        sale.ServiceType = NormalizeServiceType(draft.ServiceType);
        foreach (var line in draft.Lines) sale.Items.Add(BuildSaleItem(line, sale));

        await _db.SaveChangesAsync();
        return sale;
    }

    public async Task<Sale> RecallSuspendedAsync(int saleId)
        => await _db.Sales.Include(s => s.Items).Include(s => s.Customer)
               .FirstOrDefaultAsync(s => s.Id == saleId && s.Status == SaleStatus.Suspended)
           ?? throw new InvalidOperationException("Suspended sale not found.");

    public async Task<IReadOnlyList<Sale>> GetSuspendedSalesAsync()
        => await _db.Sales.AsNoTracking().Include(s => s.Items).Include(s => s.Customer)
            .Where(s => s.Status == SaleStatus.Suspended)
            .OrderByDescending(s => s.SaleDate).ToListAsync();

    public async Task<Sale> CheckoutAsync(SaleDraft draft)
    {
        ValidateDraftLines(draft);
        if (draft.UserId <= 0) throw new InvalidOperationException("A signed-in user is required.");

        var total = draft.Total;
        if (total < -0.0001m) throw new InvalidOperationException("The sale total cannot be negative.");
        if (total > 0.0001m && draft.Payments.Count == 0)
            throw new InvalidOperationException("A payment is required to complete the sale.");
        if (draft.Payments.Any(p => p.Amount <= 0m))
            throw new InvalidOperationException("Payment amounts must be greater than zero.");

        var appliedPayment = draft.Payments.Sum(p => p.Amount);
        if (Math.Abs(appliedPayment - total) > 0.0001m)
            throw new InvalidOperationException("Applied payments must equal the sale total.");

        var amountTendered = draft.AmountTendered > 0m ? draft.AmountTendered : appliedPayment;
        if (amountTendered + 0.0001m < appliedPayment)
            throw new InvalidOperationException("The received amount is less than the applied payments.");
        if (amountTendered - appliedPayment > 0.0001m &&
            !draft.Payments.Any(p => p.Method == PaymentMethod.Cash))
            throw new InvalidOperationException("Only cash payments can produce change.");

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var receiptNo = draft.SuspendedSaleId.HasValue
                ? (await _db.Sales.FindAsync(draft.SuspendedSaleId.Value))?.ReceiptNumber
                  ?? await GenerateReceiptNumberAsync()
                : await GenerateReceiptNumberAsync();

            if (draft.SuspendedSaleId.HasValue)
            {
                var suspended = await _db.Sales.Include(s => s.Items)
                    .FirstOrDefaultAsync(s => s.Id == draft.SuspendedSaleId.Value && s.Status == SaleStatus.Suspended);
                if (suspended != null)
                {
                    _db.SaleItems.RemoveRange(suspended.Items);
                    _db.Sales.Remove(suspended);
                }
            }

            await ValidateAndConsumePromotionsAsync(draft);

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
                AmountPaid = amountTendered,
                Change = Math.Max(0m, amountTendered - total),
                Note = Clean(draft.Note),
                ServiceType = NormalizeServiceType(draft.ServiceType)
            };

            foreach (var payment in draft.Payments)
            {
                sale.Payments.Add(new SalePayment
                {
                    Sale = sale,
                    Method = payment.Method,
                    Amount = payment.Amount,
                    Reference = Clean(payment.Reference)
                });
            }

            var stockLinks = new List<(StockTransaction Transaction, SaleItem Item)>();
            foreach (var line in draft.Lines)
            {
                var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == line.ProductId && p.IsActive)
                    ?? throw new InvalidOperationException($"Product not found or inactive: {line.ProductName}");

                var item = BuildSaleItem(line, sale);
                // Always use the current catalog cost and discount permission at checkout.
                item.CostPrice = product.CostPrice;
                sale.Items.Add(item);

                if (line.DiscountAmount > 0m && !product.AllowDiscount)
                    throw new InvalidOperationException($"Discounts are disabled for {product.Name}.");

                if (product.StockQuantity.HasValue)
                {
                    var balance = product.StockQuantity.Value - line.Quantity;
                    if (balance < 0m) throw new InvalidOperationException($"Insufficient stock for {line.ProductName}.");
                    product.StockQuantity = balance;
                    product.UpdatedAt = DateTime.UtcNow;
                    var ledger = new StockTransaction
                    {
                        ProductId = product.Id,
                        Type = StockTransactionType.Sale,
                        Quantity = -line.Quantity,
                        BalanceAfter = balance,
                        UnitCost = product.CostPrice,
                        UserId = draft.UserId,
                        Note = $"Sale {receiptNo}"
                    };
                    _db.StockTransactions.Add(ledger);
                    stockLinks.Add((ledger, item));
                }
            }

            _db.Sales.Add(sale);
            await _db.SaveChangesAsync();
            foreach (var link in stockLinks)
            {
                link.Transaction.SaleId = sale.Id;
                link.Transaction.SaleItemId = link.Item.Id;
            }
            if (stockLinks.Count > 0) await _db.SaveChangesAsync();

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

    public async Task<Sale?> GetSaleByIdAsync(int id)
        => await _db.Sales.AsNoTracking().Include(s => s.Items).Include(s => s.Payments)
            .Include(s => s.Customer).Include(s => s.User).FirstOrDefaultAsync(s => s.Id == id);

    public async Task<IReadOnlyList<Sale>> GetSalesAsync(DateTime from, DateTime to, SaleStatus? status = null)
    {
        var range = DateTimeUtilities.InclusiveLocalDateRange(from, to);
        var query = _db.Sales.AsNoTracking().Include(s => s.Items).Include(s => s.Customer).Include(s => s.User)
            .Where(s => s.SaleDate >= range.FromUtc && s.SaleDate < range.ToUtcExclusive);
        if (status.HasValue) query = query.Where(s => s.Status == status.Value);

        var sales = await query.OrderByDescending(s => s.SaleDate).ToListAsync();
        var completedIds = sales.Where(s => s.Status == SaleStatus.Completed).Select(s => s.Id).ToList();
        if (completedIds.Count > 0)
        {
            var refundedIds = await _db.Sales.AsNoTracking()
                .Where(s => s.RefundedSaleId.HasValue && completedIds.Contains(s.RefundedSaleId.Value))
                .Select(s => s.RefundedSaleId!.Value)
                .ToListAsync();
            var refundedSet = refundedIds.ToHashSet();
            foreach (var sale in sales) sale.HasRefund = refundedSet.Contains(sale.Id);
        }
        return sales;
    }

    public async Task<Sale> VoidSaleAsync(int saleId, int userId)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var sale = await _db.Sales.Include(s => s.Items).FirstOrDefaultAsync(s => s.Id == saleId)
                ?? throw new InvalidOperationException("Sale not found.");
            if (sale.Status != SaleStatus.Completed)
                throw new InvalidOperationException("Only completed sales can be voided.");
            if (await _db.Sales.AnyAsync(s => s.RefundedSaleId == saleId))
                throw new InvalidOperationException("A refunded sale cannot be voided.");

            foreach (var item in sale.Items)
            {
                var product = await _db.Products.FindAsync(item.ProductId);
                if (product?.StockQuantity is not decimal current) continue;
                product.StockQuantity = current + item.Quantity;
                product.UpdatedAt = DateTime.UtcNow;
                _db.StockTransactions.Add(new StockTransaction
                {
                    ProductId = product.Id,
                    Type = StockTransactionType.Return,
                    Quantity = item.Quantity,
                    BalanceAfter = product.StockQuantity.Value,
                    UnitCost = item.CostPrice,
                    SaleId = sale.Id,
                    SaleItemId = item.Id,
                    UserId = userId,
                    Note = $"Void of sale {sale.ReceiptNumber}"
                });
            }

            await ReleasePromotionsAsync(sale.Items);
            sale.Status = SaleStatus.Voided;
            sale.UpdatedAt = DateTime.UtcNow;
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

    public async Task<Sale> RefundSaleAsync(int saleId, int userId, string? reason = null)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var original = await _db.Sales.Include(s => s.Items).Include(s => s.Payments)
                .FirstOrDefaultAsync(s => s.Id == saleId)
                ?? throw new InvalidOperationException("Sale not found.");
            if (original.Status != SaleStatus.Completed)
                throw new InvalidOperationException("Only completed sales can be refunded.");
            if (await _db.Sales.AnyAsync(s => s.RefundedSaleId == saleId))
                throw new InvalidOperationException("This sale has already been refunded.");

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
                Rounding = -original.Rounding,
                AmountPaid = -original.Total,
                Change = 0m,
                RefundedSaleId = original.Id,
                ServiceType = original.ServiceType,
                Note = Clean(reason) ?? $"Refund of {original.ReceiptNumber}"
            };

            foreach (var payment in original.Payments)
            {
                refund.Payments.Add(new SalePayment
                {
                    Sale = refund,
                    Method = payment.Method,
                    Amount = -payment.Amount,
                    Reference = string.IsNullOrWhiteSpace(payment.Reference)
                        ? $"Refund {original.ReceiptNumber}"
                        : $"Refund {original.ReceiptNumber}: {payment.Reference}"
                });
            }

            var stockLinks = new List<(StockTransaction Transaction, SaleItem Item)>();
            foreach (var item in original.Items)
            {
                var refundItem = new SaleItem
                {
                    Sale = refund,
                    ProductId = item.ProductId,
                    ProductName = item.ProductName,
                    Sku = item.Sku,
                    Quantity = -item.Quantity,
                    UnitPrice = item.UnitPrice,
                    CostPrice = item.CostPrice,
                    TaxRate = item.TaxRate,
                    DiscountAmount = -item.DiscountAmount,
                    DiscountReason = item.DiscountReason,
                    PromotionId = item.PromotionId,
                    IsRefunded = true
                };
                refund.Items.Add(refundItem);

                var product = await _db.Products.FindAsync(item.ProductId);
                if (product?.StockQuantity is not decimal current) continue;
                product.StockQuantity = current + item.Quantity;
                product.UpdatedAt = DateTime.UtcNow;
                var ledger = new StockTransaction
                {
                    ProductId = product.Id,
                    Type = StockTransactionType.Return,
                    Quantity = item.Quantity,
                    BalanceAfter = product.StockQuantity.Value,
                    UnitCost = item.CostPrice,
                    UserId = userId,
                    Note = $"Refund of sale {original.ReceiptNumber}"
                };
                _db.StockTransactions.Add(ledger);
                stockLinks.Add((ledger, refundItem));
            }

            await ReleasePromotionsAsync(original.Items);
            original.UpdatedAt = DateTime.UtcNow;
            _db.Sales.Add(refund);
            await _db.SaveChangesAsync();
            foreach (var link in stockLinks)
            {
                link.Transaction.SaleId = refund.Id;
                link.Transaction.SaleItemId = link.Item.Id;
            }
            if (stockLinks.Count > 0) await _db.SaveChangesAsync();

            await transaction.CommitAsync();
            return refund;
        }
        catch
        {
            await transaction.RollbackAsync();
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task ValidateAndConsumePromotionsAsync(SaleDraft draft)
    {
        var ids = draft.Lines.Where(l => l.PromotionId.HasValue).Select(l => l.PromotionId!.Value).Distinct().ToList();
        if (ids.Count == 0) return;
        var now = DateTime.Now;
        var promotions = await _db.Discounts.Where(d => ids.Contains(d.Id)).ToListAsync();
        if (promotions.Count != ids.Count) throw new InvalidOperationException("A selected promotion no longer exists.");
        foreach (var promotion in promotions)
        {
            if (!promotion.IsActive || promotion.ValidFrom > now || promotion.ValidTo < now ||
                promotion.MaxUses.HasValue && promotion.UsedCount >= promotion.MaxUses.Value)
                throw new InvalidOperationException($"Promotion '{promotion.Name}' is no longer available.");

            foreach (var line in draft.Lines.Where(line => line.PromotionId == promotion.Id))
            {
                var gross = line.UnitPrice * line.Quantity;
                var expected = promotion.Type == DiscountType.Percentage
                    ? gross * promotion.Value / 100m
                    : Math.Min(promotion.Value, gross);
                if (Math.Abs(line.DiscountAmount - expected) > 0.0001m)
                    throw new InvalidOperationException(
                        $"The discount for promotion '{promotion.Name}' was changed. Reapply the promotion before checkout.");
            }

            promotion.UsedCount++;
        }
    }

    private async Task ReleasePromotionsAsync(IEnumerable<SaleItem> items)
    {
        var ids = items.Where(i => i.PromotionId.HasValue).Select(i => i.PromotionId!.Value).Distinct().ToList();
        if (ids.Count == 0) return;
        var promotions = await _db.Discounts.Where(d => ids.Contains(d.Id)).ToListAsync();
        foreach (var promotion in promotions) promotion.UsedCount = Math.Max(0, promotion.UsedCount - 1);
    }

    private static SaleItem BuildSaleItem(SaleDraftLine line, Sale sale) => new()
    {
        Sale = sale,
        ProductId = line.ProductId,
        ProductName = line.ProductName,
        Sku = line.Sku,
        Quantity = line.Quantity,
        UnitPrice = line.UnitPrice,
        CostPrice = line.CostPrice,
        TaxRate = line.TaxRate,
        DiscountAmount = line.DiscountAmount,
        DiscountReason = Clean(line.DiscountReason),
        PromotionId = line.PromotionId
    };

    private static void ValidateDraftLines(SaleDraft draft)
    {
        if (draft.Lines.Count == 0) throw new InvalidOperationException("Cannot save an empty cart.");
        if (draft.Lines.Any(l => l.ProductId <= 0 || l.Quantity <= 0m || l.UnitPrice < 0m ||
                                 l.CostPrice < 0m || l.TaxRate is < 0m or > 100m ||
                                 l.DiscountAmount < 0m || l.DiscountAmount > l.UnitPrice * l.Quantity))
            throw new InvalidOperationException("The cart contains an invalid product, quantity, price, tax, or discount.");
    }

    private static string NormalizeServiceType(string? value)
        => string.IsNullOrWhiteSpace(value) ? "Retail" : value.Trim()[..Math.Min(value.Trim().Length, 32)];

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
