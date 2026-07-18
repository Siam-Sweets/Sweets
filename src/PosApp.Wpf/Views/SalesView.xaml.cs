using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Utilities;
using PosApp.Wpf.Helpers;

namespace PosApp.Wpf.Views;

public partial class SalesView : UserControl, IRefreshable
{
    private readonly ISaleService _sales;
    private readonly IHardwareService _hardware;
    private readonly Data.AppDbContext _db;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private List<Sale> _all = new();
    private int _loadVersion;

    public SalesView(ISaleService sales, IHardwareService hardware, Data.AppDbContext db)
    {
        InitializeComponent();
        _sales = sales;
        _hardware = hardware;
        _db = db;
        FromDate.SelectedDate = DateTime.Today.AddDays(-7);
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
        var from = (FromDate.SelectedDate ?? DateTime.Today.AddDays(-7)).Date;
        var to = (ToDate.SelectedDate ?? DateTime.Today).Date;

        if (to < from)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("The To date cannot be earlier than the From date.",
                "Invalid date range", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SaleStatus? statusFilter = null;
        if (StatusFilter.SelectedItem is ComboBoxItem ci && ci.Tag is string s && Enum.TryParse<SaleStatus>(s, out var st))
            statusFilter = st;

        await _loadGate.WaitAsync();
        try
        {
            if (version != _loadVersion) return;
            var sales = (await _sales.GetSalesAsync(from, to, statusFilter)).ToList();
            if (version != _loadVersion) return;

            _all = sales;
            SalesGrid.ItemsSource = _all;
            var financial = _all.Where(x => x.Status is SaleStatus.Completed or SaleStatus.Refunded).ToList();
            TxnCountText.Text = financial.Count(x => x.Status == SaleStatus.Completed).ToString();
            GrossText.Text = FormattingUtilities.Money(financial.Sum(x => x.Subtotal), App.StoreSettings);
            DiscountText.Text = FormattingUtilities.Money(financial.Sum(x => x.DiscountTotal), App.StoreSettings);
            TaxText.Text = FormattingUtilities.Money(financial.Sum(x => x.TaxTotal), App.StoreSettings);
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to load sales", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private void Filter_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private async void PrintHistory_Click(object sender, RoutedEventArgs e)
    {
        var from = (FromDate.SelectedDate ?? DateTime.Today.AddDays(-7)).Date;
        var to = (ToDate.SelectedDate ?? DateTime.Today).Date;
        if (to < from)
        {
            LocalizedMessageBox.Show("The To date cannot be earlier than the From date.", "Invalid date range",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await RefreshAsync();
        await PageReportPrinter.PrintAsync(_hardware, BuildHistoryPrintReport(), "sales history page");
    }

    private string BuildHistoryPrintReport()
    {
        var from = (FromDate.SelectedDate ?? DateTime.Today.AddDays(-7)).Date;
        var to = (ToDate.SelectedDate ?? DateTime.Today).Date;
        var status = StatusFilter.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString() ?? "All"
            : "All";
        var financial = _all.Where(sale => sale.Status is SaleStatus.Completed or SaleStatus.Refunded).ToList();
        var builder = new StringBuilder();
        PageReportPrinter.AppendHeader(builder, "SALES HISTORY",
            $"Period: {from:dd MMM yyyy} - {to:dd MMM yyyy} | Status: {status}");
        PageReportPrinter.AppendMetric(builder, "Completed transactions",
            financial.Count(sale => sale.Status == SaleStatus.Completed).ToString());
        PageReportPrinter.AppendMetric(builder, "Gross sales", PageReportPrinter.Money(financial.Sum(sale => sale.Subtotal)));
        PageReportPrinter.AppendMetric(builder, "Discount", PageReportPrinter.Money(financial.Sum(sale => sale.DiscountTotal)));
        PageReportPrinter.AppendMetric(builder, "Tax", PageReportPrinter.Money(financial.Sum(sale => sale.TaxTotal)));

        PageReportPrinter.AppendSection(builder, "Transactions");
        if (_all.Count == 0) PageReportPrinter.AppendWrapped(builder, "No sales match the current filters.");
        foreach (var sale in _all)
        {
            var localDate = DateTimeUtilities.ToLocal(sale.SaleDate);
            var quantity = sale.Items.Sum(line => line.Quantity);
            PageReportPrinter.AppendEntry(builder,
                $"{localDate:dd MMM yyyy HH:mm} | {sale.ReceiptNumber}",
                $"Customer: {sale.Customer?.Name ?? "Walk-in"}",
                $"Cashier: {sale.User?.FullName ?? sale.UserId.ToString()}",
                $"Items {quantity:0.###} | Total {PageReportPrinter.Money(sale.Total)} | {sale.Status}");
        }

        builder.AppendLine();
        PageReportPrinter.AppendWrapped(builder, $"Printed {DateTime.Now:dd MMM yyyy HH:mm}");
        return builder.ToString();
    }


    private async void SalesGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SalesGrid.SelectedItem is Sale s) await ViewSaleAsync(s);
    }

    private async void View_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Sale s) await ViewSaleAsync(s);
    }

    private async Task ViewSaleAsync(Sale sale)
    {
        try
        {
            var full = await _db.Sales.AsNoTracking()
                .Include(x => x.Items)
                .Include(x => x.Payments)
                .Include(x => x.Customer)
                .Include(x => x.User)
                .FirstOrDefaultAsync(x => x.Id == sale.Id)
                ?? throw new InvalidOperationException("Sale not found.");

            new SaleDetailDialog(full) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (Exception ex)
        {
            App.LogError("Load sale details", ex);
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.GetBaseException().Message,
                "Unable to load sale", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Sale sale }) return;

        try
        {
            var full = await _db.Sales.AsNoTracking()
                .Include(x => x.Items)
                .Include(x => x.Payments)
                .Include(x => x.User)
                .FirstOrDefaultAsync(x => x.Id == sale.Id)
                ?? throw new InvalidOperationException("Sale not found.");

            var ok = await _hardware.PrintReceiptAsync(full);
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(
                ok ? "Receipt sent to the printer." : "Printer not available. Sale remains saved.",
                "Print", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            App.LogError("Print sale receipt", ex);
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.GetBaseException().Message,
                "Print failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Refund_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Sale s)
        {
            if (!s.CanRefund)
            {
                PosApp.Wpf.Helpers.LocalizedMessageBox.Show(
                    s.Status != SaleStatus.Completed
                        ? "Only completed sales can be refunded."
                        : "This sale has already been fully refunded.",
                    "Custom Refund", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                var original = await _db.Sales.AsNoTracking()
                    .Include(sale => sale.Items)
                    .Include(sale => sale.Payments)
                    .FirstOrDefaultAsync(sale => sale.Id == s.Id)
                    ?? throw new InvalidOperationException("Sale not found.");
                var priorRefunds = await _db.Sales.AsNoTracking()
                    .Include(sale => sale.Items)
                    .Where(sale => sale.RefundedSaleId == s.Id && sale.Status == SaleStatus.Refunded)
                    .OrderBy(sale => sale.SaleDate)
                    .ToListAsync();
                var dialog = new CustomRefundDialog(original, priorRefunds) { Owner = Window.GetWindow(this) };
                if (dialog.ShowDialog() != true || dialog.Draft == null) return;

                var refund = await _sales.RefundSaleAsync(dialog.Draft);
                PosApp.Wpf.Helpers.LocalizedMessageBox.Show(
                    $"Refund processed. Receipt: {refund.ReceiptNumber}\n" +
                    $"Amount: {FormattingUtilities.Money(Math.Abs(refund.Total), App.StoreSettings)}",
                    "Custom Refund", MessageBoxButton.OK, MessageBoxImage.Information);
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Refund failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void Void_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Sale s)
        {
            if (s.Status != SaleStatus.Completed) return;
            if (App.StoreSettings.ConfirmBeforeVoidingOrder)
            {
                var confirm = PosApp.Wpf.Helpers.LocalizedMessageBox.Show($"Void sale {s.ReceiptNumber}? Stock will be returned.",
                    "Confirm Void", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;
            }
            try
            {
                await _sales.VoidSaleAsync(s.Id, App.CurrentUser?.Id ?? 0);
                PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Sale voided.", "Void", MessageBoxButton.OK, MessageBoxImage.Information);
                _ = RefreshAsync();
            }
            catch (Exception ex)
            {
                PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Void failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV files|*.csv",
            FileName = $"sales_{DateTime.Today:yyyyMMdd}.csv"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Receipt,Date,Customer,Cashier,Items,Subtotal,Discount,Tax,Total,Status");
            foreach (var sale in _all)
            {
                var localDate = DateTimeUtilities.ToLocal(sale.SaleDate);
                var values = new[]
                {
                    FormattingUtilities.CsvField(sale.ReceiptNumber),
                    FormattingUtilities.CsvField(localDate.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)),
                    FormattingUtilities.CsvField(sale.Customer?.Name),
                    FormattingUtilities.CsvField(sale.User?.FullName),
                    sale.Items.Sum(item => item.Quantity).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    FormattingUtilities.CsvDecimal(sale.Subtotal),
                    FormattingUtilities.CsvDecimal(sale.DiscountTotal),
                    FormattingUtilities.CsvDecimal(sale.TaxTotal),
                    FormattingUtilities.CsvDecimal(sale.Total),
                    FormattingUtilities.CsvField(sale.Status.ToString())
                };
                sb.AppendLine(string.Join(',', values));
            }
            File.WriteAllText(dlg.FileName, sb.ToString());
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show($"Exported {_all.Count} sales to:\n{dlg.FileName}", "Export",
                MessageBoxButton.OK, MessageBoxImage.Information);
            try { Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true }); } catch { }
        }
        catch (Exception ex)
        {
            App.LogError("Sales CSV export", ex);
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Export failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

public class SaleDetailDialog : Window
{
    public SaleDetailDialog(Sale sale)
    {
        Title = $"Receipt {sale.ReceiptNumber}";
        Width = 660; Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BackgroundBrush");

        var panel = new StackPanel { Margin = new Thickness(24) };

        var header = new TextBlock { Text = $"Receipt {sale.ReceiptNumber}", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(header);

        var info = new TextBlock
        {
            Text = $"Date: {DateTimeUtilities.ToLocal(sale.SaleDate):yyyy-MM-dd HH:mm}\nCustomer: {sale.Customer?.Name ?? "Walk-in"}\nCashier: {sale.User?.FullName ?? sale.UserId.ToString()}\nStatus: {sale.Status}",
            FontSize = 13,
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextMutedBrush"),
            Margin = new Thickness(0, 0, 0, 16)
        };
        panel.Children.Add(info);

        var dg = new DataGrid
        {
            ItemsSource = sale.Items.ToList(),
            AutoGenerateColumns = false,
            IsReadOnly = true,
            Height = 260,
            Margin = new Thickness(0, 0, 0, 16)
        };
        dg.Columns.Add(new DataGridTextColumn { Header = "Product", Binding = new System.Windows.Data.Binding("ProductName"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        dg.Columns.Add(new DataGridTextColumn { Header = "Qty", Binding = new System.Windows.Data.Binding("QuantityDisplay"), Width = 90 });
        dg.Columns.Add(new DataGridTextColumn { Header = "Price / unit", Binding = new System.Windows.Data.Binding("UnitPrice") { Converter = new PosApp.Wpf.Converters.MoneyConverter() }, Width = 100 });
        dg.Columns.Add(new DataGridTextColumn { Header = "Disc", Binding = new System.Windows.Data.Binding("DiscountAmount") { Converter = new PosApp.Wpf.Converters.MoneyConverter() }, Width = 80 });
        dg.Columns.Add(new DataGridTextColumn { Header = "Total", Binding = new System.Windows.Data.Binding("LineTotal") { Converter = new PosApp.Wpf.Converters.MoneyConverter() }, Width = 100 });
        panel.Children.Add(dg);

        var totals = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        totals.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        totals.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        for (int i = 0; i < 7; i++) totals.RowDefinitions.Add(new RowDefinition());

        AddRow(totals, 0, "Subtotal", FormattingUtilities.Money(sale.Subtotal, App.StoreSettings));
        AddRow(totals, 1, "Discount", FormattingUtilities.Money(-sale.DiscountTotal, App.StoreSettings));
        AddRow(totals, 2, "Tax", FormattingUtilities.Money(sale.TaxTotal, App.StoreSettings));
        AddRow(totals, 3, "TOTAL", FormattingUtilities.Money(sale.Total, App.StoreSettings), bold: true);
        var paymentSummary = sale.Payments.Count == 0
            ? "-"
            : string.Join(" + ", sale.Payments.Select(payment =>
                $"{PaymentName(payment.Method)} {FormattingUtilities.Money(payment.Amount, App.StoreSettings)}"));
        AddRow(totals, 4, "Payments applied", paymentSummary);
        AddRow(totals, 5, "Received", FormattingUtilities.Money(sale.AmountPaid, App.StoreSettings));
        AddRow(totals, 6, "Change", FormattingUtilities.Money(sale.Change, App.StoreSettings));

        panel.Children.Add(totals);

        var closeBtn = new Button
        {
            Content = "Close",
            Style = (Style)System.Windows.Application.Current.FindResource("PrimaryButton"),
            Padding = new Thickness(0, 10, 0, 10),
            Margin = new Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Width = 120
        };
        closeBtn.Click += (_, _) => Close();
        panel.Children.Add(closeBtn);

        Content = panel;
    }

    private static void AddRow(Grid grid, int row, string label, string value, bool bold = false)
    {
        var lbl = new TextBlock
        {
            Text = label,
            FontSize = bold ? 16 : 13,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        var val = new TextBlock
        {
            Text = value,
            FontSize = bold ? 16 : 13,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(val, row); Grid.SetColumn(val, 1);
        grid.Children.Add(val);
    }

    private static string PaymentName(PaymentMethod method) => method switch
    {
        PaymentMethod.MobileWallet => "Mobile wallet",
        PaymentMethod.BankTransfer => "Bank transfer",
        PaymentMethod.StoreCredit => "Store credit",
        _ => method.ToString()
    };
}
