using System.Windows;
using System.Windows.Controls;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using PosApp.Core.Utilities;

namespace PosApp.Wpf.Views;

public partial class InventoryView : UserControl, IRefreshable
{
    private readonly IInventoryService _inventory;
    private readonly Data.AppDbContext _db;

    public InventoryView(IInventoryService inventory, Data.AppDbContext db)
    {
        InitializeComponent();
        _inventory = inventory;
        _db = db;
    }

    public async Task RefreshAsync()
    {
        IsEnabled = false;
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to load inventory", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async Task LoadAsync()
    {
        var products = await _inventory.SearchProductsAsync(null);
        StockGrid.ItemsSource = products.Where(p => p.StockQuantity.HasValue).ToList();

        TotalProductsText.Text = products.Count.ToString();
        var low = await _inventory.GetLowStockProductsAsync();
        LowStockText.Text = low.Count.ToString();
        StockValueText.Text = FormattingUtilities.Money(
            products.Where(p => p.StockQuantity.HasValue).Sum(p => p.StockQuantity!.Value * p.CostPrice),
            App.StoreSettings);

        var history = await _db.StockTransactions
            .AsNoTracking()
            .Include(t => t.Product)
            .OrderByDescending(t => t.CreatedAt)
            .Take(50)
            .ToListAsync();
        HistoryGrid.ItemsSource = history;
    }

    private async void Adjust_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Product p)
        {
            var dlg = new StockAdjustDialog(p, _inventory, App.CurrentUser?.Id ?? 0) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true)
            {
                _ = RefreshAsync();
            }
        }
    }

    private async void InventoryCount_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var products = (await _inventory.SearchProductsAsync(null))
                .Where(product => product.StockQuantity.HasValue)
                .ToList();
            var dialog = new InventoryCountDialog(
                _inventory, products, App.CurrentUser?.Id)
            {
                Owner = Window.GetWindow(this)
            };
            if (dialog.ShowDialog() == true) _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to start inventory count", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

public class StockAdjustDialog : Window
{
    public StockAdjustDialog(Product product, IInventoryService svc, int userId)
    {
        Title = $"Adjust Stock - {product.Name}";
        Width = 460; Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BackgroundBrush");

        var panel = new StackPanel { Margin = new Thickness(24) };

        panel.Children.Add(new TextBlock { Text = $"Current stock: {product.StockQuantity:0.###}", FontSize = 14, Margin = new Thickness(0, 0, 0, 12) });

        var typeCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 12) };
        typeCombo.Items.Add(new ComboBoxItem { Content = "Purchase (stock in)", Tag = StockTransactionType.Purchase });
        typeCombo.Items.Add(new ComboBoxItem { Content = "Wastage", Tag = StockTransactionType.Wastage });
        typeCombo.Items.Add(new ComboBoxItem { Content = "Adjustment", Tag = StockTransactionType.Adjustment });
        typeCombo.SelectedIndex = 0;
        panel.Children.Add(typeCombo);

        var qtyBox = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(MakeRow("Quantity (positive number)", qtyBox));

        var noteBox = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(MakeRow("Reason / Note", noteBox));

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelBtn = new Button
        {
            Content = "Cancel",
            Style = (Style)System.Windows.Application.Current.FindResource("OutlineButton"),
            Padding = new Thickness(20, 10, 20, 10),
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };
        btnRow.Children.Add(cancelBtn);

        var saveBtn = new Button
        {
            Content = "Save",
            Style = (Style)System.Windows.Application.Current.FindResource("PrimaryButton"),
            Padding = new Thickness(20, 10, 20, 10)
        };
        saveBtn.Click += async (_, _) =>
        {
            if (!FormattingUtilities.TryParseDecimal(qtyBox.Text, out var qty) || qty <= 0)
            {
                MessageBox.Show("Enter a valid positive quantity", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var type = (StockTransactionType)((ComboBoxItem)typeCombo.SelectedItem).Tag;
            var delta = type == StockTransactionType.Wastage ? -qty : qty;
            if (type == StockTransactionType.Adjustment)
            {
                // For adjustment, delta is signed from current to target
                delta = qty - (product.StockQuantity ?? 0);
            }
            try
            {
                await svc.AdjustStockAsync(product.Id, delta, type, noteBox.Text, userId);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        btnRow.Children.Add(saveBtn);
        panel.Children.Add(btnRow);

        Content = panel;
    }

    private static System.Windows.Controls.Border MakeRow(string label, FrameworkElement ctrl)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextMutedBrush"), Margin = new Thickness(0, 0, 0, 4) });
        stack.Children.Add(ctrl);
        return new System.Windows.Controls.Border { Child = stack };
    }
}
