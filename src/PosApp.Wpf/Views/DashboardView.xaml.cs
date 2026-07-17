using System.Windows;
using System.Windows.Controls;
using PosApp.Core.Interfaces;
using PosApp.Core.Utilities;

namespace PosApp.Wpf.Views;

public partial class DashboardView : UserControl, IRefreshable
{
    private readonly IReportService _reports;
    private bool _loading;

    public DashboardView(IReportService reports)
    {
        InitializeComponent();
        _reports = reports;
    }

    public async Task RefreshAsync() => await LoadAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        IsEnabled = false;
        try
        {
            var now = DateTime.Now;
            var from = new DateTime(now.Year, now.Month, 1);
            var to = from.AddMonths(1).AddTicks(-1);
            // ReportService owns one EF Core DbContext. Keep these reads
            // sequential because a DbContext cannot execute concurrent queries.
            var range = await _reports.GetRangeReportAsync(from, to);
            var today = await _reports.GetDailyReportAsync(now.Date);
            var top = await _reports.GetTopProductsAsync(from, to, 10);
            var hourly = await _reports.GetSalesByHourAsync(now.Date);
            var payments = await _reports.GetPaymentBreakdownAsync(from, to);
            PeriodText.Text = $"{from:dd MMM yyyy} – {to:dd MMM yyyy}";
            SalesText.Text = FormattingUtilities.Money(range.NetSales, App.StoreSettings);
            TransactionsText.Text = range.TransactionCount.ToString();
            ProfitText.Text = FormattingUtilities.Money(range.GrossProfit, App.StoreSettings);
            TodayText.Text = FormattingUtilities.Money(today.NetSales, App.StoreSettings);
            DailyGrid.ItemsSource = range.Daily.OrderByDescending(row => row.Date).ToList();
            TopProductsGrid.ItemsSource = top;
            PaymentGrid.ItemsSource = payments;
            HourlyGrid.ItemsSource = hourly
                .Select(row => new HourlyDashboardRow(row.Hour, row.TransactionCount, row.Revenue))
                .ToList();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Dashboard", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
            _loading = false;
        }
    }
}

public sealed record HourlyDashboardRow(int Hour, int TransactionCount, decimal Revenue)
{
    public string HourLabel => $"{Hour:00}:00";
}
