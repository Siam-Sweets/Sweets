using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Core.Utilities;
using PosApp.Data;

namespace PosApp.Services;

public class ReportService : IReportService
{
    private readonly AppDbContext _db;
    private readonly IUserSessionContext _session;
    public ReportService(AppDbContext db, IUserSessionContext session)
    {
        _db = db;
        _session = session;
    }

    public Task<DailySalesReport> GetDailyReportAsync(DateTime date)
        => GetDailyReportAsync(date, _db.CurrentStoreId);

    public async Task<DailySalesReport> GetDailyReportAsync(DateTime date, int? storeId)
    {
        storeId = ResolveReadableStoreId(storeId);
        var range = DateTimeUtilities.InclusiveLocalDateRange(date, date);
        var sales = await LoadFinancialSalesAsync(range.FromUtc, range.ToUtcExclusive, storeId);
        return BuildDaily(date.Date, sales);
    }

    public Task<DateRangeReport> GetRangeReportAsync(DateTime from, DateTime to)
        => GetRangeReportAsync(from, to, _db.CurrentStoreId);

    public async Task<DateRangeReport> GetRangeReportAsync(DateTime from, DateTime to, int? storeId)
    {
        storeId = ResolveReadableStoreId(storeId);
        var range = DateTimeUtilities.InclusiveLocalDateRange(from, to);
        var sales = await LoadFinancialSalesAsync(range.FromUtc, range.ToUtcExclusive, storeId);
        var report = new DateRangeReport { From = from.Date, To = to.Date };
        foreach (var sale in sales) Accumulate(report, sale);
        foreach (var group in sales.GroupBy(s => DateTimeUtilities.ToLocal(s.SaleDate).Date).OrderBy(g => g.Key))
            report.Daily.Add(BuildDaily(group.Key, group));
        return report;
    }

    public Task<IReadOnlyList<TopProductRow>> GetTopProductsAsync(DateTime from, DateTime to, int top = 10)
        => GetTopProductsAsync(from, to, top, _db.CurrentStoreId);

    public async Task<IReadOnlyList<TopProductRow>> GetTopProductsAsync(
        DateTime from, DateTime to, int top, int? storeId)
    {
        storeId = ResolveReadableStoreId(storeId);
        var range = DateTimeUtilities.InclusiveLocalDateRange(from, to);
        var query = _db.SaleItems.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.Sale != null && i.Sale.SaleDate >= range.FromUtc &&
                        i.Sale.SaleDate < range.ToUtcExclusive &&
                        (i.Sale.Status == SaleStatus.Completed || i.Sale.Status == SaleStatus.Refunded));
        if (storeId.HasValue) query = query.Where(i => i.StoreId == storeId.Value);
        var items = await query.Select(i => new
        {
            i.StoreId, i.ProductId, i.ProductName, i.Sku, i.Quantity,
            i.UnitPrice, i.CostPrice, i.DiscountAmount
        }).ToListAsync();
        var stores = await _db.Stores.AsNoTracking().ToDictionaryAsync(x => x.Id);

        return items.GroupBy(i => new { i.StoreId, i.ProductId, i.ProductName, Sku = i.Sku ?? string.Empty })
            .Select(g => new TopProductRow
            {
                StoreId = g.Key.StoreId,
                StoreName = stores.GetValueOrDefault(g.Key.StoreId)?.Name ?? $"Store {g.Key.StoreId}",
                ProductId = g.Key.ProductId,
                ProductName = g.Key.ProductName,
                Sku = string.IsNullOrEmpty(g.Key.Sku) ? null : g.Key.Sku,
                QuantitySold = g.Sum(i => i.Quantity),
                Revenue = g.Sum(i => i.UnitPrice * i.Quantity - i.DiscountAmount),
                Profit = g.Sum(i => (i.UnitPrice - i.CostPrice) * i.Quantity - i.DiscountAmount)
            })
            .OrderByDescending(r => r.Revenue).Take(Math.Clamp(top, 1, 100)).ToList();
    }

    public Task<IReadOnlyList<SalesByHourRow>> GetSalesByHourAsync(DateTime date)
        => GetSalesByHourAsync(date, date, _db.CurrentStoreId);
    public Task<IReadOnlyList<SalesByHourRow>> GetSalesByHourAsync(DateTime from, DateTime to)
        => GetSalesByHourAsync(from, to, _db.CurrentStoreId);

    public async Task<IReadOnlyList<SalesByHourRow>> GetSalesByHourAsync(
        DateTime from, DateTime to, int? storeId)
    {
        storeId = ResolveReadableStoreId(storeId);
        var range = DateTimeUtilities.InclusiveLocalDateRange(from, to);
        var query = _db.Sales.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.SaleDate >= range.FromUtc && s.SaleDate < range.ToUtcExclusive &&
                        (s.Status == SaleStatus.Completed || s.Status == SaleStatus.Refunded));
        if (storeId.HasValue) query = query.Where(s => s.StoreId == storeId.Value);
        var sales = await query.Select(s => new
        {
            s.SaleDate, s.Status, s.Subtotal, s.DiscountTotal, s.TaxTotal, s.Rounding
        }).ToListAsync();
        return sales.GroupBy(s => DateTimeUtilities.ToLocal(s.SaleDate).Hour)
            .Select(g => new SalesByHourRow
            {
                Hour = g.Key,
                TransactionCount = g.Count(s => s.Status == SaleStatus.Completed),
                Revenue = g.Sum(s => s.Subtotal - s.DiscountTotal + s.TaxTotal + s.Rounding)
            }).OrderBy(r => r.Hour).ToList();
    }

    public Task<IReadOnlyList<SalesByCategoryRow>> GetSalesByCategoryAsync(DateTime from, DateTime to)
        => GetSalesByCategoryAsync(from, to, _db.CurrentStoreId);

    public async Task<IReadOnlyList<SalesByCategoryRow>> GetSalesByCategoryAsync(
        DateTime from, DateTime to, int? storeId)
    {
        storeId = ResolveReadableStoreId(storeId);
        var range = DateTimeUtilities.InclusiveLocalDateRange(from, to);
        var query = _db.SaleItems.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.Sale != null && i.Sale.SaleDate >= range.FromUtc &&
                        i.Sale.SaleDate < range.ToUtcExclusive &&
                        (i.Sale.Status == SaleStatus.Completed || i.Sale.Status == SaleStatus.Refunded));
        if (storeId.HasValue) query = query.Where(i => i.StoreId == storeId.Value);
        var items = await query.Select(i => new
        {
            CategoryName = string.IsNullOrEmpty(i.CategoryName) ? "Uncategorized" : i.CategoryName,
            i.Quantity, i.UnitPrice, i.DiscountAmount
        }).ToListAsync();
        return items.GroupBy(i => i.CategoryName)
            .Select(g => new SalesByCategoryRow
            {
                CategoryName = g.Key,
                QuantitySold = g.Sum(i => i.Quantity),
                Revenue = g.Sum(i => i.UnitPrice * i.Quantity - i.DiscountAmount)
            }).OrderByDescending(r => r.Revenue).ToList();
    }

    public Task<IReadOnlyList<PaymentBreakdownRow>> GetPaymentBreakdownAsync(DateTime from, DateTime to)
        => GetPaymentBreakdownAsync(from, to, _db.CurrentStoreId);

    public async Task<IReadOnlyList<PaymentBreakdownRow>> GetPaymentBreakdownAsync(
        DateTime from, DateTime to, int? storeId)
    {
        storeId = ResolveReadableStoreId(storeId);
        var range = DateTimeUtilities.InclusiveLocalDateRange(from, to);
        var query = _db.SalePayments.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Sale != null && p.Sale.SaleDate >= range.FromUtc &&
                        p.Sale.SaleDate < range.ToUtcExclusive &&
                        (p.Sale.Status == SaleStatus.Completed || p.Sale.Status == SaleStatus.Refunded));
        if (storeId.HasValue) query = query.Where(p => p.StoreId == storeId.Value);
        var payments = await query.Select(p => new { p.SaleId, p.Method, p.Amount }).ToListAsync();
        return payments.GroupBy(p => p.Method)
            .Select(g => new PaymentBreakdownRow
            {
                Method = g.Key,
                Count = g.Select(p => p.SaleId).Distinct().Count(),
                Total = g.Sum(p => p.Amount)
            }).OrderByDescending(r => r.Total).ToList();
    }

    public Task<IReadOnlyList<StorePerformanceRow>> GetStorePerformanceAsync(DateTime from, DateTime to)
        => GetStorePerformanceAsync(from, to, null);

    public async Task<IReadOnlyList<StorePerformanceRow>> GetStorePerformanceAsync(
        DateTime from, DateTime to, int? storeId)
    {
        storeId = ResolveReadableStoreId(storeId);
        var range = DateTimeUtilities.InclusiveLocalDateRange(from, to);
        var sales = await LoadFinancialSalesAsync(range.FromUtc, range.ToUtcExclusive, storeId);
        var stores = await _db.Stores.AsNoTracking().ToDictionaryAsync(x => x.Id);
        return sales.GroupBy(x => x.StoreId).Select(group =>
        {
            var row = new DateRangeReport();
            foreach (var sale in group) Accumulate(row, sale);
            var store = stores.GetValueOrDefault(group.Key);
            return new StorePerformanceRow
            {
                StoreId = group.Key,
                StoreCode = store?.Code ?? $"#{group.Key}",
                StoreName = store?.Name ?? $"Store {group.Key}",
                TransactionCount = row.TransactionCount,
                NetSales = row.NetSales,
                GrossProfit = row.GrossProfit
            };
        }).OrderByDescending(x => x.NetSales).ThenBy(x => x.StoreName).ToList();
    }

    private int? ResolveReadableStoreId(int? requested)
    {
        if (_session.IsAdmin) return requested;
        var allowed = _session.StoreId ?? _db.CurrentStoreId;
        if (requested.HasValue && requested.Value != allowed)
            throw new UnauthorizedAccessException("You can only view reports for the selected store.");
        return allowed;
    }

    private async Task<List<Sale>> LoadFinancialSalesAsync(
        DateTime fromUtc, DateTime toUtcExclusive, int? storeId)
    {
        var query = _db.Sales.IgnoreQueryFilters().AsNoTracking()
            .Include(s => s.Items).Include(s => s.Payments)
            .Where(s => s.SaleDate >= fromUtc && s.SaleDate < toUtcExclusive &&
                        (s.Status == SaleStatus.Completed || s.Status == SaleStatus.Refunded));
        if (storeId.HasValue) query = query.Where(s => s.StoreId == storeId.Value);
        return await query.ToListAsync();
    }

    private static DailySalesReport BuildDaily(DateTime date, IEnumerable<Sale> sales)
    {
        var report = new DailySalesReport { Date = date.Date };
        foreach (var sale in sales) Accumulate(report, sale);
        return report;
    }

    private static void Accumulate(DailySalesReport report, Sale sale)
    {
        if (sale.Status == SaleStatus.Refunded)
        {
            report.RefundCount++;
            report.RefundTotal += Math.Abs(sale.Total);
        }
        else report.TransactionCount++;
        report.ItemCount += sale.Items.Sum(i => i.Quantity);
        report.GrossSales += sale.Subtotal;
        report.DiscountTotal += sale.DiscountTotal;
        report.TaxTotal += sale.TaxTotal;
        report.NetSales += sale.Total;
        report.CostOfGoods += sale.Items.Sum(i => i.CostPrice * i.Quantity);
        foreach (var payment in sale.Payments)
            report.ByPaymentMethod[payment.Method] = report.ByPaymentMethod.GetValueOrDefault(payment.Method) + payment.Amount;
    }

    private static void Accumulate(DateRangeReport report, Sale sale)
    {
        if (sale.Status == SaleStatus.Completed) report.TransactionCount++;
        report.ItemCount += sale.Items.Sum(i => i.Quantity);
        report.GrossSales += sale.Subtotal;
        report.DiscountTotal += sale.DiscountTotal;
        report.TaxTotal += sale.TaxTotal;
        report.NetSales += sale.Total;
        report.CostOfGoods += sale.Items.Sum(i => i.CostPrice * i.Quantity);
    }
}
