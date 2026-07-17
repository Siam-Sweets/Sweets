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
    public ReportService(AppDbContext db) => _db = db;

    public async Task<DailySalesReport> GetDailyReportAsync(DateTime date)
    {
        var range = DateTimeUtilities.InclusiveLocalDateRange(date, date);
        var sales = await LoadFinancialSalesAsync(range.FromUtc, range.ToUtcExclusive);
        return BuildDaily(date.Date, sales);
    }

    public async Task<DateRangeReport> GetRangeReportAsync(DateTime from, DateTime to)
    {
        var range = DateTimeUtilities.InclusiveLocalDateRange(from, to);
        var sales = await LoadFinancialSalesAsync(range.FromUtc, range.ToUtcExclusive);
        var report = new DateRangeReport { From = from.Date, To = to.Date };
        foreach (var sale in sales) Accumulate(report, sale);

        foreach (var group in sales.GroupBy(s => DateTimeUtilities.ToLocal(s.SaleDate).Date).OrderBy(g => g.Key))
            report.Daily.Add(BuildDaily(group.Key, group));
        return report;
    }

    public async Task<IReadOnlyList<TopProductRow>> GetTopProductsAsync(DateTime from, DateTime to, int top = 10)
    {
        var range = DateTimeUtilities.InclusiveLocalDateRange(from, to);
        var items = await _db.SaleItems.AsNoTracking()
            .Where(i => i.Sale!.SaleDate >= range.FromUtc && i.Sale.SaleDate < range.ToUtcExclusive &&
                        (i.Sale.Status == SaleStatus.Completed || i.Sale.Status == SaleStatus.Refunded))
            .Select(i => new { i.ProductId, i.ProductName, i.Sku, i.Quantity, i.UnitPrice, i.CostPrice, i.DiscountAmount })
            .ToListAsync();

        return items.GroupBy(i => new { i.ProductId, i.ProductName, i.Sku })
            .Select(g => new TopProductRow
            {
                ProductId = g.Key.ProductId,
                ProductName = g.Key.ProductName,
                Sku = g.Key.Sku,
                QuantitySold = g.Sum(i => i.Quantity),
                Revenue = g.Sum(i => i.UnitPrice * i.Quantity - i.DiscountAmount),
                Profit = g.Sum(i => (i.UnitPrice - i.CostPrice) * i.Quantity - i.DiscountAmount)
            })
            .OrderByDescending(r => r.Revenue).Take(Math.Clamp(top, 1, 100)).ToList();
    }

    public async Task<IReadOnlyList<SalesByHourRow>> GetSalesByHourAsync(DateTime date)
    {
        var range = DateTimeUtilities.InclusiveLocalDateRange(date, date);
        var sales = await _db.Sales.AsNoTracking()
            .Where(s => s.SaleDate >= range.FromUtc && s.SaleDate < range.ToUtcExclusive &&
                        (s.Status == SaleStatus.Completed || s.Status == SaleStatus.Refunded))
            .Select(s => new { s.SaleDate, s.Subtotal, s.DiscountTotal, s.TaxTotal, s.Rounding })
            .ToListAsync();

        return sales.GroupBy(s => DateTimeUtilities.ToLocal(s.SaleDate).Hour)
            .Select(g => new SalesByHourRow
            {
                Hour = g.Key,
                TransactionCount = g.Count(s => s.Subtotal >= 0m),
                Revenue = g.Sum(s => s.Subtotal - s.DiscountTotal + s.TaxTotal + s.Rounding)
            }).OrderBy(r => r.Hour).ToList();
    }

    public async Task<IReadOnlyList<SalesByCategoryRow>> GetSalesByCategoryAsync(DateTime from, DateTime to)
    {
        var range = DateTimeUtilities.InclusiveLocalDateRange(from, to);
        var items = await _db.SaleItems.AsNoTracking()
            .Where(i => i.Sale!.SaleDate >= range.FromUtc && i.Sale.SaleDate < range.ToUtcExclusive &&
                        (i.Sale.Status == SaleStatus.Completed || i.Sale.Status == SaleStatus.Refunded))
            .Select(i => new
            {
                CategoryName = i.Product != null && i.Product.Category != null ? i.Product.Category.Name : "Uncategorized",
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

    public async Task<IReadOnlyList<PaymentBreakdownRow>> GetPaymentBreakdownAsync(DateTime from, DateTime to)
    {
        var range = DateTimeUtilities.InclusiveLocalDateRange(from, to);
        var payments = await _db.SalePayments.AsNoTracking()
            .Where(p => p.Sale!.SaleDate >= range.FromUtc && p.Sale.SaleDate < range.ToUtcExclusive &&
                        (p.Sale.Status == SaleStatus.Completed || p.Sale.Status == SaleStatus.Refunded))
            .Select(p => new { p.SaleId, p.Method, p.Amount }).ToListAsync();

        return payments.GroupBy(p => p.Method)
            .Select(g => new PaymentBreakdownRow
            {
                Method = g.Key,
                Count = g.Select(p => p.SaleId).Distinct().Count(),
                Total = g.Sum(p => p.Amount)
            }).OrderByDescending(r => r.Total).ToList();
    }

    private async Task<List<Sale>> LoadFinancialSalesAsync(DateTime fromUtc, DateTime toUtcExclusive)
        => await _db.Sales.AsNoTracking().Include(s => s.Items).Include(s => s.Payments)
            .Where(s => s.SaleDate >= fromUtc && s.SaleDate < toUtcExclusive &&
                        (s.Status == SaleStatus.Completed || s.Status == SaleStatus.Refunded))
            .ToListAsync();

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
        else
        {
            report.TransactionCount++;
        }
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
