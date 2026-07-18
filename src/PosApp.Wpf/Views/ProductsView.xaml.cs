using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to load products", MessageBoxButton.OK, MessageBoxImage.Error);
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
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to open product editor", MessageBoxButton.OK, MessageBoxImage.Error);
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
                PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to open product editor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void Active_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not Product product) return;

        e.Handled = true;
        var previousValue = product.IsActive;
        var requestedValue = !previousValue;

        checkBox.IsChecked = requestedValue;
        checkBox.IsEnabled = false;
        try
        {
            await _inventory.SetProductActiveAsync(product.Id, requestedValue);
            product.IsActive = requestedValue;
            ProductsGrid.Items.Refresh();
        }
        catch (Exception ex)
        {
            product.IsActive = previousValue;
            checkBox.IsChecked = previousValue;
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.GetBaseException().Message, "Unable to update product status",
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
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Product catalog exported.", "CSV Export",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to export CSV", MessageBoxButton.OK, MessageBoxImage.Error);
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
        if (PosApp.Wpf.Helpers.LocalizedMessageBox.Show(modeDialog.ConfirmationText,
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
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(
                $"CSV import complete.\nCreated: {result.Created}\nUpdated: {result.Updated}\nStock changes: {result.StockAdjusted}{warningText}",
                "CSV Import", MessageBoxButton.OK,
                result.Warnings.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to import CSV", MessageBoxButton.OK, MessageBoxImage.Error);
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
    private readonly ComboBox _saleModeBox = new();
    private readonly ComboBox _unitBox = new();
    private readonly CheckBox _allowDiscountBox = new() { Content = "Allow discounts" };
    private readonly TextBlock _priceLabel = CreateEditorLabel(string.Empty);
    private readonly TextBlock _costLabel = CreateEditorLabel(string.Empty);
    private readonly TextBlock _stockLabel = CreateEditorLabel(string.Empty);
    private readonly TextBlock _thresholdLabel = CreateEditorLabel(string.Empty);
    private readonly TextBlock _measurementHelp = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, -2, 0, 12)
    };

    public ProductEditDialog(IInventoryService svc, IReadOnlyList<Category> categories, Product? existing = null)
    {
        _svc = svc;
        _isNew = existing == null;
        _product = existing == null
            ? new Product { IsActive = true, Unit = UnitOfMeasure.Piece, TaxRate = App.StoreSettings.DefaultTaxRate }
            : CopyProduct(existing);

        Title = _isNew ? "Add Product" : "Edit Product";
        Width = 540;
        Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BackgroundBrush");

        _nameBox.Text = _product.Name;
        _skuBox.Text = _product.Sku ?? string.Empty;
        _barcodeBox.Text = _product.Barcode ?? string.Empty;
        _priceBox.Text = _product.Price.ToString("0.####", CultureInfo.CurrentCulture);
        _costBox.Text = _product.CostPrice.ToString("0.####", CultureInfo.CurrentCulture);
        _stockBox.Text = _product.StockQuantity?.ToString("0.###", CultureInfo.CurrentCulture) ?? string.Empty;
        _thresholdBox.Text = _product.LowStockThreshold?.ToString("0.###", CultureInfo.CurrentCulture) ?? string.Empty;
        _taxBox.Text = _product.TaxRate.ToString("0.###", CultureInfo.CurrentCulture);
        _allowDiscountBox.IsChecked = _product.AllowDiscount;
        _allowDiscountBox.Content = DialogLayout.Text("Prod_AllowDiscount", "Allow discounts");
        _measurementHelp.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextMutedBrush");

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

        AddSaleModeChoice(ProductSaleMode.PerItem, DialogLayout.Text("Prod_SoldPerItem", "Per item"));
        AddSaleModeChoice(ProductSaleMode.Weight, DialogLayout.Text("Prod_SoldByWeight", "By weight"));
        AddSaleModeChoice(ProductSaleMode.Volume, DialogLayout.Text("Prod_SoldByVolume", "By volume"));
        AddSaleModeChoice(ProductSaleMode.Length, DialogLayout.Text("Prod_SoldByLength", "By length"));
        SelectSaleMode(_product.SaleMode);
        PopulateUnitChoices(_product.Unit);
        _saleModeBox.SelectionChanged += (_, _) => PopulateUnitChoices(null);
        _unitBox.SelectionChanged += (_, _) => UpdateMeasurementLabels();

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(MakeRow(DialogLayout.Text("Prod_Name", "Name"), _nameBox));
        panel.Children.Add(MakeRow(DialogLayout.Text("Prod_SkuOnly", "SKU"), _skuBox));
        panel.Children.Add(MakeRow(DialogLayout.Text("Prod_Barcode", "Barcode"), _barcodeBox));
        panel.Children.Add(MakeRow(DialogLayout.Text("Prod_Category", "Category"), _categoryBox));
        panel.Children.Add(MakeRow(DialogLayout.Text("Prod_SaleMode", "Sale mode"), _saleModeBox));
        panel.Children.Add(MakeRow(DialogLayout.Text("Prod_Unit", "Pricing unit"), _unitBox));
        panel.Children.Add(_measurementHelp);
        panel.Children.Add(MakeRow(_priceLabel, _priceBox));
        panel.Children.Add(MakeRow(_costLabel, _costBox));
        panel.Children.Add(MakeRow(_stockLabel, _stockBox));
        panel.Children.Add(MakeRow(_thresholdLabel, _thresholdBox));
        panel.Children.Add(MakeRow(DialogLayout.Text("Prod_TaxRate", "Tax rate %"), _taxBox));
        _allowDiscountBox.Margin = new Thickness(0, 0, 0, 16);
        panel.Children.Add(_allowDiscountBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var cancel = new Button
        {
            Content = DialogLayout.Text("Common_Cancel", "Cancel"),
            Style = (Style)Application.Current.FindResource("OutlineButton"),
            Padding = new Thickness(20, 10, 20, 10),
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = new Button
        {
            Content = DialogLayout.Text("Prod_Save", "Save"),
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
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Name is required.", "Invalid product", MessageBoxButton.OK, MessageBoxImage.Warning);
            _nameBox.Focus();
            return;
        }
        if (_categoryBox.SelectedItem is not ComboBoxItem categoryItem || categoryItem.Tag is not Category category)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Select a category.", "Invalid product", MessageBoxButton.OK, MessageBoxImage.Warning);
            _categoryBox.Focus();
            return;
        }
        if (!TryRequiredDecimal(_priceBox, "Price", out var price) || price < 0m) return;
        if (!TryRequiredDecimal(_costBox, "Cost price", out var cost) || cost < 0m) return;
        if (!TryOptionalDecimal(_stockBox, "Stock quantity", out var stock) || stock < 0m) return;
        if (!TryOptionalDecimal(_thresholdBox, "Low stock threshold", out var threshold) || threshold < 0m) return;
        if (!TryRequiredDecimal(_taxBox, "Tax rate", out var tax) || tax is < 0m or > 100m)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Tax rate must be between 0 and 100.", "Invalid product", MessageBoxButton.OK, MessageBoxImage.Warning);
            _taxBox.Focus();
            return;
        }

        _product.Name = name;
        _product.Sku = NormalizeOptional(_skuBox.Text);
        _product.Barcode = NormalizeOptional(_barcodeBox.Text);
        _product.CategoryId = category.Id;
        _product.Category = null;
        var saleMode = SelectedSaleMode;
        _product.Unit = _unitBox.SelectedItem is ComboBoxItem { Tag: UnitOfMeasure unit }
            ? unit
            : UnitOfMeasure.Piece;
        _product.Price = price;
        _product.CostPrice = cost;
        _product.StockQuantity = stock;
        _product.LowStockThreshold = threshold;
        _product.TaxRate = tax;
        _product.IsWeighted = saleMode != ProductSaleMode.PerItem;
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
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.GetBaseException().Message, "Unable to save product", MessageBoxButton.OK, MessageBoxImage.Error);
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
        PosApp.Wpf.Helpers.LocalizedMessageBox.Show($"{label} must be a valid number.", "Invalid product", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private ProductSaleMode SelectedSaleMode =>
        _saleModeBox.SelectedItem is ComboBoxItem { Tag: ProductSaleMode mode }
            ? mode
            : ProductSaleMode.PerItem;

    private void AddSaleModeChoice(ProductSaleMode mode, string label)
        => _saleModeBox.Items.Add(new ComboBoxItem { Content = label, Tag = mode });

    private void SelectSaleMode(ProductSaleMode mode)
    {
        for (var index = 0; index < _saleModeBox.Items.Count; index++)
        {
            if (_saleModeBox.Items[index] is ComboBoxItem { Tag: ProductSaleMode candidate } && candidate == mode)
            {
                _saleModeBox.SelectedIndex = index;
                return;
            }
        }
        _saleModeBox.SelectedIndex = 0;
    }

    private void PopulateUnitChoices(UnitOfMeasure? preferred)
    {
        var units = SelectedSaleMode switch
        {
            ProductSaleMode.Weight => new[] { UnitOfMeasure.Kilogram, UnitOfMeasure.Gram },
            ProductSaleMode.Volume => new[] { UnitOfMeasure.Liter, UnitOfMeasure.Milliliter },
            ProductSaleMode.Length => new[] { UnitOfMeasure.Meter },
            _ => new[] { UnitOfMeasure.Piece, UnitOfMeasure.Pack }
        };

        _unitBox.Items.Clear();
        foreach (var unit in units)
        {
            _unitBox.Items.Add(new ComboBoxItem
            {
                Content = $"{UnitName(unit)} ({unit.ToSymbol()})",
                Tag = unit
            });
        }
        var selected = preferred.HasValue && units.Contains(preferred.Value) ? preferred.Value : units[0];
        for (var index = 0; index < _unitBox.Items.Count; index++)
        {
            if (_unitBox.Items[index] is ComboBoxItem { Tag: UnitOfMeasure unit } && unit == selected)
            {
                _unitBox.SelectedIndex = index;
                break;
            }
        }
        UpdateMeasurementLabels();
    }

    private void UpdateMeasurementLabels()
    {
        var unit = _unitBox.SelectedItem is ComboBoxItem { Tag: UnitOfMeasure selected }
            ? selected
            : UnitOfMeasure.Piece;
        var symbol = unit.ToSymbol();
        _priceLabel.Text = string.Format(
            DialogLayout.Text("Prod_PricePer", "Selling price per {0}"), symbol);
        _costLabel.Text = string.Format(
            DialogLayout.Text("Prod_CostPer", "Cost price per {0}"), symbol);
        _stockLabel.Text = string.Format(
            DialogLayout.Text("Prod_StockIn", "Stock quantity in {0} (blank = untracked)"), symbol);
        _thresholdLabel.Text = string.Format(
            DialogLayout.Text("Prod_ThresholdIn", "Low-stock threshold in {0} (blank = none)"), symbol);
        _measurementHelp.Text = SelectedSaleMode == ProductSaleMode.PerItem
            ? DialogLayout.Text("Prod_PerItemHelp", "The price applies to one piece or pack.")
            : string.Format(
                DialogLayout.Text("Prod_MeasuredHelp", "The price applies to 1 {0}. The cashier enters the exact amount at checkout."),
                symbol);
    }

    private static string UnitName(UnitOfMeasure unit) => unit switch
    {
        UnitOfMeasure.Kilogram => DialogLayout.Text("Unit_Kilogram", "Kilogram"),
        UnitOfMeasure.Gram => DialogLayout.Text("Unit_Gram", "Gram"),
        UnitOfMeasure.Liter => DialogLayout.Text("Unit_Liter", "Liter"),
        UnitOfMeasure.Milliliter => DialogLayout.Text("Unit_Milliliter", "Milliliter"),
        UnitOfMeasure.Meter => DialogLayout.Text("Unit_Meter", "Meter"),
        UnitOfMeasure.Pack => DialogLayout.Text("Unit_Pack", "Pack"),
        _ => DialogLayout.Text("Unit_Piece", "Piece")
    };

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

    private static TextBlock CreateEditorLabel(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextMutedBrush"),
        Margin = new Thickness(0, 0, 0, 4)
    };

    private static Border MakeRow(string label, FrameworkElement control)
        => MakeRow(CreateEditorLabel(label), control);

    private static Border MakeRow(TextBlock label, FrameworkElement control)
    {
        var stack = new StackPanel();
        stack.Children.Add(label);
        control.Margin = new Thickness(0, 0, 0, 12);
        stack.Children.Add(control);
        return new Border { Child = stack };
    }
}
