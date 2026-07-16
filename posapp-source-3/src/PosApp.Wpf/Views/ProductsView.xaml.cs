using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;

namespace PosApp.Wpf.Views;

public partial class ProductsView : UserControl, IRefreshable
{
    private readonly IInventoryService _inventory;
    private readonly ICatalogTransferService _catalogTransfer;
    private ObservableCollection<Product> _all = new();
    private int _selectedCategoryId;

    public ProductsView(IInventoryService inventory, ICatalogTransferService catalogTransfer)
    {
        InitializeComponent();
        _inventory = inventory;
        _catalogTransfer = catalogTransfer;
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

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Product Catalog",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = $"posapp-products-{DateTime.Today:yyyyMMdd}.csv",
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            IsEnabled = false;
            await _catalogTransfer.ExportProductsAsync(dialog.FileName);
            MessageBox.Show("Product catalog exported.", "CSV Export",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to export CSV", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void ImportCsv_Click(object sender, RoutedEventArgs e)
    {
        var fileDialog = new OpenFileDialog
        {
            Title = "Import Product Catalog",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (fileDialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var modeDialog = new CatalogImportModeDialog { Owner = Window.GetWindow(this) };
        if (modeDialog.ShowDialog() != true) return;
        if (MessageBox.Show(modeDialog.ConfirmationText,
                "Confirm CSV Import", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        try
        {
            IsEnabled = false;
            var result = await _catalogTransfer.ImportProductsAsync(
                fileDialog.FileName, modeDialog.Mode, App.CurrentUser?.Id);
            var warningText = result.Warnings.Count == 0
                ? string.Empty
                : $"\n\nWarnings ({result.Warnings.Count}):\n" +
                  string.Join("\n", result.Warnings.Take(5)) +
                  (result.Warnings.Count > 5 ? "\n…" : string.Empty);
            MessageBox.Show(
                $"CSV import complete.\nCreated: {result.Created}\nUpdated: {result.Updated}\nStock changes: {result.StockAdjusted}{warningText}",
                "CSV Import", MessageBoxButton.OK,
                result.Warnings.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to import CSV", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }
}

public sealed class CatalogImportModeDialog : Window
{
    private readonly RadioButton _catalog = new() { Content = "Catalog only — update product details and costs" };
    private readonly RadioButton _count = new() { Content = "Inventory count — set stock to each CSV quantity" };
    private readonly RadioButton _purchase = new() { Content = "Stock receipt — add each CSV quantity to stock" };

    public CatalogImportModeDialog()
    {
        Title = "Choose CSV Import Mode";
        Width = 610;
        Height = 390;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _catalog.IsChecked = true;

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "How should StockQuantity be handled?", Style = (Style)FindResource("Heading2") });
        panel.Children.Add(new TextBlock
        {
            Text = "All modes create missing products and categories. Existing products are matched by SKU, barcode, then name.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)FindResource("Muted"),
            Margin = new Thickness(0, 0, 0, 18)
        });
        foreach (var option in new[] { _catalog, _count, _purchase })
        {
            option.GroupName = "ImportMode";
            option.Margin = new Thickness(0, 0, 0, 14);
            option.FontSize = 14;
            panel.Children.Add(option);
        }
        var caution = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("InfoSurfaceBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Child = new TextBlock
            {
                Text = "Use Inventory count only when the CSV contains the physically counted final balance. Use Stock receipt when the CSV quantity is newly delivered stock. Use New Purchase for a supplier-linked purchase document.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("InfoTextBrush")
            }
        };
        panel.Children.Add(caution);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("OutlineButton"), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var next = new Button { Content = "Continue", Style = (Style)FindResource("PrimaryButton") };
        next.Click += (_, _) => { DialogResult = true; Close(); };
        actions.Children.Add(cancel);
        actions.Children.Add(next);
        panel.Children.Add(actions);
        Content = panel;
    }

    public ProductImportMode Mode => _purchase.IsChecked == true
        ? ProductImportMode.Purchase
        : _count.IsChecked == true
            ? ProductImportMode.InventoryCount
            : ProductImportMode.CatalogOnly;

    public string ConfirmationText => Mode switch
    {
        ProductImportMode.InventoryCount =>
            "This will replace existing stock balances with the quantities in the CSV and record every difference. Continue?",
        ProductImportMode.Purchase =>
            "This will add the CSV quantities to existing stock and update moving-average cost. Continue?",
        _ => "This will update catalog fields. Existing stock balances will not be changed. Continue?"
    };
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
