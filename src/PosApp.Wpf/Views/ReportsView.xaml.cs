using System.Windows;
using System.Windows.Controls;
using PosApp.Core.Interfaces;

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
        ToDate.SelectedDate = DateTime.Today.AddDays(1);
    }

    public async void Refresh()
    {
        IsEnabled = false;
        try { await LoadAsync(); }
        finally { IsEnabled = true; }
    }

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        var (from, to) = GetRange();
        await _loadGate.WaitAsync();
        try
        {
            if (version != _loadVersion) return;

            var range = await _reports.GetRangeReportAsync(from, to);
            var top = await _reports.GetTopProductsAsync(from, to, 20);
            var cats = await _reports.GetSalesByCategoryAsync(from, to);
            var pay = await _reports.GetPaymentBreakdownAsync(from, to);
            if (version != _loadVersion) return;

            KpiGross.Text = $"৳ {range.GrossSales:0.00}";
            KpiProfit.Text = $"৳ {range.GrossProfit:0.00}";
            KpiTax.Text = $"৳ {range.TaxTotal:0.00}";
            KpiTxn.Text = range.TransactionCount.ToString();
            DailyTrendGrid.ItemsSource = range.Daily;
            TopProductsGrid.ItemsSource = top.ToList();
            CategoryGrid.ItemsSource = cats.ToList();
            PaymentGrid.ItemsSource = pay.ToList();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to load reports", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private (DateTime from, DateTime to) GetRange()
    {
        if (_activePeriod == "today") return (DateTime.Today, DateTime.Today.AddDays(1));
        if (_activePeriod == "week") return (DateTime.Today.AddDays(-6), DateTime.Today.AddDays(1));
        if (_activePeriod == "month") return (DateTime.Today.AddDays(-29), DateTime.Today.AddDays(1));
        return (FromDate.SelectedDate ?? DateTime.Today.AddDays(-30),
                ToDate.SelectedDate ?? DateTime.Today.AddDays(1));
    }

    private void Period_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            _activePeriod = tag;
            BtnToday.Style = (Style)FindResource(tag == "today" ? "PrimaryButton" : "OutlineButton");
            BtnWeek.Style = (Style)FindResource(tag == "week" ? "PrimaryButton" : "OutlineButton");
            BtnMonth.Style = (Style)FindResource(tag == "month" ? "PrimaryButton" : "OutlineButton");
            Refresh();
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        _activePeriod = "custom";
        BtnToday.Style = (Style)FindResource("OutlineButton");
        BtnWeek.Style = (Style)FindResource("OutlineButton");
        BtnMonth.Style = (Style)FindResource("OutlineButton");
        Refresh();
    }
}
