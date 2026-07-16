using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;

namespace PosApp.Wpf.Views;

public partial class ProductsView : UserControl, IRefreshable
{
    private readonly IInventoryService _inventory;
    private ObservableCollection<Product> _all = new();
    private int _selectedCategoryId;

    public ProductsView(IInventoryService inventory)
    {
        InitializeComponent();
        _inventory = inventory;
    }

    public async void Refresh()
    {
        IsEnabled = false;
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to load products", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async Task LoadAsync()
    {
        var products = await _inventory.SearchProductsAsync(null);
        _all = new ObservableCollection<Product>(products);
        ProductsGrid.ItemsSource = _all;

        var cats = await _inventory.ListCategoriesAsync();
        CategoryFilter.Items.Clear();
        CategoryFilter.Items.Add(new ComboBoxItem { Content = "All Categories", Tag = 0 });
        foreach (var c in cats)
        {
            CategoryFilter.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c.Id });
        }
        CategoryFilter.SelectedIndex = 0;
        ApplyFilters();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilters();
    }

    private void CategoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryFilter.SelectedItem is not ComboBoxItem item) return;
        _selectedCategoryId = (int)(item.Tag ?? 0);
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        if (ProductsGrid == null) return;
        var term = SearchBox?.Text?.Trim() ?? "";
        var filtered = _all.Where(p =>
            (_selectedCategoryId == 0 || p.CategoryId == _selectedCategoryId) &&
            (string.IsNullOrEmpty(term) ||
             p.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
             (p.Sku?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
             (p.Barcode?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
             (p.Category?.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)))
            .ToList();
        ProductsGrid.ItemsSource = filtered;
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var categories = await _inventory.ListCategoriesAsync();
            var dlg = new ProductEditDialog(_inventory, categories) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true) Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to open product editor", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Product p)
        {
            try
            {
                var categories = await _inventory.ListCategoriesAsync();
                var dlg = new ProductEditDialog(_inventory, categories, p) { Owner = Window.GetWindow(this) };
                if (dlg.ShowDialog() == true) Refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Unable to open product editor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Product p)
        {
            var confirm = MessageBox.Show($"Delete product '{p.Name}'?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            try
            {
                p.IsActive = false;
                await _inventory.CreateOrUpdateProductAsync(p);
                Refresh();
            }
            catch (Exception ex)
            {
                p.IsActive = true;
                MessageBox.Show(ex.Message, "Unable to delete product", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}

public class ProductEditDialog : Window
{
    private readonly IInventoryService _svc;
    private readonly Product _product;
    private readonly bool _isNew;

    public ProductEditDialog(IInventoryService svc, IReadOnlyList<Category> categories, Product? existing = null)
    {
        _svc = svc;
        _isNew = existing == null;
        _product = existing == null
            ? new Product { IsActive = true, Unit = UnitOfMeasure.Piece }
            : CopyProduct(existing);

        Title = _isNew ? "Add Product" : "Edit Product";
        Width = 520; Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BackgroundBrush");

        var panel = new StackPanel { Margin = new Thickness(24) };

        panel.Children.Add(MakeRow("Name", () =>
        {
            var tb = new TextBox { Text = _product.Name };
            tb.TextChanged += (_, _) => _product.Name = tb.Text;
            return tb;
        }));

        panel.Children.Add(MakeRow("SKU / Barcode", () =>
        {
            var tb = new TextBox { Text = _product.Sku ?? "" };
            tb.TextChanged += (_, _) => _product.Sku = tb.Text;
            return tb;
        }));

        // Category picker
        var catCombo = new ComboBox { Margin = new Thickness(0, 4, 0, 12) };
        foreach (var c in categories) catCombo.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c });
        catCombo.SelectionChanged += (_, _) =>
        {
            if (catCombo.SelectedItem is ComboBoxItem ci && ci.Tag is Category c)
            {
                _product.CategoryId = c.Id;
                _product.Category = null;
            }
        };
        if (_product.CategoryId > 0)
        {
            for (int i = 0; i < catCombo.Items.Count; i++)
            {
                if (catCombo.Items[i] is ComboBoxItem ci && ci.Tag is Category c && c.Id == _product.CategoryId)
                {
                    catCombo.SelectedIndex = i; break;
                }
            }
        }
        else if (catCombo.Items.Count > 0) catCombo.SelectedIndex = 0;
        panel.Children.Add(MakeRow("Category", () => catCombo));

        panel.Children.Add(MakeRow("Price", () =>
        {
            var tb = new TextBox { Text = _product.Price.ToString("0.00") };
            tb.TextChanged += (_, _) => { if (decimal.TryParse(tb.Text, out var v)) _product.Price = v; };
            return tb;
        }));

        panel.Children.Add(MakeRow("Cost Price", () =>
        {
            var tb = new TextBox { Text = _product.CostPrice.ToString("0.00") };
            tb.TextChanged += (_, _) => { if (decimal.TryParse(tb.Text, out var v)) _product.CostPrice = v; };
            return tb;
        }));

        panel.Children.Add(MakeRow("Stock Quantity", () =>
        {
            var tb = new TextBox { Text = _product.StockQuantity?.ToString() ?? "" };
            tb.TextChanged += (_, _) => { if (decimal.TryParse(tb.Text, out var v)) _product.StockQuantity = v; };
            return tb;
        }));

        panel.Children.Add(MakeRow("Low Stock Threshold", () =>
        {
            var tb = new TextBox { Text = _product.LowStockThreshold?.ToString() ?? "" };
            tb.TextChanged += (_, _) => { if (decimal.TryParse(tb.Text, out var v)) _product.LowStockThreshold = v; };
            return tb;
        }));

        panel.Children.Add(MakeRow("Tax Rate %", () =>
        {
            var tb = new TextBox { Text = _product.TaxRate.ToString("0.###") };
            tb.TextChanged += (_, _) => { if (decimal.TryParse(tb.Text, out var v)) _product.TaxRate = v; };
            return tb;
        }));

        var weightedCheckbox = new CheckBox { Content = "Weighted (sold by kg/liter)", IsChecked = _product.IsWeighted, Margin = new Thickness(0, 8, 0, 16) };
        weightedCheckbox.Checked += (_, _) => _product.IsWeighted = true;
        weightedCheckbox.Unchecked += (_, _) => _product.IsWeighted = false;
        panel.Children.Add(weightedCheckbox);

        var btnRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
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
            if (string.IsNullOrWhiteSpace(_product.Name))
            {
                MessageBox.Show("Name is required", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_product.CategoryId <= 0)
            {
                MessageBox.Show("Select a category", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                await _svc.CreateOrUpdateProductAsync(_product);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Unable to save product", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        btnRow.Children.Add(saveBtn);
        panel.Children.Add(btnRow);

        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Content = scroll;
    }

    private static Product CopyProduct(Product source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Description = source.Description,
        Sku = source.Sku,
        Barcode = source.Barcode,
        CategoryId = source.CategoryId,
        Price = source.Price,
        CostPrice = source.CostPrice,
        TaxRate = source.TaxRate,
        Unit = source.Unit,
        StockQuantity = source.StockQuantity,
        LowStockThreshold = source.LowStockThreshold,
        ImagePath = source.ImagePath,
        IsWeighted = source.IsWeighted,
        IsActive = source.IsActive,
        AllowDiscount = source.AllowDiscount,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt
    };

    private static System.Windows.Controls.Border MakeRow(string label, Func<FrameworkElement> makeControl)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextMutedBrush"), Margin = new Thickness(0, 0, 0, 4) });
        var ctrl = makeControl();
        ctrl.Margin = new Thickness(0, 0, 0, 12);
        stack.Children.Add(ctrl);
        return new System.Windows.Controls.Border { Child = stack };
    }
}
