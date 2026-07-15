using System.Windows;
using System.Windows.Controls;
using PosApp.Core.Interfaces;

namespace PosApp.Wpf.Views;

public partial class ReportsView : UserControl, IRefreshable
{
    private readonly IReportService _reports;
    private string _activePeriod = "today";

    public ReportsView(IReportService reports)
    {
        InitializeComponent();
        _reports = reports;
        FromDate.SelectedDate = DateTime.Today.AddDays(-30);
        ToDate.SelectedDate = DateTime.Today.AddDays(1);
        Loaded += async (_, _) => await LoadAsync();
    }

    public async void Refresh()
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var (from, to) = GetRange();
        var range = await _reports.GetRangeReportAsync(from, to);
        KpiGross.Text = $"৳ {range.GrossSales:0.00}";
        KpiProfit.Text = $"৳ {range.GrossProfit:0.00}";
        KpiTax.Text = $"৳ {range.TaxTotal:0.00}";
        KpiTxn.Text = range.TransactionCount.ToString();

        DailyTrendGrid.ItemsSource = range.Daily;

        var top = await _reports.GetTopProductsAsync(from, to, 20);
        TopProductsGrid.ItemsSource = top.ToList();

        var cats = await _reports.GetSalesByCategoryAsync(from, to);
        CategoryGrid.ItemsSource = cats.ToList();

        var pay = await _reports.GetPaymentBreakdownAsync(from, to);
        PaymentGrid.ItemsSource = pay.ToList();
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
