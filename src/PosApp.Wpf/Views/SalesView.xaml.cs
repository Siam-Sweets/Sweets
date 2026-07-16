using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;

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
        var from = FromDate.SelectedDate ?? DateTime.Today.AddDays(-7);
        var to = ToDate.SelectedDate ?? DateTime.Today.AddDays(1);

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
            TxnCountText.Text = _all.Count.ToString();
            GrossText.Text = $"৳ {_all.Sum(x => x.Subtotal):0.00}";
            DiscountText.Text = $"৳ {_all.Sum(x => x.DiscountTotal):0.00}";
            TaxText.Text = $"৳ {_all.Sum(x => x.TaxTotal):0.00}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to load sales", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private void Filter_Click(object sender, RoutedEventArgs e) => Refresh();

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
        // Load full sale with items
        var full = await _db.Sales.AsNoTracking()
            .Include(x => x.Items)
            .Include(x => x.Payments)
            .Include(x => x.Customer)
            .Include(x => x.User)
            .FirstOrDefaultAsync(x => x.Id == sale.Id);
        if (full == null) return;
        var dlg = new SaleDetailDialog(full) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Sale s)
        {
            var full = await _db.Sales.AsNoTracking()
                .Include(x => x.Items)
                .Include(x => x.Payments)
                .Include(x => x.User)
                .FirstOrDefaultAsync(x => x.Id == s.Id);
            if (full != null)
            {
                var ok = await _hardware.PrintReceiptAsync(full);
                MessageBox.Show(ok ? "Receipt sent to the printer." : "Printer not available. Sale remains saved.",
                    "Print", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
        }
    }

    private async void Refund_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Sale s)
        {
            if (s.Status != SaleStatus.Completed)
            {
                MessageBox.Show("Only completed sales can be refunded.", "Refund", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var confirm = MessageBox.Show($"Refund sale {s.ReceiptNumber} for ৳ {s.Total:0.00}?",
                "Confirm Refund", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            try
            {
                await _sales.RefundSaleAsync(s.Id, App.CurrentUser?.Id ?? 0);
                MessageBox.Show("Refund processed.", "Refund", MessageBoxButton.OK, MessageBoxImage.Information);
                Refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Refund failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void Void_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Sale s)
        {
            if (s.Status != SaleStatus.Completed) return;
            var confirm = MessageBox.Show($"Void sale {s.ReceiptNumber}? Stock will be returned.",
                "Confirm Void", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            try
            {
                await _sales.VoidSaleAsync(s.Id, App.CurrentUser?.Id ?? 0);
                MessageBox.Show("Sale voided.", "Void", MessageBoxButton.OK, MessageBoxImage.Information);
                Refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Void failed", MessageBoxButton.OK, MessageBoxImage.Error);
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
        var sb = new StringBuilder();
        sb.AppendLine("Receipt,Date,Customer,Cashier,Items,Subtotal,Discount,Tax,Total,Status");
        foreach (var s in _all)
        {
            sb.AppendLine(string.Join(",",
                s.ReceiptNumber,
                s.SaleDate.ToString("yyyy-MM-dd HH:mm"),
                $"\"{s.Customer?.Name ?? ""}\"",
                $"\"{s.User?.FullName ?? ""}\"",
                s.Items.Count.ToString(),
                s.Subtotal.ToString("0.00"),
                s.DiscountTotal.ToString("0.00"),
                s.TaxTotal.ToString("0.00"),
                s.Total.ToString("0.00"),
                s.Status));
        }
        File.WriteAllText(dlg.FileName, sb.ToString());
        MessageBox.Show($"Exported {_all.Count} sales to:\n{dlg.FileName}", "Export",
            MessageBoxButton.OK, MessageBoxImage.Information);
        try { Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true }); } catch { }
    }
}

public class SaleDetailDialog : Window
{
    public SaleDetailDialog(Sale sale)
    {
        Title = $"Receipt {sale.ReceiptNumber}";
        Width = 600; Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BackgroundBrush");

        var panel = new StackPanel { Margin = new Thickness(24) };

        var header = new TextBlock { Text = $"Receipt {sale.ReceiptNumber}", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(header);

        var info = new TextBlock
        {
            Text = $"Date: {sale.SaleDate:yyyy-MM-dd HH:mm}\nCustomer: {sale.Customer?.Name ?? "Walk-in"}\nCashier: {sale.User?.FullName ?? sale.UserId.ToString()}\nStatus: {sale.Status}",
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
            Height = 280,
            Margin = new Thickness(0, 0, 0, 16)
        };
        dg.Columns.Add(new DataGridTextColumn { Header = "Product", Binding = new System.Windows.Data.Binding("ProductName"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        dg.Columns.Add(new DataGridTextColumn { Header = "Qty", Binding = new System.Windows.Data.Binding("Quantity") { StringFormat = "0.###" }, Width = 70 });
        dg.Columns.Add(new DataGridTextColumn { Header = "Price", Binding = new System.Windows.Data.Binding("UnitPrice") { StringFormat = "৳ {0:0.00}" }, Width = 80 });
        dg.Columns.Add(new DataGridTextColumn { Header = "Disc", Binding = new System.Windows.Data.Binding("DiscountAmount") { StringFormat = "৳ {0:0.00}" }, Width = 80 });
        dg.Columns.Add(new DataGridTextColumn { Header = "Total", Binding = new System.Windows.Data.Binding("LineTotal") { StringFormat = "৳ {0:0.00}" }, Width = 100 });
        panel.Children.Add(dg);

        var totals = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        totals.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        totals.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        for (int i = 0; i < 4; i++) totals.RowDefinitions.Add(new RowDefinition());

        AddRow(totals, 0, "Subtotal", $"৳ {sale.Subtotal:0.00}");
        AddRow(totals, 1, "Discount", $"- ৳ {sale.DiscountTotal:0.00}");
        AddRow(totals, 2, "Tax", $"৳ {sale.TaxTotal:0.00}");
        AddRow(totals, 3, "TOTAL", $"৳ {sale.Total:0.00}", bold: true);

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
}
