using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Core.Utilities;
using PosApp.Data;

namespace PosApp.Services;

/// <summary>
/// Atomic, idempotent sale, suspension, void, refund, payment, promotion, and
/// stock processing. All inventory movements are protected by optimistic
/// concurrency and deterministic append-only ledger keys.
/// </summary>
public class SaleService : ISaleService
{
    private readonly AppDbContext _db;
    private readonly ISettingsService _settings;

    public SaleService(AppDbContext db, ISettingsService settings)
    {
        _db = db;
        _settings = settings;
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
        draft.OperationId = NormalizeOperationId(draft.OperationId, "sale");
        if (draft.UserId <= 0) throw new InvalidOperationException("A signed-in user is required.");

        _db.ChangeTracker.Clear();
        await EnsureActiveUserAsync(draft.UserId);
        await EnsureActiveCustomerAsync(draft.CustomerId);
        using var operation = SyncOperationScope.Begin(draft.OperationId);

        Sale? sale = null;
        if (draft.SuspendedSaleId.HasValue)
        {
            if (draft.SuspendedSaleId.Value <= 0)
                throw new InvalidOperationException("The saved sale reference is invalid.");
            sale = await _db.Sales.Include(s => s.Items).Include(s => s.Payments)
                .FirstOrDefaultAsync(s => s.Id == draft.SuspendedSaleId.Value &&
                                          s.Status == SaleStatus.Suspended);
            if (sale == null)
                throw new InvalidOperationException(
                    "The saved sale is no longer available. Refresh the open-sales list.");
        }
        sale ??= await _db.Sales.Include(s => s.Items).Include(s => s.Payments)
            .FirstOrDefaultAsync(s => s.OperationId == draft.OperationId);
        if (sale is { Status: not SaleStatus.Suspended }) return sale;

        if (sale == null)
        {
            sale = new Sale
            {
                OperationId = draft.OperationId,
                ReceiptNumber = await GenerateReceiptNumberAsync(),
                Status = SaleStatus.Suspended
            };
            _db.Sales.Add(sale);
        }
        else
        {
            _db.SaleItems.RemoveRange(sale.Items.ToList());
            _db.SalePayments.RemoveRange(sale.Payments.ToList());
            sale.Items.Clear();
            sale.Payments.Clear();
            sale.UpdatedAt = DateTime.UtcNow;
            sale.OperationId = draft.OperationId;
        }

        sale.CustomerId = draft.CustomerId;
        sale.UserId = draft.UserId;
        sale.SaleDate = DateTime.UtcNow;
        sale.Subtotal = draft.Subtotal;
        sale.DiscountTotal = draft.DiscountTotal;
        sale.TaxTotal = draft.TaxTotal;
        sale.Rounding = 0m;
        sale.AmountPaid = 0m;
        sale.Change = 0m;
        sale.Note = Clean(draft.Note, 500, "Sale note");
        sale.ServiceType = NormalizeServiceType(draft.ServiceType);
        foreach (var line in draft.Lines) sale.Items.Add(BuildSaleItem(line, sale));

        await _db.SaveChangesAsync();
        var id = sale.Id;
        _db.ChangeTracker.Clear();
        return await GetSaleByIdAsync(id) ?? sale;
    }

    public async Task<Sale> RecallSuspendedAsync(int saleId)
        => saleId <= 0
            ? throw new InvalidOperationException("Select a valid saved sale.")
            : await _db.Sales.AsNoTracking().Include(s => s.Items).Include(s => s.Customer)
               .FirstOrDefaultAsync(s => s.Id == saleId && s.Status == SaleStatus.Suspended)
              ?? throw new InvalidOperationException("Suspended sale not found.");

    public async Task<IReadOnlyList<Sale>> GetSuspendedSalesAsync()
        => await _db.Sales.AsNoTracking().Include(s => s.Items).Include(s => s.Customer)
            .Where(s => s.Status == SaleStatus.Suspended)
            .OrderByDescending(s => s.SaleDate).ToListAsync();

    public async Task<Sale> CheckoutAsync(SaleDraft draft)
    {
        ValidateDraftLines(draft);
        draft.OperationId = NormalizeOperationId(draft.OperationId, "sale");
        if (draft.UserId <= 0) throw new InvalidOperationException("A signed-in user is required.");
        ValidatePayments(draft);

        var existing = await _db.Sales.AsNoTracking().Include(s => s.Items).Include(s => s.Payments)
            .FirstOrDefaultAsync(s => s.OperationId == draft.OperationId &&
                                      s.Status != SaleStatus.Suspended);
        if (existing != null) return existing;

        var total = draft.Total;
        var appliedPayment = draft.Payments.Sum(p => p.Amount);
        var amountTendered = draft.AmountTendered > 0m ? draft.AmountTendered : appliedPayment;
        var settings = await _settings.GetStoreSettingsAsync();

        using var operation = SyncOperationScope.Begin(draft.OperationId);
        _db.ChangeTracker.Clear();
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            await EnsureActiveUserAsync(draft.UserId);
            await EnsureActiveCustomerAsync(draft.CustomerId);
            var openSessionId = await GetOpenSessionIdAsync();
            if (settings.RequireOpenRegisterForSales && !openSessionId.HasValue)
                throw new InvalidOperationException("Open the cash register before completing this sale.");

            Sale? sale = null;
            if (draft.SuspendedSaleId.HasValue)
            {
                if (draft.SuspendedSaleId.Value <= 0)
                    throw new InvalidOperationException("The saved sale reference is invalid.");
                sale = await _db.Sales.Include(s => s.Items).Include(s => s.Payments)
                    .FirstOrDefaultAsync(s => s.Id == draft.SuspendedSaleId.Value &&
                                              s.Status == SaleStatus.Suspended);
                if (sale == null)
                    throw new InvalidOperationException(
                        "The saved sale is no longer available. Refresh the open-sales list before checkout.");
            }
            sale ??= await _db.Sales.Include(s => s.Items).Include(s => s.Payments)
                .FirstOrDefaultAsync(s => s.OperationId == draft.OperationId &&
                                          s.Status == SaleStatus.Suspended);

            var receiptNo = sale?.ReceiptNumber ?? await GenerateReceiptNumberAsync();
            if (sale == null)
            {
                sale = new Sale { OperationId = draft.OperationId, ReceiptNumber = receiptNo };
                _db.Sales.Add(sale);
            }
            else
            {
                _db.SaleItems.RemoveRange(sale.Items.ToList());
                _db.SalePayments.RemoveRange(sale.Payments.ToList());
                sale.Items.Clear();
                sale.Payments.Clear();
                sale.OperationId = draft.OperationId;
            }

            await ValidateAndConsumePromotionsAsync(draft);
            sale.CustomerId = draft.CustomerId;
            sale.UserId = draft.UserId;
            sale.CashSessionId = openSessionId;
            sale.Status = SaleStatus.Completed;
            sale.SaleDate = DateTime.UtcNow;
            sale.Subtotal = draft.Subtotal;
            sale.DiscountTotal = draft.DiscountTotal;
            sale.TaxTotal = draft.TaxTotal;
            sale.Rounding = 0m;
            sale.AmountPaid = amountTendered;
            sale.Change = Math.Max(0m, amountTendered - total);
            sale.Note = Clean(draft.Note, 500, "Sale note");
            sale.ServiceType = NormalizeServiceType(draft.ServiceType);
            sale.UpdatedAt = DateTime.UtcNow;

            foreach (var payment in draft.Payments)
            {
                sale.Payments.Add(new SalePayment
                {
                    Sale = sale,
                    Method = payment.Method,
                    Amount = payment.Amount,
                    Reference = Clean(payment.Reference, 64, "Payment reference")
                });
            }

            var pendingStockMovements = new List<(
                int ProductId,
                string OperationKey,
                decimal Quantity,
                decimal BalanceAfter,
                decimal? UnitCost,
                SaleItem Item,
                string Note)>();
            var lineNumber = 0;
            foreach (var line in draft.Lines)
            {
                var product = await _db.Products.Include(x => x.Category)
                    .FirstOrDefaultAsync(p => p.Id == line.ProductId && p.IsActive)
                    ?? throw new InvalidOperationException(
                        $"Product not found or inactive: {line.ProductName}");
                if (line.DiscountAmount > 0m && !product.AllowDiscount)
                    throw new InvalidOperationException($"Discounts are disabled for {product.Name}.");

                var item = BuildSaleItem(line, sale);
                item.ProductName = product.Name;
                item.Sku = product.Sku;
                item.CategoryName = product.Category?.Name ?? "Uncategorized";
                item.CostPrice = product.CostPrice;
                item.Unit = product.EffectiveUnit;
                sale.Items.Add(item);

                if (!product.StockQuantity.HasValue) continue;
                var balance = product.StockQuantity.Value - line.Quantity;
                if (balance < 0m)
                    throw new InvalidOperationException($"Insufficient stock for {product.Name}.");
                product.StockQuantity = balance;
                product.UpdatedAt = DateTime.UtcNow;
                pendingStockMovements.Add((
                    product.Id,
                    $"{draft.OperationId}:sale:{++lineNumber}",
                    -line.Quantity,
                    balance,
                    product.CostPrice,
                    item,
                    $"Sale {receiptNo}"));
            }

            // Save the sale and its items first so their generated IDs are known.
            // Ledger rows are then inserted once with final foreign keys; an
            // append-only row is never updated after insertion.
            await _db.SaveChangesAsync();
            foreach (var movement in pendingStockMovements)
            {
                _db.StockTransactions.Add(new StockTransaction
                {
                    ProductId = movement.ProductId,
                    OperationKey = movement.OperationKey,
                    Type = StockTransactionType.Sale,
                    Quantity = movement.Quantity,
                    BalanceAfter = movement.BalanceAfter,
                    UnitCost = movement.UnitCost,
                    SaleId = sale.Id,
                    SaleItemId = movement.Item.Id,
                    UserId = draft.UserId,
                    Note = movement.Note
                });
            }
            if (pendingStockMovements.Count > 0) await _db.SaveChangesAsync();
            await _db.CommitExternalTransactionAsync(transaction);
            var id = sale.Id;
            _db.ChangeTracker.Clear();
            return await GetSaleByIdAsync(id) ?? sale;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            await _db.RollbackExternalTransactionAsync(transaction);
            throw new InvalidOperationException(
                "Stock or promotion availability changed while checkout was processing. Reload the cart and try again.", ex);
        }
        catch (DbUpdateException)
        {
            await _db.RollbackExternalTransactionAsync(transaction);
            var duplicate = await _db.Sales.AsNoTracking().Include(s => s.Items).Include(s => s.Payments)
                .FirstOrDefaultAsync(s => s.OperationId == draft.OperationId &&
                                          s.Status != SaleStatus.Suspended);
            if (duplicate != null) return duplicate;
            throw;
        }
        catch
        {
            await _db.RollbackExternalTransactionAsync(transaction);
            throw;
        }
    }

    public async Task<Sale?> GetSaleByIdAsync(int id)
        => id <= 0
            ? null
            : await _db.Sales.AsNoTracking().Include(s => s.Items).Include(s => s.Payments)
                .Include(s => s.Customer).Include(s => s.User).FirstOrDefaultAsync(s => s.Id == id);

    public async Task<IReadOnlyList<Sale>> GetSalesAsync(DateTime from, DateTime to, SaleStatus? status = null)
    {
        var range = DateTimeUtilities.InclusiveLocalDateRange(from, to);
        var query = _db.Sales.AsNoTracking().Include(s => s.Items).Include(s => s.Customer)
            .Include(s => s.User)
            .Where(s => s.SaleDate >= range.FromUtc && s.SaleDate < range.ToUtcExclusive);
        if (status.HasValue) query = query.Where(s => s.Status == status.Value);

        var sales = await query.OrderByDescending(s => s.SaleDate).ToListAsync();
        var completedIds = sales.Where(s => s.Status == SaleStatus.Completed).Select(s => s.Id).ToList();
        if (completedIds.Count == 0) return sales;

        var refunds = await _db.Sales.AsNoTracking()
            .Where(s => s.Status == SaleStatus.Refunded && s.RefundedSaleId.HasValue &&
                        completedIds.Contains(s.RefundedSaleId.Value))
            .Select(s => new
            {
                OriginalSaleId = s.RefundedSaleId!.Value,
                s.Subtotal, s.DiscountTotal, s.TaxTotal, s.Rounding
            }).ToListAsync();
        var refundedBySale = refunds.GroupBy(x => x.OriginalSaleId).ToDictionary(
            group => group.Key,
            group => new
            {
                Count = group.Count(),
                Total = group.Sum(refund => Math.Abs(
                    refund.Subtotal - refund.DiscountTotal + refund.TaxTotal + refund.Rounding))
            });
        foreach (var sale in sales.Where(x => x.Status == SaleStatus.Completed))
        {
            if (!refundedBySale.TryGetValue(sale.Id, out var state)) continue;
            sale.HasRefund = state.Count > 0;
            sale.IsFullyRefunded = state.Total + 0.0001m >= Math.Abs(sale.Total);
        }
        return sales;
    }

    public async Task<Sale> VoidSaleAsync(int saleId, int userId)
    {
        if (saleId <= 0) throw new InvalidOperationException("Select a completed sale to void.");
        if (userId <= 0) throw new InvalidOperationException("A signed-in user is required.");
        _db.ChangeTracker.Clear();
        var preview = await _db.Sales.AsNoTracking().FirstOrDefaultAsync(x => x.Id == saleId)
                      ?? throw new InvalidOperationException("Sale not found.");
        if (preview.Status == SaleStatus.Voided)
            return await GetSaleByIdAsync(saleId) ?? preview;
        var operationId = $"{preview.SyncId}:void";
        using var operation = SyncOperationScope.Begin(operationId);
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            await EnsureActiveUserAsync(userId);
            var sale = await _db.Sales.Include(s => s.Items).FirstAsync(s => s.Id == saleId);
            if (sale.Status == SaleStatus.Voided)
            {
                await _db.CommitExternalTransactionAsync(transaction);
                return sale;
            }
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
                        "This sale belongs to a closed register. Process a refund in an open register instead.");
            }

            var lineNumber = 0;
            foreach (var item in sale.Items)
            {
                var product = await _db.Products.FirstOrDefaultAsync(x => x.Id == item.ProductId);
                if (product?.StockQuantity is not decimal current) continue;
                product.StockQuantity = current + item.Quantity;
                product.UpdatedAt = DateTime.UtcNow;
                _db.StockTransactions.Add(new StockTransaction
                {
                    ProductId = product.Id,
                    OperationKey = $"{operationId}:{++lineNumber}",
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
            await _db.CommitExternalTransactionAsync(transaction);
            var id = sale.Id;
            _db.ChangeTracker.Clear();
            return await GetSaleByIdAsync(id) ?? sale;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            await _db.RollbackExternalTransactionAsync(transaction);
            throw new InvalidOperationException(
                "The sale or inventory changed while the void was processing. Reload and try again.", ex);
        }
        catch (DbUpdateException ex) when (IsOperationKeyDuplicate(ex))
        {
            await _db.RollbackExternalTransactionAsync(transaction);
            return await GetSaleByIdAsync(saleId) ?? preview;
        }
        catch
        {
            await _db.RollbackExternalTransactionAsync(transaction);
            throw;
        }
    }

    public async Task<Sale> RefundSaleAsync(int saleId, int userId, string? reason = null)
    {
        if (saleId <= 0) throw new InvalidOperationException("Select a completed sale to refund.");
        if (userId <= 0) throw new InvalidOperationException("A signed-in user is required.");
        _db.ChangeTracker.Clear();
        var original = await _db.Sales.AsNoTracking().Include(s => s.Items).Include(s => s.Payments)
            .FirstOrDefaultAsync(s => s.Id == saleId)
            ?? throw new InvalidOperationException("Sale not found.");
        var lines = original.Items
            .Select(item => new RefundDraftLine
            {
                SaleItemId = item.Id,
                Quantity = Math.Max(0m, item.Quantity - item.RefundedQuantity)
            })
            .Where(line => line.Quantity > 0.0001m).ToList();
        if (lines.Count == 0)
            throw new InvalidOperationException("This sale has already been fully refunded.");

        var refundable = Math.Abs(original.Total) - await GetPriorRefundTotalAsync(original.Id);
        return await RefundSaleAsync(new RefundDraft
        {
            SaleId = saleId,
            UserId = userId,
            Reason = reason,
            Lines = lines,
            Payments = AllocateRefundPayments(original.Payments, refundable)
        });
    }

    public async Task<Sale> RefundSaleAsync(RefundDraft draft)
    {
        ValidateRefundDraft(draft);
        draft.OperationId = NormalizeOperationId(draft.OperationId, "refund");
        var existing = await _db.Sales.AsNoTracking().Include(s => s.Items).Include(s => s.Payments)
            .FirstOrDefaultAsync(s => s.OperationId == draft.OperationId &&
                                      s.Status == SaleStatus.Refunded);
        if (existing != null) return existing;

        using var operation = SyncOperationScope.Begin(draft.OperationId);
        _db.ChangeTracker.Clear();
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            await EnsureActiveUserAsync(draft.UserId);
            var original = await _db.Sales.Include(s => s.Items).Include(s => s.Payments)
                .FirstOrDefaultAsync(s => s.Id == draft.SaleId)
                ?? throw new InvalidOperationException("Sale not found.");
            if (original.Status != SaleStatus.Completed)
                throw new InvalidOperationException("Only completed sales can be refunded.");

            var requestedByLine = draft.Lines.GroupBy(line => line.SaleItemId)
                .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity));
            var originalById = original.Items.ToDictionary(item => item.Id);
            var selections = new List<(SaleItem Item, decimal Quantity, decimal Discount, decimal Tax)>();
            foreach (var request in requestedByLine)
            {
                if (!originalById.TryGetValue(request.Key, out var item))
                    throw new InvalidOperationException("A selected refund line does not belong to this sale.");
                var remaining = item.Quantity - item.RefundedQuantity;
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
                item.Quantity - item.RefundedQuantity -
                requestedByLine.GetValueOrDefault(item.Id) <= 0.0001m);
            var subtotal = -selections.Sum(x => x.Item.UnitPrice * x.Quantity);
            var discountTotal = -selections.Sum(x => x.Discount);
            var taxTotal = -selections.Sum(x => x.Tax);
            var priorRefundTotal = await GetPriorRefundTotalAsync(original.Id);
            var remainingFinancialTotal = Math.Max(0m, Math.Abs(original.Total) - priorRefundTotal);
            var beforeRounding = subtotal - discountTotal + taxTotal;
            var rounding = allRemainingSelected ? -remainingFinancialTotal - beforeRounding : 0m;
            var refundTotal = subtotal - discountTotal + taxTotal + rounding;
            if (refundTotal >= -0.0001m)
                throw new InvalidOperationException("The selected items do not have a refundable value.");

            var refundPayments = NormalizeRefundPayments(draft, refundTotal, original.Payments);
            var openSessionId = await GetOpenSessionIdAsync();
            if (refundPayments.Any(x => x.Method == PaymentMethod.Cash) && !openSessionId.HasValue)
                throw new InvalidOperationException(
                    "Open the cash register before processing a refund that returns cash.");

            var refund = new Sale
            {
                OperationId = draft.OperationId,
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
                Note = Clean(draft.Reason, 500, "Refund reason") ??
                       $"Refund of {original.ReceiptNumber}"
            };
            foreach (var payment in refundPayments)
            {
                refund.Payments.Add(new SalePayment
                {
                    Sale = refund,
                    Method = payment.Method,
                    Amount = payment.Amount,
                    Reference = Clean(payment.Reference, 64, "Payment reference") ??
                                $"Refund {original.ReceiptNumber}"
                });
            }

            var pendingStockMovements = new List<(
                int ProductId,
                string OperationKey,
                decimal Quantity,
                decimal BalanceAfter,
                decimal? UnitCost,
                SaleItem Item,
                string Note)>();
            var lineNumber = 0;
            foreach (var selection in selections)
            {
                var item = selection.Item;
                item.RefundedQuantity += selection.Quantity;
                item.IsRefunded = item.RefundedQuantity + 0.0001m >= item.Quantity;
                var refundItem = new SaleItem
                {
                    Sale = refund,
                    ProductId = item.ProductId,
                    ProductName = item.ProductName,
                    Sku = item.Sku,
                    CategoryName = item.CategoryName,
                    Quantity = -selection.Quantity,
                    Unit = item.Unit,
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

                var product = await _db.Products.FirstOrDefaultAsync(x => x.Id == item.ProductId);
                if (product?.StockQuantity is not decimal current) continue;
                product.StockQuantity = current + selection.Quantity;
                product.UpdatedAt = DateTime.UtcNow;
                pendingStockMovements.Add((
                    product.Id,
                    $"{draft.OperationId}:refund:{++lineNumber}",
                    selection.Quantity,
                    product.StockQuantity.Value,
                    item.CostPrice,
                    refundItem,
                    $"Refund of sale {original.ReceiptNumber}"));
            }

            if (allRemainingSelected) await ReleasePromotionsAsync(original.Items);
            original.UpdatedAt = DateTime.UtcNow;
            _db.Sales.Add(refund);
            // Persist the refund and refund items before creating immutable
            // ledger rows, so every ledger row is inserted with final links.
            await _db.SaveChangesAsync();
            foreach (var movement in pendingStockMovements)
            {
                _db.StockTransactions.Add(new StockTransaction
                {
                    ProductId = movement.ProductId,
                    OperationKey = movement.OperationKey,
                    Type = StockTransactionType.Return,
                    Quantity = movement.Quantity,
                    BalanceAfter = movement.BalanceAfter,
                    UnitCost = movement.UnitCost,
                    SaleId = refund.Id,
                    SaleItemId = movement.Item.Id,
                    UserId = draft.UserId,
                    Note = movement.Note
                });
            }
            if (pendingStockMovements.Count > 0) await _db.SaveChangesAsync();
            await _db.CommitExternalTransactionAsync(transaction);
            var id = refund.Id;
            _db.ChangeTracker.Clear();
            return await GetSaleByIdAsync(id) ?? refund;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            await _db.RollbackExternalTransactionAsync(transaction);
            throw new InvalidOperationException(
                "The sale, refundable quantity, or inventory changed on another device. Reload and try again.", ex);
        }
        catch (DbUpdateException)
        {
            await _db.RollbackExternalTransactionAsync(transaction);
            var duplicate = await _db.Sales.AsNoTracking().Include(s => s.Items).Include(s => s.Payments)
                .FirstOrDefaultAsync(s => s.OperationId == draft.OperationId &&
                                          s.Status == SaleStatus.Refunded);
            if (duplicate != null) return duplicate;
            throw;
        }
        catch
        {
            await _db.RollbackExternalTransactionAsync(transaction);
            throw;
        }
    }

    private async Task ValidateAndConsumePromotionsAsync(SaleDraft draft)
    {
        var ids = draft.Lines.Where(l => l.PromotionId.HasValue)
            .Select(l => l.PromotionId!.Value).Distinct().ToList();
        if (ids.Count == 0) return;
        var now = DateTime.Now;
        var promotions = await _db.Discounts.Where(d => ids.Contains(d.Id)).ToListAsync();
        if (promotions.Count != ids.Count)
            throw new InvalidOperationException("A selected promotion no longer exists.");
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
                        $"The discount for promotion '{promotion.Name}' changed. Reapply it before checkout.");
            }
            promotion.UsedCount++;
        }
    }

    private async Task ReleasePromotionsAsync(IEnumerable<SaleItem> items)
    {
        var ids = items.Where(i => i.PromotionId.HasValue)
            .Select(i => i.PromotionId!.Value).Distinct().ToList();
        if (ids.Count == 0) return;
        var promotions = await _db.Discounts.Where(d => ids.Contains(d.Id)).ToListAsync();
        foreach (var promotion in promotions)
            promotion.UsedCount = Math.Max(0, promotion.UsedCount - 1);
    }

    private static SaleItem BuildSaleItem(SaleDraftLine line, Sale sale) => new()
    {
        Sale = sale,
        ProductId = line.ProductId,
        ProductName = line.ProductName,
        Sku = line.Sku,
        CategoryName = string.IsNullOrWhiteSpace(line.CategoryName)
            ? "Uncategorized" : line.CategoryName.Trim(),
        Quantity = line.Quantity,
        Unit = line.Unit,
        UnitPrice = line.UnitPrice,
        CostPrice = line.CostPrice,
        TaxRate = line.TaxRate,
        DiscountAmount = line.DiscountAmount,
        DiscountReason = Clean(line.DiscountReason, 200, "Discount reason"),
        PromotionId = line.PromotionId
    };

    private static void ValidateDraftLines(SaleDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Lines == null) throw new InvalidOperationException("The cart is unavailable.");
        if (draft.Payments == null) throw new InvalidOperationException("Payment information is unavailable.");
        if (draft.Lines.Count == 0) throw new InvalidOperationException("Cannot save an empty cart.");
        if (draft.Lines.Any(l => l.ProductId <= 0 || l.Quantity <= 0m || l.UnitPrice < 0m ||
                                 l.CostPrice < 0m || l.TaxRate is < 0m or > 100m ||
                                 l.DiscountAmount < 0m || l.DiscountAmount > l.UnitPrice * l.Quantity))
            throw new InvalidOperationException(
                "The cart contains an invalid product, quantity, price, tax, or discount.");
        if (draft.Lines.Any(line => string.IsNullOrWhiteSpace(line.ProductName) ||
                                    line.ProductName.Length > 100 || line.Sku?.Length > 64 ||
                                    line.CategoryName?.Length > 100))
            throw new InvalidOperationException("The cart contains invalid product snapshot data.");
    }

    private static void ValidatePayments(SaleDraft draft)
    {
        var total = draft.Total;
        if (total < -0.0001m) throw new InvalidOperationException("The sale total cannot be negative.");
        if (total > 0.0001m && draft.Payments.Count == 0)
            throw new InvalidOperationException("A payment is required to complete the sale.");
        if (draft.Payments.Any(p => p.Amount <= 0m || !Enum.IsDefined(p.Method)))
            throw new InvalidOperationException(
                "Payments must use a valid method and an amount greater than zero.");
        var appliedPayment = draft.Payments.Sum(p => p.Amount);
        if (Math.Abs(appliedPayment - total) > 0.0001m)
            throw new InvalidOperationException("Applied payments must equal the sale total.");
        var amountTendered = draft.AmountTendered > 0m ? draft.AmountTendered : appliedPayment;
        if (amountTendered + 0.0001m < appliedPayment)
            throw new InvalidOperationException("The received amount is less than the applied payments.");
        if (amountTendered - appliedPayment > 0.0001m &&
            !draft.Payments.Any(p => p.Method == PaymentMethod.Cash))
            throw new InvalidOperationException("Only cash payments can produce change.");
    }

    private static void ValidateRefundDraft(RefundDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.SaleId <= 0) throw new InvalidOperationException("Select a completed sale to refund.");
        if (draft.UserId <= 0) throw new InvalidOperationException("A signed-in user is required.");
        if (draft.Lines == null || draft.Lines.Count == 0)
            throw new InvalidOperationException("Select at least one item to refund.");
        if (!Enum.IsDefined(draft.PaymentMethod))
            throw new InvalidOperationException("Select a valid refund payment method.");
        if (draft.Lines.Any(line => line.SaleItemId <= 0 || line.Quantity <= 0m))
            throw new InvalidOperationException("Refund quantities must be greater than zero.");
    }

    private static List<SalePayment> NormalizeRefundPayments(
        RefundDraft draft,
        decimal refundTotal,
        IEnumerable<SalePayment> originalPayments)
    {
        var magnitude = Math.Abs(refundTotal);
        var supplied = draft.Payments?.Where(x => x.Amount != 0m).ToList() ?? new List<SalePayment>();
        if (supplied.Count == 0)
        {
            supplied = new List<SalePayment>
            {
                new() { Method = draft.PaymentMethod, Amount = -magnitude }
            };
        }
        if (supplied.Any(x => !Enum.IsDefined(x.Method)))
            throw new InvalidOperationException("A refund payment method is invalid.");
        var normalized = supplied.Select(x => new SalePayment
        {
            Method = x.Method,
            Amount = -Math.Abs(x.Amount),
            Reference = x.Reference
        }).ToList();
        if (Math.Abs(normalized.Sum(x => x.Amount) - refundTotal) > 0.0001m)
            throw new InvalidOperationException("Refund payments must equal the refund total.");
        return normalized;
    }

    private static List<SalePayment> AllocateRefundPayments(
        IEnumerable<SalePayment> originalPayments,
        decimal magnitude)
    {
        var payments = originalPayments.Where(x => x.Amount > 0m).ToList();
        if (payments.Count == 0)
            return new List<SalePayment>
            {
                new() { Method = PaymentMethod.Cash, Amount = -magnitude }
            };
        var total = payments.Sum(x => x.Amount);
        var allocated = new List<SalePayment>();
        decimal used = 0m;
        for (var i = 0; i < payments.Count; i++)
        {
            var amount = i == payments.Count - 1
                ? magnitude - used
                : Math.Round(magnitude * payments[i].Amount / total, 4,
                    MidpointRounding.AwayFromZero);
            used += amount;
            allocated.Add(new SalePayment
            {
                Method = payments[i].Method,
                Amount = -amount,
                Reference = payments[i].Reference
            });
        }
        return allocated;
    }

    private async Task<decimal> GetPriorRefundTotalAsync(int saleId)
    {
        var values = await _db.Sales.AsNoTracking()
            .Where(x => x.Status == SaleStatus.Refunded && x.RefundedSaleId == saleId)
            .Select(x => new { x.Subtotal, x.DiscountTotal, x.TaxTotal, x.Rounding })
            .ToListAsync();
        return values.Sum(x => Math.Abs(x.Subtotal - x.DiscountTotal + x.TaxTotal + x.Rounding));
    }

    private async Task<int?> GetOpenSessionIdAsync()
        => await _db.CashSessions.AsNoTracking()
            .Where(session => session.ClosedAt == null)
            .OrderByDescending(session => session.OpenedAt)
            .Select(session => (int?)session.Id)
            .FirstOrDefaultAsync();

    private async Task EnsureActiveUserAsync(int userId)
    {
        if (!await _db.Users.AsNoTracking().AnyAsync(user => user.Id == userId && user.IsActive))
            throw new InvalidOperationException(
                "The signed-in user no longer exists or is inactive. Sign in again.");
    }

    private async Task EnsureActiveCustomerAsync(int? customerId)
    {
        if (!customerId.HasValue) return;
        if (customerId.Value <= 0 ||
            !await _db.Customers.AsNoTracking().AnyAsync(customer =>
                customer.Id == customerId.Value && customer.IsActive))
            throw new InvalidOperationException(
                "The selected customer no longer exists or is inactive.");
    }

    private static string NormalizeOperationId(string? value, string label)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 8 or > 64 || normalized.Any(char.IsWhiteSpace))
            throw new InvalidOperationException($"The {label} operation ID is invalid.");
        return normalized;
    }

    private static bool IsOperationKeyDuplicate(DbUpdateException exception)
        => exception.InnerException?.Message.Contains("OperationKey", StringComparison.OrdinalIgnoreCase) == true ||
           exception.Message.Contains("OperationKey", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeServiceType(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "Retail" : value.Trim();
        return normalized[..Math.Min(normalized.Length, 32)];
    }

    private static string? Clean(string? value, int maxLength, string field)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalized?.Length > maxLength)
            throw new InvalidOperationException($"{field} cannot exceed {maxLength} characters.");
        return normalized;
    }
}
