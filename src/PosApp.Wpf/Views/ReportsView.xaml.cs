using System.Text;
using System.Windows;
using System.Windows.Controls;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Core.Utilities;
using PosApp.Wpf.Helpers;

namespace PosApp.Wpf.Views;

public partial class ReportsView : UserControl, IRefreshable
{
    private readonly IReportService _reports;
    private readonly IHardwareService _hardware;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private string _activePeriod = "today";
    private int _loadVersion;
    private DateRangeReport? _currentRange;
    private IReadOnlyList<TopProductRow> _currentTopProducts = Array.Empty<TopProductRow>();
    private IReadOnlyList<SalesByCategoryRow> _currentCategories = Array.Empty<SalesByCategoryRow>();
    private IReadOnlyList<PaymentBreakdownRow> _currentPayments = Array.Empty<PaymentBreakdownRow>();

    public ReportsView(IReportService reports, IHardwareService hardware)
    {
        InitializeComponent();
        _reports = reports;
        _hardware = hardware;
        FromDate.SelectedDate = DateTime.Today.AddDays(-30);
        ToDate.SelectedDate = DateTime.Today;
    }

    public async Task RefreshAsync()
    {
        IsEnabled = false;
        try { await LoadAsync(); }
        finally { IsEnabled = true; }
    }

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        await _loadGate.WaitAsync();
        try
        {
            if (version != _loadVersion) return;
            var (from, to) = GetRange();

            var range = await _reports.GetRangeReportAsync(from, to);
            var top = await _reports.GetTopProductsAsync(from, to, 20);
            var cats = await _reports.GetSalesByCategoryAsync(from, to);
            var pay = await _reports.GetPaymentBreakdownAsync(from, to);
            if (version != _loadVersion) return;

            _currentRange = range;
            _currentTopProducts = top.ToList();
            _currentCategories = cats.ToList();
            _currentPayments = pay.ToList();

            KpiGross.Text = FormattingUtilities.Money(range.GrossSales, App.StoreSettings);
            KpiProfit.Text = FormattingUtilities.Money(range.GrossProfit, App.StoreSettings);
            KpiTax.Text = FormattingUtilities.Money(range.TaxTotal, App.StoreSettings);
            KpiTxn.Text = range.TransactionCount.ToString();
            DailyTrendGrid.ItemsSource = range.Daily;
            TopProductsGrid.ItemsSource = _currentTopProducts;
            CategoryGrid.ItemsSource = _currentCategories;
            PaymentGrid.ItemsSource = _currentPayments;
        }
        catch (Exception ex)
        {
            LocalizedMessageBox.Show(ex.Message, "Unable to load reports", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private (DateTime from, DateTime to) GetRange()
    {
        if (_activePeriod == "today") return (DateTime.Today, DateTime.Today);
        if (_activePeriod == "week") return (DateTime.Today.AddDays(-6), DateTime.Today);
        if (_activePeriod == "month") return (DateTime.Today.AddDays(-29), DateTime.Today);
        var from = (FromDate.SelectedDate ?? DateTime.Today.AddDays(-30)).Date;
        var to = (ToDate.SelectedDate ?? DateTime.Today).Date;
        if (to < from) throw new InvalidOperationException("The To date cannot be earlier than the From date.");
        return (from, to);
    }

    private void Period_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            _activePeriod = tag;
            BtnToday.Style = (Style)FindResource(tag == "today" ? "PrimaryButton" : "OutlineButton");
            BtnWeek.Style = (Style)FindResource(tag == "week" ? "PrimaryButton" : "OutlineButton");
            BtnMonth.Style = (Style)FindResource(tag == "month" ? "PrimaryButton" : "OutlineButton");
            _ = RefreshAsync();
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var from = (FromDate.SelectedDate ?? DateTime.Today.AddDays(-30)).Date;
        var to = (ToDate.SelectedDate ?? DateTime.Today).Date;
        if (to < from)
        {
            LocalizedMessageBox.Show("The To date cannot be earlier than the From date.", "Invalid date range",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _activePeriod = "custom";
        BtnToday.Style = (Style)FindResource("OutlineButton");
        BtnWeek.Style = (Style)FindResource("OutlineButton");
        BtnMonth.Style = (Style)FindResource("OutlineButton");
        _ = RefreshAsync();
    }

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var requestedRange = GetRange();
            await RefreshAsync();
            if (_currentRange == null || _currentRange.From != requestedRange.from || _currentRange.To != requestedRange.to) return;
            await PageReportPrinter.PrintAsync(_hardware, BuildPrintReport(), "reports and dashboard page");
        }
        catch (Exception ex)
        {
            LocalizedMessageBox.Show(ex.Message, "Unable to print reports", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string BuildPrintReport()
    {
        var range = _currentRange ?? throw new InvalidOperationException("Report data is not loaded.");
        var builder = new StringBuilder();
        PageReportPrinter.AppendHeader(builder, "REPORTS & DASHBOARD",
            $"Period: {range.From:dd MMM yyyy} - {range.To:dd MMM yyyy}");
        PageReportPrinter.AppendMetric(builder, "Gross sales", PageReportPrinter.Money(range.GrossSales));
        PageReportPrinter.AppendMetric(builder, "Gross profit", PageReportPrinter.Money(range.GrossProfit));
        PageReportPrinter.AppendMetric(builder, "Tax collected", PageReportPrinter.Money(range.TaxTotal));
        PageReportPrinter.AppendMetric(builder, "Transactions", range.TransactionCount.ToString());
        PageReportPrinter.AppendMetric(builder, "Items sold", range.ItemCount.ToString("0.###"));

        PageReportPrinter.AppendSection(builder, "Top products");
        if (_currentTopProducts.Count == 0) PageReportPrinter.AppendWrapped(builder, "No products in this period.");
        foreach (var row in _currentTopProducts)
            PageReportPrinter.AppendEntry(builder, row.ProductName,
                $"Qty {row.QuantitySold:0.###} | Revenue {PageReportPrinter.Money(row.Revenue)}");

        PageReportPrinter.AppendSection(builder, "Sales by category");
        if (_currentCategories.Count == 0) PageReportPrinter.AppendWrapped(builder, "No category sales in this period.");
        foreach (var row in _currentCategories)
            PageReportPrinter.AppendEntry(builder, row.CategoryName,
                $"Qty {row.QuantitySold:0.###} | Revenue {PageReportPrinter.Money(row.Revenue)}");

        PageReportPrinter.AppendSection(builder, "Daily trend");
        if (range.Daily.Count == 0) PageReportPrinter.AppendWrapped(builder, "No daily sales in this period.");
        foreach (var row in range.Daily.OrderBy(item => item.Date))
            PageReportPrinter.AppendEntry(builder, row.Date.ToString("dd MMM yyyy"),
                $"Txns {row.TransactionCount} | Items {row.ItemCount:0.###}",
                $"Net {PageReportPrinter.Money(row.NetSales)} | Profit {PageReportPrinter.Money(row.GrossProfit)}");

        PageReportPrinter.AppendSection(builder, "Payment breakdown");
        if (_currentPayments.Count == 0) PageReportPrinter.AppendWrapped(builder, "No payments in this period.");
        foreach (var row in _currentPayments)
            PageReportPrinter.AppendEntry(builder, PageReportPrinter.PaymentMethodName(row.Method),
                $"Count {row.Count} | Total {PageReportPrinter.Money(row.Total)}");

        builder.AppendLine();
        PageReportPrinter.AppendWrapped(builder, $"Printed {DateTime.Now:dd MMM yyyy HH:mm}");
        return builder.ToString();
    }
}
