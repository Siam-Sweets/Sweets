using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Data;

namespace PosApp.Services;

public class ReportService : IReportService
{
    private readonly AppDbContext _db;
    public ReportService(AppDbContext db) => _db = db;

    public async Task<DailySalesReport> GetDailyReportAsync(DateTime date)
    {
        var from = date.Date;
        var to = from.AddDays(1);
        var sales = await _db.Sales.AsNoTracking()
            .Include(s => s.Items).ThenInclude(i => i.Product)
            .Include(s => s.Payments)
            .Where(s => s.SaleDate >= from && s.SaleDate < to
                        && (s.Status == SaleStatus.Completed || s.Status == SaleStatus.Refunded))
            .ToListAsync();

        var report = new DailySalesReport { Date = from };
        foreach (var sale in sales)
        {
            if (sale.Status == SaleStatus.Refunded)
            {
                report.RefundCount++;
                report.RefundTotal += -sale.Total;
                continue;
            }
            report.TransactionCount++;
            report.ItemCount += sale.Items.Sum(i => (int)Math.Abs(i.Quantity));
            report.GrossSales += sale.Subtotal;
            report.DiscountTotal += sale.DiscountTotal;
            report.TaxTotal += sale.TaxTotal;
            report.NetSales += sale.Total;
            report.CostOfGoods += sale.Items.Sum(i => (i.Product?.CostPrice ?? 0m) * Math.Abs(i.Quantity));
            foreach (var pay in sale.Payments)
            {
                if (!report.ByPaymentMethod.ContainsKey(pay.Method))
                    report.ByPaymentMethod[pay.Method] = 0m;
                report.ByPaymentMethod[pay.Method] += pay.Amount;
            }
        }
        return report;
    }

    public async Task<DateRangeReport> GetRangeReportAsync(DateTime from, DateTime to)
    {
        var sales = await _db.Sales.AsNoTracking()
            .Include(s => s.Items).ThenInclude(i => i.Product)
            .Include(s => s.Payments)
            .Where(s => s.SaleDate >= from && s.SaleDate <= to
                        && s.Status == SaleStatus.Completed)
            .ToListAsync();

        var report = new DateRangeReport { From = from, To = to };
        foreach (var sale in sales)
        {
            report.TransactionCount++;
            report.ItemCount += sale.Items.Sum(i => (int)Math.Abs(i.Quantity));
            report.GrossSales += sale.Subtotal;
            report.DiscountTotal += sale.DiscountTotal;
            report.TaxTotal += sale.TaxTotal;
            report.NetSales += sale.Total;
            report.CostOfGoods += sale.Items.Sum(i => (i.Product?.CostPrice ?? 0m) * Math.Abs(i.Quantity));
        }

        // Group by day
        foreach (var grp in sales.GroupBy(s => s.SaleDate.Date))
        {
            var daily = new DailySalesReport { Date = grp.Key };
            foreach (var sale in grp)
            {
                daily.TransactionCount++;
                daily.ItemCount += sale.Items.Sum(i => (int)Math.Abs(i.Quantity));
                daily.GrossSales += sale.Subtotal;
                daily.DiscountTotal += sale.DiscountTotal;
                daily.TaxTotal += sale.TaxTotal;
                daily.NetSales += sale.Total;
                daily.CostOfGoods += sale.Items.Sum(i => (i.Product?.CostPrice ?? 0m) * Math.Abs(i.Quantity));
            }
            report.Daily.Add(daily);
        }
        return report;
    }

    public async Task<IReadOnlyList<TopProductRow>> GetTopProductsAsync(DateTime from, DateTime to, int top = 10)
    {
        var rows = await _db.SaleItems.AsNoTracking()
            .Include(i => i.Product)
            .Where(i => i.Sale.SaleDate >= from && i.Sale.SaleDate <= to
                        && i.Sale.Status == SaleStatus.Completed && !i.IsRefunded)
            .GroupBy(i => new { i.ProductId, i.ProductName, i.Sku })
            .Select(g => new TopProductRow
            {
                ProductId = g.Key.ProductId,
                ProductName = g.Key.ProductName,
                Sku = g.Key.Sku,
                QuantitySold = (int)g.Sum(i => i.Quantity),
                Revenue = g.Sum(i => i.LineTotal),
                Profit = g.Sum(i => (i.UnitPrice - (i.Product != null ? i.Product.CostPrice : 0m)) * i.Quantity)
            })
            .OrderByDescending(r => r.Revenue)
            .Take(top)
            .ToListAsync();
        return rows;
    }

    public async Task<IReadOnlyList<SalesByHourRow>> GetSalesByHourAsync(DateTime date)
    {
        var from = date.Date;
        var to = from.AddDays(1);
        var rows = await _db.Sales.AsNoTracking()
            .Where(s => s.SaleDate >= from && s.SaleDate < to && s.Status == SaleStatus.Completed)
            .GroupBy(s => s.SaleDate.Hour)
            .Select(g => new SalesByHourRow
            {
                Hour = g.Key,
                TransactionCount = g.Count(),
                Revenue = g.Sum(s => s.Total)
            })
            .OrderBy(r => r.Hour)
            .ToListAsync();
        return rows;
    }

    public async Task<IReadOnlyList<SalesByCategoryRow>> GetSalesByCategoryAsync(DateTime from, DateTime to)
    {
        var rows = await _db.SaleItems.AsNoTracking()
            .Include(i => i.Product).ThenInclude(p => p!.Category)
            .Where(i => i.Sale.SaleDate >= from && i.Sale.SaleDate <= to
                        && i.Sale.Status == SaleStatus.Completed && !i.IsRefunded)
            .GroupBy(i => i.Product != null && i.Product.Category != null ? i.Product.Category.Name : "Uncategorized")
            .Select(g => new SalesByCategoryRow
            {
                CategoryName = g.Key,
                QuantitySold = (int)g.Sum(i => i.Quantity),
                Revenue = g.Sum(i => i.LineTotal)
            })
            .OrderByDescending(r => r.Revenue)
            .ToListAsync();
        return rows;
    }

    public async Task<IReadOnlyList<PaymentBreakdownRow>> GetPaymentBreakdownAsync(DateTime from, DateTime to)
    {
        var rows = await _db.SalePayments.AsNoTracking()
            .Where(p => p.Sale.SaleDate >= from && p.Sale.SaleDate <= to
                        && p.Sale.Status == SaleStatus.Completed)
            .GroupBy(p => p.Method)
            .Select(g => new PaymentBreakdownRow
            {
                Method = g.Key,
                Count = g.Count(),
                Total = g.Sum(p => p.Amount)
            })
            .OrderByDescending(r => r.Total)
            .ToListAsync();
        return rows;
    }
}
