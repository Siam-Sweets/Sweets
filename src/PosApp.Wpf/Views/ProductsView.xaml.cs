using System.Collections.ObjectModel;
using System.Globalization;
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

    public async Task RefreshAsync()
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
        var products = await _inventory.SearchProductsAsync(null, includeInactive: true);
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
            if (dlg.ShowDialog() == true) await RefreshAsync();
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
                if (dlg.ShowDialog() == true) await RefreshAsync();
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
            var activating = !p.IsActive;
            var action = activating ? "restore" : "deactivate";
            var confirm = MessageBox.Show($"{char.ToUpperInvariant(action[0])}{action[1..]} product '{p.Name}'?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            var previous = p.IsActive;
            try
            {
                p.IsActive = activating;
                await _inventory.CreateOrUpdateProductAsync(p, App.CurrentUser?.Id);
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                p.IsActive = previous;
                MessageBox.Show(ex.Message, $"Unable to {action} product", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void Weighted_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not Product product) return;

        var previousValue = product.IsWeighted;
        var requestedValue = checkBox.IsChecked == true;
        if (previousValue == requestedValue) return;

        checkBox.IsEnabled = false;
        try
        {
            await _inventory.SetProductWeightedAsync(product.Id, requestedValue);
            product.IsWeighted = requestedValue;
        }
        catch (Exception ex)
        {
            product.IsWeighted = previousValue;
            checkBox.IsChecked = previousValue;
            MessageBox.Show(ex.GetBaseException().Message, "Unable to update weighted product",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            checkBox.IsEnabled = true;
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
    private readonly TextBox _nameBox = new();
    private readonly TextBox _skuBox = new();
    private readonly TextBox _barcodeBox = new();
    private readonly TextBox _priceBox = new();
    private readonly TextBox _costBox = new();
    private readonly TextBox _stockBox = new();
    private readonly TextBox _thresholdBox = new();
    private readonly TextBox _taxBox = new();
    private readonly ComboBox _categoryBox = new();
    private readonly ComboBox _unitBox = new();
    private readonly CheckBox _weightedBox = new() { Content = "Weighted (sold by kg/liter)" };
    private readonly CheckBox _allowDiscountBox = new() { Content = "Allow discounts" };

    public ProductEditDialog(IInventoryService svc, IReadOnlyList<Category> categories, Product? existing = null)
    {
        _svc = svc;
        _isNew = existing == null;
        _product = existing == null
            ? new Product { IsActive = true, Unit = UnitOfMeasure.Piece, TaxRate = App.StoreSettings.DefaultTaxRate }
            : CopyProduct(existing);

        Title = _isNew ? "Add Product" : "Edit Product";
        Width = 540;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BackgroundBrush");

        _nameBox.Text = _product.Name;
        _skuBox.Text = _product.Sku ?? string.Empty;
        _barcodeBox.Text = _product.Barcode ?? string.Empty;
        _priceBox.Text = _product.Price.ToString("0.00", CultureInfo.CurrentCulture);
        _costBox.Text = _product.CostPrice.ToString("0.00", CultureInfo.CurrentCulture);
        _stockBox.Text = _product.StockQuantity?.ToString("0.###", CultureInfo.CurrentCulture) ?? string.Empty;
        _thresholdBox.Text = _product.LowStockThreshold?.ToString("0.###", CultureInfo.CurrentCulture) ?? string.Empty;
        _taxBox.Text = _product.TaxRate.ToString("0.###", CultureInfo.CurrentCulture);
        _weightedBox.IsChecked = _product.IsWeighted;
        _allowDiscountBox.IsChecked = _product.AllowDiscount;

        foreach (var category in categories)
            _categoryBox.Items.Add(new ComboBoxItem { Content = category.Name, Tag = category });
        for (var index = 0; index < _categoryBox.Items.Count; index++)
        {
            if (_categoryBox.Items[index] is ComboBoxItem item && item.Tag is Category category && category.Id == _product.CategoryId)
            {
                _categoryBox.SelectedIndex = index;
                break;
            }
        }
        if (_categoryBox.SelectedIndex < 0 && _categoryBox.Items.Count > 0)
            _categoryBox.SelectedIndex = 0;

        foreach (var unit in Enum.GetValues<UnitOfMeasure>())
            _unitBox.Items.Add(unit);
        _unitBox.SelectedItem = _product.Unit;

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(MakeRow("Name", _nameBox));
        panel.Children.Add(MakeRow("SKU", _skuBox));
        panel.Children.Add(MakeRow("Barcode", _barcodeBox));
        panel.Children.Add(MakeRow("Category", _categoryBox));
        panel.Children.Add(MakeRow("Unit", _unitBox));
        panel.Children.Add(MakeRow("Price", _priceBox));
        panel.Children.Add(MakeRow("Cost Price", _costBox));
        panel.Children.Add(MakeRow("Stock Quantity (blank = untracked)", _stockBox));
        panel.Children.Add(MakeRow("Low Stock Threshold (blank = none)", _thresholdBox));
        panel.Children.Add(MakeRow("Tax Rate %", _taxBox));
        _weightedBox.Margin = new Thickness(0, 4, 0, 10);
        _allowDiscountBox.Margin = new Thickness(0, 0, 0, 16);
        panel.Children.Add(_weightedBox);
        panel.Children.Add(_allowDiscountBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var cancel = new Button
        {
            Content = "Cancel",
            Style = (Style)Application.Current.FindResource("OutlineButton"),
            Padding = new Thickness(20, 10, 20, 10),
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = new Button
        {
            Content = "Save",
            Style = (Style)Application.Current.FindResource("PrimaryButton"),
            Padding = new Thickness(20, 10, 20, 10)
        };
        save.Click += Save_Click;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = _nameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show("Name is required.", "Invalid product", MessageBoxButton.OK, MessageBoxImage.Warning);
            _nameBox.Focus();
            return;
        }
        if (_categoryBox.SelectedItem is not ComboBoxItem categoryItem || categoryItem.Tag is not Category category)
        {
            MessageBox.Show("Select a category.", "Invalid product", MessageBoxButton.OK, MessageBoxImage.Warning);
            _categoryBox.Focus();
            return;
        }
        if (!TryRequiredDecimal(_priceBox, "Price", out var price) || price < 0m) return;
        if (!TryRequiredDecimal(_costBox, "Cost price", out var cost) || cost < 0m) return;
        if (!TryOptionalDecimal(_stockBox, "Stock quantity", out var stock) || stock < 0m) return;
        if (!TryOptionalDecimal(_thresholdBox, "Low stock threshold", out var threshold) || threshold < 0m) return;
        if (!TryRequiredDecimal(_taxBox, "Tax rate", out var tax) || tax is < 0m or > 100m)
        {
            MessageBox.Show("Tax rate must be between 0 and 100.", "Invalid product", MessageBoxButton.OK, MessageBoxImage.Warning);
            _taxBox.Focus();
            return;
        }

        _product.Name = name;
        _product.Sku = NormalizeOptional(_skuBox.Text);
        _product.Barcode = NormalizeOptional(_barcodeBox.Text);
        _product.CategoryId = category.Id;
        _product.Category = null;
        _product.Unit = _unitBox.SelectedItem is UnitOfMeasure unit ? unit : UnitOfMeasure.Piece;
        _product.Price = price;
        _product.CostPrice = cost;
        _product.StockQuantity = stock;
        _product.LowStockThreshold = threshold;
        _product.TaxRate = tax;
        _product.IsWeighted = _weightedBox.IsChecked == true;
        _product.AllowDiscount = _allowDiscountBox.IsChecked == true;

        try
        {
            IsEnabled = false;
            await _svc.CreateOrUpdateProductAsync(_product, App.CurrentUser?.Id);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.GetBaseException().Message, "Unable to save product", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private static bool TryRequiredDecimal(TextBox box, string label, out decimal value)
    {
        if (decimal.TryParse(box.Text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out value) ||
            decimal.TryParse(box.Text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out value))
            return true;
        MessageBox.Show($"{label} must be a valid number.", "Invalid product", MessageBoxButton.OK, MessageBoxImage.Warning);
        box.Focus();
        return false;
    }

    private static bool TryOptionalDecimal(TextBox box, string label, out decimal? value)
    {
        if (string.IsNullOrWhiteSpace(box.Text))
        {
            value = null;
            return true;
        }
        if (TryRequiredDecimal(box, label, out var parsed))
        {
            value = parsed;
            return true;
        }
        value = null;
        return false;
    }

    private static string? NormalizeOptional(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

    private static Border MakeRow(string label, FrameworkElement control)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextMutedBrush"),
            Margin = new Thickness(0, 0, 0, 4)
        });
        control.Margin = new Thickness(0, 0, 0, 12);
        stack.Children.Add(control);
        return new Border { Child = stack };
    }
}

