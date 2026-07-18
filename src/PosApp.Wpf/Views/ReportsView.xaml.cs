using System.Windows;
using System.Windows.Controls;
using PosApp.Core.Interfaces;
using PosApp.Core.Utilities;

namespace PosApp.Wpf.Views;

public partial class ReportsView : UserControl, IRefreshable
{
    private readonly IReportService _reports;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private string _activePeriod = "today";
    private int _loadVersion;

    public ReportsView(IReportService reports)
    {
        InitializeComponent();
        _reports = reports;
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

            KpiGross.Text = FormattingUtilities.Money(range.GrossSales, App.StoreSettings);
            KpiProfit.Text = FormattingUtilities.Money(range.GrossProfit, App.StoreSettings);
            KpiTax.Text = FormattingUtilities.Money(range.TaxTotal, App.StoreSettings);
            KpiTxn.Text = range.TransactionCount.ToString();
            DailyTrendGrid.ItemsSource = range.Daily;
            TopProductsGrid.ItemsSource = top.ToList();
            CategoryGrid.ItemsSource = cats.ToList();
            PaymentGrid.ItemsSource = pay.ToList();
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to load reports", MessageBoxButton.OK, MessageBoxImage.Error);
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
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("The To date cannot be earlier than the From date.", "Invalid date range",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _activePeriod = "custom";
        BtnToday.Style = (Style)FindResource("OutlineButton");
        BtnWeek.Style = (Style)FindResource("OutlineButton");
        BtnMonth.Style = (Style)FindResource("OutlineButton");
        _ = RefreshAsync();
    }
}
