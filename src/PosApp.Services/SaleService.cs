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
        _db.ChangeTracker.Clear();
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
        _db.ChangeTracker.Clear();
        return sale;
    }

    public async Task<Sale> RecallSuspendedAsync(int saleId)
        => await _db.Sales.AsNoTracking().Include(s => s.Items).Include(s => s.Customer)
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

        // SaleService lives for the whole signed-in window. Discard entities from
        // earlier operations before reading stock so an inventory count performed
        // by another view cannot be overwritten by a stale tracked Product.
        _db.ChangeTracker.Clear();
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var openSessionId = await _db.CashSessions.AsNoTracking()
                .Where(session => session.ClosedAt == null)
                .Select(session => (int?)session.Id)
                .SingleOrDefaultAsync();
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
                CashSessionId = openSessionId,
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
            _db.ChangeTracker.Clear();
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
            var refunds = await _db.Sales.AsNoTracking()
                .Where(s => s.Status == SaleStatus.Refunded &&
                            s.RefundedSaleId.HasValue && completedIds.Contains(s.RefundedSaleId.Value))
                .Select(s => new
                {
                    OriginalSaleId = s.RefundedSaleId!.Value,
                    s.Subtotal,
                    s.DiscountTotal,
                    s.TaxTotal,
                    s.Rounding
                })
                .ToListAsync();
            var refundedBySale = refunds
                .GroupBy(refund => refund.OriginalSaleId)
                .ToDictionary(
                    group => group.Key,
                    group => new
                    {
                        Count = group.Count(),
                        Total = group.Sum(refund => Math.Abs(
                            refund.Subtotal - refund.DiscountTotal + refund.TaxTotal + refund.Rounding))
                    });
            foreach (var sale in sales.Where(sale => sale.Status == SaleStatus.Completed))
            {
                if (!refundedBySale.TryGetValue(sale.Id, out var refundState)) continue;
                sale.HasRefund = refundState.Count > 0;
                sale.IsFullyRefunded = refundState.Total + 0.0001m >= Math.Abs(sale.Total);
            }
        }
        return sales;
    }

    public async Task<Sale> VoidSaleAsync(int saleId, int userId)
    {
        _db.ChangeTracker.Clear();
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var sale = await _db.Sales.Include(s => s.Items).FirstOrDefaultAsync(s => s.Id == saleId)
                ?? throw new InvalidOperationException("Sale not found.");
            if (sale.Status != SaleStatus.Completed)
                throw new InvalidOperationException("Only completed sales can be voided.");
            if (await _db.Sales.AnyAsync(s => s.RefundedSaleId == saleId))
                throw new InvalidOperationException("A refunded sale cannot be voided.");

            if (sale.CashSessionId.HasValue)
            {
                var ownerSession = await _db.CashSessions.AsNoTracking()
                    .FirstOrDefaultAsync(session => session.Id == sale.CashSessionId.Value)
                    ?? throw new InvalidOperationException("The sale's register session could not be found.");
                if (ownerSession.ClosedAt.HasValue)
                    throw new InvalidOperationException(
                        "This sale belongs to a closed register. Process a refund in an open register instead of voiding it.");
            }

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
            _db.ChangeTracker.Clear();
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
        _db.ChangeTracker.Clear();
        var original = await _db.Sales.AsNoTracking()
            .Include(sale => sale.Items)
            .Include(sale => sale.Payments)
            .FirstOrDefaultAsync(sale => sale.Id == saleId)
            ?? throw new InvalidOperationException("Sale not found.");
        var priorItems = await _db.Sales.AsNoTracking()
            .Where(sale => sale.RefundedSaleId == saleId && sale.Status == SaleStatus.Refunded)
            .SelectMany(sale => sale.Items)
            .ToListAsync();
        var returnedByLine = BuildReturnedQuantityMap(original.Items, priorItems);
        var lines = original.Items
            .Select(item => new RefundDraftLine
            {
                SaleItemId = item.Id,
                Quantity = Math.Max(0m, item.Quantity - returnedByLine.GetValueOrDefault(item.Id))
            })
            .Where(line => line.Quantity > 0.0001m)
            .ToList();
        if (lines.Count == 0)
            throw new InvalidOperationException("This sale has already been fully refunded.");

        return await RefundSaleAsync(new RefundDraft
        {
            SaleId = saleId,
            UserId = userId,
            PaymentMethod = original.Payments.FirstOrDefault()?.Method ?? PaymentMethod.Cash,
            Reason = reason,
            Lines = lines
        });
    }

    public async Task<Sale> RefundSaleAsync(RefundDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.SaleId <= 0) throw new InvalidOperationException("Select a completed sale to refund.");
        if (draft.UserId <= 0) throw new InvalidOperationException("A signed-in user is required.");
        if (draft.Lines.Count == 0) throw new InvalidOperationException("Select at least one item to refund.");
        if (!Enum.IsDefined(draft.PaymentMethod))
            throw new InvalidOperationException("Select a valid refund payment method.");
        if (draft.Lines.Any(line => line.SaleItemId <= 0 || line.Quantity <= 0m))
            throw new InvalidOperationException("Refund quantities must be greater than zero.");

        _db.ChangeTracker.Clear();
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var original = await _db.Sales.Include(s => s.Items).Include(s => s.Payments)
                .FirstOrDefaultAsync(s => s.Id == draft.SaleId)
                ?? throw new InvalidOperationException("Sale not found.");
            if (original.Status != SaleStatus.Completed)
                throw new InvalidOperationException("Only completed sales can be refunded.");

            var priorRefunds = await _db.Sales
                .Include(sale => sale.Items)
                .Where(sale => sale.RefundedSaleId == original.Id && sale.Status == SaleStatus.Refunded)
                .ToListAsync();
            var returnedByLine = BuildReturnedQuantityMap(
                original.Items,
                priorRefunds.SelectMany(refund => refund.Items));

            var requestedByLine = draft.Lines
                .GroupBy(line => line.SaleItemId)
                .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity));
            var originalById = original.Items.ToDictionary(item => item.Id);
            var selections = new List<(SaleItem Item, decimal Quantity, decimal Discount, decimal Tax)>();
            foreach (var request in requestedByLine)
            {
                if (!originalById.TryGetValue(request.Key, out var item))
                    throw new InvalidOperationException("A selected refund line does not belong to this sale.");
                var remaining = item.Quantity - returnedByLine.GetValueOrDefault(item.Id);
                if (request.Value > remaining + 0.0001m)
                    throw new InvalidOperationException(
                        $"Only {remaining:0.###} of {item.ProductName} remains refundable.");

                var ratio = item.Quantity == 0m ? 0m : request.Value / item.Quantity;
                var discount = Math.Round(item.DiscountAmount * ratio, 4, MidpointRounding.AwayFromZero);
                var gross = item.UnitPrice * request.Value;
                var tax = Math.Round((gross - discount) * item.TaxRate / 100m, 4,
                    MidpointRounding.AwayFromZero);
                selections.Add((item, request.Value, discount, tax));
            }

            var allRemainingSelected = original.Items.All(item =>
                item.Quantity - returnedByLine.GetValueOrDefault(item.Id) -
                requestedByLine.GetValueOrDefault(item.Id) <= 0.0001m);
            var subtotal = -selections.Sum(selection => selection.Item.UnitPrice * selection.Quantity);
            var discountTotal = -selections.Sum(selection => selection.Discount);
            var taxTotal = -selections.Sum(selection => selection.Tax);
            var priorRefundTotal = priorRefunds.Sum(refund => Math.Abs(refund.Total));
            var remainingFinancialTotal = Math.Max(0m, Math.Abs(original.Total) - priorRefundTotal);
            var beforeRounding = subtotal - discountTotal + taxTotal;
            var rounding = allRemainingSelected ? -remainingFinancialTotal - beforeRounding : 0m;
            var refundTotal = subtotal - discountTotal + taxTotal + rounding;
            if (refundTotal >= -0.0001m)
                throw new InvalidOperationException("The selected items do not have a refundable value.");

            var openSessionId = await _db.CashSessions.AsNoTracking()
                .Where(session => session.ClosedAt == null)
                .Select(session => (int?)session.Id)
                .SingleOrDefaultAsync();
            if (draft.PaymentMethod == PaymentMethod.Cash && !openSessionId.HasValue)
            {
                throw new InvalidOperationException(
                    "Open the cash register before processing a refund that returns cash.");
            }

            var refund = new Sale
            {
                ReceiptNumber = await GenerateReceiptNumberAsync(),
                UserId = draft.UserId,
                CustomerId = original.CustomerId,
                CashSessionId = openSessionId,
                Status = SaleStatus.Refunded,
                SaleDate = DateTime.UtcNow,
                Subtotal = subtotal,
                DiscountTotal = discountTotal,
                TaxTotal = taxTotal,
                Rounding = rounding,
                AmountPaid = refundTotal,
                Change = 0m,
                RefundedSaleId = original.Id,
                ServiceType = original.ServiceType,
                Note = Clean(draft.Reason) ?? $"Refund of {original.ReceiptNumber}"
            };

            refund.Payments.Add(new SalePayment
            {
                Sale = refund,
                Method = draft.PaymentMethod,
                Amount = refundTotal,
                Reference = $"Refund {original.ReceiptNumber}"
            });

            var stockLinks = new List<(StockTransaction Transaction, SaleItem Item)>();
            foreach (var selection in selections)
            {
                var item = selection.Item;
                var refundItem = new SaleItem
                {
                    Sale = refund,
                    ProductId = item.ProductId,
                    ProductName = item.ProductName,
                    Sku = item.Sku,
                    Quantity = -selection.Quantity,
                    UnitPrice = item.UnitPrice,
                    CostPrice = item.CostPrice,
                    TaxRate = item.TaxRate,
                    DiscountAmount = -selection.Discount,
                    DiscountReason = item.DiscountReason,
                    PromotionId = item.PromotionId,
                    RefundedSaleItemId = item.Id,
                    IsRefunded = true
                };
                refund.Items.Add(refundItem);

                var product = await _db.Products.FindAsync(item.ProductId);
                if (product?.StockQuantity is not decimal current) continue;
                product.StockQuantity = current + selection.Quantity;
                product.UpdatedAt = DateTime.UtcNow;
                var ledger = new StockTransaction
                {
                    ProductId = product.Id,
                    Type = StockTransactionType.Return,
                    Quantity = selection.Quantity,
                    BalanceAfter = product.StockQuantity.Value,
                    UnitCost = item.CostPrice,
                    UserId = draft.UserId,
                    Note = $"Refund of sale {original.ReceiptNumber}"
                };
                _db.StockTransactions.Add(ledger);
                stockLinks.Add((ledger, refundItem));
            }

            if (allRemainingSelected) await ReleasePromotionsAsync(original.Items);
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
            _db.ChangeTracker.Clear();
            return refund;
        }
        catch
        {
            await transaction.RollbackAsync();
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    private static Dictionary<int, decimal> BuildReturnedQuantityMap(
        IEnumerable<SaleItem> originalItems,
        IEnumerable<SaleItem> refundItems)
    {
        var originals = originalItems.ToList();
        var result = originals.ToDictionary(item => item.Id, _ => 0m);
        foreach (var refundItem in refundItems)
        {
            var originalId = refundItem.RefundedSaleItemId;
            if (!originalId.HasValue || !result.ContainsKey(originalId.Value))
            {
                originalId = originals
                    .Where(item => item.ProductId == refundItem.ProductId &&
                                   Math.Abs(item.UnitPrice - refundItem.UnitPrice) < 0.0001m)
                    .OrderBy(item => item.Id)
                    .Select(item => (int?)item.Id)
                    .FirstOrDefault();
            }
            if (originalId.HasValue)
                result[originalId.Value] += Math.Abs(refundItem.Quantity);
        }
        return result;
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
