using System.Text;
using System.Windows;
using System.Windows.Controls;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Core.Utilities;
using PosApp.Wpf.Helpers;

namespace PosApp.Wpf.Views;

public partial class DashboardView : UserControl, IRefreshable
{
    private readonly IReportService _reports;
    private readonly IHardwareService _hardware;
    private readonly IStoreService _stores;
    private bool _loading;
    private DateRangeReport? _range;
    private DailySalesReport? _today;
    private IReadOnlyList<TopProductRow> _topProducts = Array.Empty<TopProductRow>();
    private IReadOnlyList<SalesByHourRow> _hourly = Array.Empty<SalesByHourRow>();
    private IReadOnlyList<PaymentBreakdownRow> _payments = Array.Empty<PaymentBreakdownRow>();
    private IReadOnlyList<StorePerformanceRow> _storePerformance = Array.Empty<StorePerformanceRow>();
    private bool _storeFilterReady;
    private DateTime _selectedFrom;
    private DateTime _selectedTo;

    public DashboardView(IReportService reports, IHardwareService hardware, IStoreService stores)
    {
        InitializeComponent();
        _reports = reports;
        _hardware = hardware;
        _stores = stores;

        var today = DateTime.Today;
        _selectedFrom = new DateTime(today.Year, today.Month, 1);
        _selectedTo = _selectedFrom.AddMonths(1).AddDays(-1);
        FromDate.SelectedDate = _selectedFrom;
        ToDate.SelectedDate = _selectedTo;
    }

    public async Task RefreshAsync() => await LoadAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void ApplyDateFilter_Click(object sender, RoutedEventArgs e)
    {
        var from = (FromDate.SelectedDate ?? _selectedFrom).Date;
        var to = (ToDate.SelectedDate ?? _selectedTo).Date;
        if (to < from)
        {
            LocalizedMessageBox.Show(
                "The To date cannot be earlier than the From date.",
                "Invalid date range",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _selectedFrom = from;
        _selectedTo = to;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        IsEnabled = false;
        try
        {
            await EnsureStoreFilterAsync();
            var from = _selectedFrom.Date;
            var to = _selectedTo.Date;
            var storeId = SelectedStoreId();
            // ReportService owns one EF Core DbContext. Keep these reads
            // sequential because a DbContext cannot execute concurrent queries.
            var range = await _reports.GetRangeReportAsync(from, to, storeId);
            var today = await _reports.GetDailyReportAsync(DateTime.Today, storeId);
            var top = await _reports.GetTopProductsAsync(from, to, 10, storeId);
            var hourly = await _reports.GetSalesByHourAsync(from, to, storeId);
            var payments = await _reports.GetPaymentBreakdownAsync(from, to, storeId);
            var performance = await _reports.GetStorePerformanceAsync(from, to, storeId);

            _range = range;
            _today = today;
            _topProducts = top.ToList();
            _hourly = hourly.ToList();
            _payments = payments.ToList();
            _storePerformance = performance.ToList();

            PeriodText.Text = $"{from:dd MMM yyyy} – {to:dd MMM yyyy}";
            SalesText.Text = FormattingUtilities.Money(range.NetSales, App.StoreSettings);
            TransactionsText.Text = range.TransactionCount.ToString();
            ProfitText.Text = FormattingUtilities.Money(range.GrossProfit, App.StoreSettings);
            TodayText.Text = FormattingUtilities.Money(today.NetSales, App.StoreSettings);
            DailyGrid.ItemsSource = range.Daily.OrderByDescending(row => row.Date).ToList();
            TopProductsGrid.ItemsSource = _topProducts;
            PaymentGrid.ItemsSource = _payments;
            HourlyGrid.ItemsSource = _hourly
                .Select(row => new HourlyDashboardRow(row.Hour, row.TransactionCount, row.Revenue))
                .ToList();
            StorePerformanceGrid.ItemsSource = _storePerformance;
        }
        catch (Exception ex)
        {
            LocalizedMessageBox.Show(ex.Message, "Dashboard", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
            _loading = false;
        }
    }

    private async Task EnsureStoreFilterAsync()
    {
        if (_storeFilterReady) return;
        var stores = await _stores.GetStoresAsync(false);
        var options = new List<StoreFilterOption>();
        if (App.CurrentUser?.Role == PosApp.Core.Entities.UserRole.Admin)
            options.Add(new StoreFilterOption(0, FindResource("Transfer_AllStores")?.ToString() ?? "All stores"));
        options.AddRange(stores.Select(x => new StoreFilterOption(x.Id, x.Name)));
        StoreFilter.ItemsSource = options;
        StoreFilter.SelectedValue = App.CurrentUser?.Role == PosApp.Core.Entities.UserRole.Admin ? 0 : App.CurrentStore?.Id ?? 1;
        StoreFilter.IsEnabled = App.CurrentUser?.Role == PosApp.Core.Entities.UserRole.Admin;
        _storeFilterReady = true;
    }

    private int? SelectedStoreId()
    {
        var value = StoreFilter.SelectedValue is int id ? id : App.CurrentStore?.Id ?? 1;
        return value == 0 ? null : value;
    }

    private async void StoreFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_storeFilterReady) await LoadAsync();
    }

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
        if (_range == null || _today == null) return;
        await PageReportPrinter.PrintAsync(_hardware, BuildPrintReport(), "management dashboard page");
    }

    private string BuildPrintReport()
    {
        var range = _range ?? throw new InvalidOperationException("Dashboard data is not loaded.");
        var today = _today ?? throw new InvalidOperationException("Today's dashboard data is not loaded.");
        var builder = new StringBuilder();
        PageReportPrinter.AppendHeader(builder, "MANAGEMENT DASHBOARD",
            $"Period: {range.From:dd MMM yyyy} - {range.To:dd MMM yyyy}");
        PageReportPrinter.AppendMetric(builder, "Sales", PageReportPrinter.Money(range.NetSales));
        PageReportPrinter.AppendMetric(builder, "Transactions", range.TransactionCount.ToString());
        PageReportPrinter.AppendMetric(builder, "Gross profit", PageReportPrinter.Money(range.GrossProfit));
        PageReportPrinter.AppendMetric(builder, "Today", PageReportPrinter.Money(today.NetSales));

        PageReportPrinter.AppendSection(builder, "Daily sales");
        if (range.Daily.Count == 0) PageReportPrinter.AppendWrapped(builder, "No sales in the selected period.");
        foreach (var row in range.Daily.OrderBy(item => item.Date))
            PageReportPrinter.AppendEntry(builder, row.Date.ToString("dd MMM yyyy"),
                $"Txns {row.TransactionCount} | Net {PageReportPrinter.Money(row.NetSales)}",
                $"Gross profit {PageReportPrinter.Money(row.GrossProfit)}");

        PageReportPrinter.AppendSection(builder, "Store performance");
        if (_storePerformance.Count == 0) PageReportPrinter.AppendWrapped(builder, "No store sales in the selected period.");
        foreach (var row in _storePerformance)
            PageReportPrinter.AppendEntry(builder, $"{row.StoreCode} - {row.StoreName}",
                $"Transactions {row.TransactionCount} | Net {PageReportPrinter.Money(row.NetSales)}",
                $"Gross profit {PageReportPrinter.Money(row.GrossProfit)}");

        PageReportPrinter.AppendSection(builder, "Payment breakdown");
        if (_payments.Count == 0) PageReportPrinter.AppendWrapped(builder, "No payments in the selected period.");
        foreach (var row in _payments)
            PageReportPrinter.AppendEntry(builder, PageReportPrinter.PaymentMethodName(row.Method),
                $"Transactions {row.Count} | Total {PageReportPrinter.Money(row.Total)}");

        PageReportPrinter.AppendSection(builder, "Top products");
        if (_topProducts.Count == 0) PageReportPrinter.AppendWrapped(builder, "No products sold in the selected period.");
        foreach (var row in _topProducts)
            PageReportPrinter.AppendEntry(builder, row.ProductName,
                $"Qty {row.QuantitySold:0.###} | Revenue {PageReportPrinter.Money(row.Revenue)}");

        PageReportPrinter.AppendSection(builder, "Sales by hour");
        if (_hourly.Count == 0) PageReportPrinter.AppendWrapped(builder, "No hourly sales in the selected period.");
        foreach (var row in _hourly)
            PageReportPrinter.AppendEntry(builder, $"{row.Hour:00}:00",
                $"Transactions {row.TransactionCount} | Revenue {PageReportPrinter.Money(row.Revenue)}");

        builder.AppendLine();
        PageReportPrinter.AppendWrapped(builder, $"Printed {DateTime.Now:dd MMM yyyy HH:mm}");
        return builder.ToString();
    }
}

public sealed record HourlyDashboardRow(int Hour, int TransactionCount, decimal Revenue)
{
    public string HourLabel => $"{Hour:00}:00";
}
