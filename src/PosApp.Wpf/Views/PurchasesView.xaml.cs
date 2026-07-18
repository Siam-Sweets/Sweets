using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Core.Utilities;

namespace PosApp.Wpf.Views;

public partial class PurchasesView : UserControl, IRefreshable
{
    private readonly IPurchaseService _purchases;
    private readonly IInventoryService _inventory;
    private IReadOnlyList<Supplier> _suppliers = Array.Empty<Supplier>();
    private bool _datesInitialized;

    public PurchasesView(IPurchaseService purchases, IInventoryService inventory)
    {
        InitializeComponent();
        _purchases = purchases;
        _inventory = inventory;
    }

    public async Task RefreshAsync()
    {
        IsEnabled = false;
        try
        {
            if (!_datesInitialized)
            {
                FromPicker.SelectedDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                ToPicker.SelectedDate = DateTime.Today;
                _datesInitialized = true;
            }
            await LoadAsync();
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to load purchases", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async Task LoadAsync()
    {
        var fromLocal = (FromPicker.SelectedDate ?? DateTime.Today).Date;
        var toLocal = (ToPicker.SelectedDate ?? DateTime.Today).Date;
        if (toLocal < fromLocal) throw new InvalidOperationException("The To date cannot be before the From date.");

        var documents = await _purchases.GetPurchasesAsync(fromLocal, toLocal);
        _suppliers = await _purchases.SearchSuppliersAsync();
        PurchasesGrid.ItemsSource = documents;
        SuppliersGrid.ItemsSource = _suppliers;
        DocumentCountText.Text = documents.Count.ToString();
        PurchaseTotalText.Text = FormattingUtilities.Money(documents.Sum(item => item.Total), App.StoreSettings);
        SupplierCountText.Text = _suppliers.Count.ToString();
        ApplySupplierFilter();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private async void NewPurchase_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var suppliers = await _purchases.SearchSuppliersAsync();
            var products = (await _inventory.SearchProductsAsync(null))
                .Where(product => product.StockQuantity.HasValue)
                .ToList();
            var dialog = new PurchaseEditDialog(_purchases, suppliers, products)
            {
                Owner = Window.GetWindow(this)
            };
            if (dialog.ShowDialog() == true) _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to start purchase", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddSupplier_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SupplierEditDialog(_purchases) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) _ = RefreshAsync();
    }

    private void EditSupplier_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Supplier supplier }) return;
        var dialog = new SupplierEditDialog(_purchases, supplier) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) _ = RefreshAsync();
    }

    private void SupplierSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplySupplierFilter();

    private void ApplySupplierFilter()
    {
        if (SuppliersGrid == null) return;
        var term = SupplierSearchBox?.Text?.Trim() ?? string.Empty;
        SuppliersGrid.ItemsSource = string.IsNullOrEmpty(term)
            ? _suppliers
            : _suppliers.Where(supplier =>
                    supplier.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    (supplier.Phone?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (supplier.Email?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
    }

    private void ViewPurchase_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PurchaseDocument purchase }) ShowDetails(purchase);
    }

    private void PurchasesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PurchasesGrid.SelectedItem is PurchaseDocument purchase) ShowDetails(purchase);
    }

    private void ShowDetails(PurchaseDocument purchase)
    {
        new PurchaseDetailsDialog(purchase) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
}

public sealed class PurchaseEditDialog : Window
{
    private readonly IPurchaseService _service;
    private readonly IReadOnlyList<Product> _products;
    private readonly ObservableCollection<PurchaseDraftLine> _lines = new();
    private readonly ComboBox _supplierCombo = new();
    private readonly ComboBox _productCombo = new();
    private readonly TextBox _referenceBox = new();
    private readonly DatePicker _datePicker = new();
    private readonly TextBox _quantityBox = new() { Text = "1" };
    private readonly TextBox _costBox = new();
    private readonly TextBox _taxBox = new();
    private readonly TextBox _noteBox = new() { AcceptsReturn = true, Height = 55, TextWrapping = TextWrapping.Wrap };
    private readonly DataGrid _linesGrid = new();
    private readonly TextBlock _totalsText = new();

    public PurchaseEditDialog(
        IPurchaseService service,
        IReadOnlyList<Supplier> suppliers,
        IReadOnlyList<Product> products)
    {
        _service = service;
        _products = products;
        Title = "New Purchase";
        Width = 940;
        Height = 700;
        MinWidth = 820;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _supplierCombo.Items.Add(new ComboBoxItem { Content = "(no supplier)", Tag = null });
        foreach (var supplier in suppliers)
            _supplierCombo.Items.Add(new ComboBoxItem { Content = supplier.Name, Tag = supplier });
        _supplierCombo.SelectedIndex = 0;

        foreach (var product in products)
            _productCombo.Items.Add(new ComboBoxItem
            {
                Content = string.IsNullOrWhiteSpace(product.Sku)
                    ? product.Name
                    : $"{product.Name}  ·  {product.Sku}",
                Tag = product
            });
        _productCombo.SelectionChanged += Product_SelectionChanged;
        if (_productCombo.Items.Count > 0) _productCombo.SelectedIndex = 0;
        _datePicker.SelectedDate = DateTime.Today;

        _linesGrid.ItemsSource = _lines;
        _linesGrid.AutoGenerateColumns = false;
        _linesGrid.IsReadOnly = true;
        _linesGrid.Columns.Add(new DataGridTextColumn { Header = "Product", Binding = new System.Windows.Data.Binding("ProductName"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _linesGrid.Columns.Add(new DataGridTextColumn { Header = "SKU", Binding = new System.Windows.Data.Binding("Sku"), Width = 100 });
        _linesGrid.Columns.Add(new DataGridTextColumn { Header = "Qty", Binding = new System.Windows.Data.Binding("Quantity") { StringFormat = "0.###" }, Width = 75 });
        _linesGrid.Columns.Add(new DataGridTextColumn { Header = "Cost", Binding = new System.Windows.Data.Binding("UnitCost") { StringFormat = "0.00" }, Width = 90 });
        _linesGrid.Columns.Add(new DataGridTextColumn { Header = "Tax %", Binding = new System.Windows.Data.Binding("TaxRate") { StringFormat = "0.###" }, Width = 70 });
        _linesGrid.Columns.Add(new DataGridTextColumn { Header = "Total", Binding = new System.Windows.Data.Binding("LineTotal") { StringFormat = "0.00" }, Width = 100 });

        Content = BuildContent();
        UpdateTotals();
    }

    private FrameworkElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = "Receive supplier stock",
            Style = (Style)FindResource("Heading2"),
            Margin = new Thickness(0, 0, 0, 14)
        };
        root.Children.Add(heading);

        var header = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        for (var i = 0; i < 3; i++) header.ColumnDefinitions.Add(new ColumnDefinition());
        AddLabeled(header, "Supplier", _supplierCombo, 0);
        AddLabeled(header, "Supplier invoice / reference", _referenceBox, 1);
        AddLabeled(header, "Document date", _datePicker, 2);
        Grid.SetRow(header, 1);
        root.Children.Add(header);

        var middle = new Grid();
        middle.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        middle.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        middle.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var addRow = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AddLabeled(addRow, "Product", _productCombo, 0);
        AddLabeled(addRow, "Quantity", _quantityBox, 1);
        AddLabeled(addRow, "Unit cost", _costBox, 2);
        AddLabeled(addRow, "Tax %", _taxBox, 3);
        var addButton = new Button
        {
            Content = "Add Line",
            Style = (Style)FindResource("PrimaryButton"),
            Margin = new Thickness(8, 20, 0, 0),
            Padding = new Thickness(14, 8, 14, 8),
            VerticalAlignment = VerticalAlignment.Top
        };
        addButton.Click += AddLine_Click;
        Grid.SetColumn(addButton, 4);
        addRow.Children.Add(addButton);
        middle.Children.Add(addRow);

        Grid.SetRow(_linesGrid, 1);
        middle.Children.Add(_linesGrid);

        var lineFooter = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        lineFooter.ColumnDefinitions.Add(new ColumnDefinition());
        lineFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var removeButton = new Button
        {
            Content = "Remove Selected",
            Style = (Style)FindResource("OutlineButton"),
            Padding = new Thickness(12, 7, 12, 7)
        };
        removeButton.Click += (_, _) =>
        {
            if (_linesGrid.SelectedItem is PurchaseDraftLine line)
            {
                _lines.Remove(line);
                UpdateTotals();
            }
        };
        lineFooter.Children.Add(removeButton);
        _totalsText.FontSize = 18;
        _totalsText.FontWeight = FontWeights.SemiBold;
        Grid.SetColumn(_totalsText, 1);
        lineFooter.Children.Add(_totalsText);
        Grid.SetRow(lineFooter, 2);
        middle.Children.Add(lineFooter);

        Grid.SetRow(middle, 2);
        root.Children.Add(middle);

        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AddLabeled(footer, "Internal note", _noteBox, 0);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(16, 0, 0, 0)
        };
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("OutlineButton"), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var post = new Button { Content = "Post Purchase", Style = (Style)FindResource("SuccessButton") };
        post.Click += Post_Click;
        actions.Children.Add(cancel);
        actions.Children.Add(post);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        return root;
    }

    private static void AddLabeled(Grid grid, string label, FrameworkElement control, int column)
    {
        var panel = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 8, 0, 0, 0) };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(control);
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
    }

    private void Product_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_productCombo.SelectedItem is ComboBoxItem { Tag: Product product })
        {
            _costBox.Text = product.CostPrice.ToString("0.00");
            _taxBox.Text = product.TaxRate.ToString("0.###");
        }
    }

    private void AddLine_Click(object sender, RoutedEventArgs e)
    {
        if (_productCombo.SelectedItem is not ComboBoxItem { Tag: Product product })
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Select a product.", "Purchase", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!FormattingUtilities.TryParseDecimal(_quantityBox.Text, out var quantity) || quantity <= 0m ||
            !FormattingUtilities.TryParseDecimal(_costBox.Text, out var cost) || cost < 0m ||
            !FormattingUtilities.TryParseDecimal(_taxBox.Text, out var tax) || tax is < 0m or > 100m)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Enter a positive quantity and valid cost/tax values.", "Purchase", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var existing = _lines.FirstOrDefault(line =>
            line.ProductId == product.Id && line.UnitCost == cost && line.TaxRate == tax);
        if (existing == null)
        {
            _lines.Add(new PurchaseDraftLine
            {
                ProductId = product.Id,
                ProductName = product.Name,
                Sku = product.Sku,
                Quantity = quantity,
                UnitCost = cost,
                TaxRate = tax
            });
        }
        else
        {
            var index = _lines.IndexOf(existing);
            _lines[index] = new PurchaseDraftLine
            {
                ProductId = existing.ProductId,
                ProductName = existing.ProductName,
                Sku = existing.Sku,
                Quantity = existing.Quantity + quantity,
                UnitCost = cost,
                TaxRate = tax
            };
        }
        _quantityBox.Text = "1";
        UpdateTotals();
    }

    private void UpdateTotals()
    {
        var subtotal = _lines.Sum(line => line.LineSubtotal);
        var tax = _lines.Sum(line => line.LineTax);
        _totalsText.Text = $"Subtotal {FormattingUtilities.Money(subtotal, App.StoreSettings)}   ·   " +
                           $"Tax {FormattingUtilities.Money(tax, App.StoreSettings)}   ·   " +
                           $"Total {FormattingUtilities.Money(subtotal + tax, App.StoreSettings)}";
    }

    private async void Post_Click(object sender, RoutedEventArgs e)
    {
        if (_lines.Count == 0)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Add at least one product.", "Purchase", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (App.CurrentUser == null) return;

        try
        {
            IsEnabled = false;
            var localDate = _datePicker.SelectedDate ?? DateTime.Today;
            var documentTime = localDate.Date.Add(DateTime.Now.TimeOfDay).ToUniversalTime();
            var supplier = (_supplierCombo.SelectedItem as ComboBoxItem)?.Tag as Supplier;
            var draft = new PurchaseDraft
            {
                SupplierId = supplier?.Id,
                UserId = App.CurrentUser.Id,
                ExternalReference = _referenceBox.Text,
                DocumentDate = documentTime,
                StockDate = DateTime.UtcNow,
                Note = _noteBox.Text,
                Lines = _lines.ToList()
            };
            var posted = await _service.PostPurchaseAsync(draft);
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show($"Purchase {posted.DocumentNumber} posted and stock updated.",
                "Purchase", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to post purchase", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }
}

public sealed class SupplierEditDialog : Window
{
    private readonly IPurchaseService _service;
    private readonly Supplier _supplier;
    private readonly TextBox _name = new();
    private readonly TextBox _phone = new();
    private readonly TextBox _email = new();
    private readonly TextBox _taxId = new();
    private readonly TextBox _address = new() { AcceptsReturn = true, Height = 60, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _notes = new() { AcceptsReturn = true, Height = 60, TextWrapping = TextWrapping.Wrap };

    public SupplierEditDialog(IPurchaseService service, Supplier? existing = null)
    {
        _service = service;
        _supplier = existing == null
            ? new Supplier()
            : new Supplier
            {
                Id = existing.Id,
                Name = existing.Name,
                Phone = existing.Phone,
                Email = existing.Email,
                Address = existing.Address,
                TaxId = existing.TaxId,
                Notes = existing.Notes,
                IsActive = existing.IsActive,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = existing.UpdatedAt
            };
        Title = existing == null ? "Add Supplier" : "Edit Supplier";
        Width = 520;
        Height = 610;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _name.Text = _supplier.Name;
        _phone.Text = _supplier.Phone ?? string.Empty;
        _email.Text = _supplier.Email ?? string.Empty;
        _taxId.Text = _supplier.TaxId ?? string.Empty;
        _address.Text = _supplier.Address ?? string.Empty;
        _notes.Text = _supplier.Notes ?? string.Empty;
        Content = BuildContent(existing != null);
    }

    private FrameworkElement BuildContent(bool canDeactivate)
    {
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Field("Supplier name", _name));
        panel.Children.Add(Field("Phone", _phone));
        panel.Children.Add(Field("Email", _email));
        panel.Children.Add(Field("Tax ID / registration", _taxId));
        panel.Children.Add(Field("Address", _address));
        panel.Children.Add(Field("Notes", _notes));
        var actions = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (canDeactivate)
        {
            var deactivate = new Button { Content = "Deactivate", Style = (Style)FindResource("DangerButton") };
            deactivate.Click += Deactivate_Click;
            actions.Children.Add(deactivate);
        }
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("OutlineButton"), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = new Button { Content = "Save Supplier", Style = (Style)FindResource("PrimaryButton") };
        save.Click += Save_Click;
        right.Children.Add(cancel);
        right.Children.Add(save);
        Grid.SetColumn(right, 1);
        actions.Children.Add(right);
        panel.Children.Add(actions);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static FrameworkElement Field(string label, FrameworkElement control)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
        stack.Children.Add(control);
        return stack;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        _supplier.Name = _name.Text;
        _supplier.Phone = Empty(_phone.Text);
        _supplier.Email = Empty(_email.Text);
        _supplier.TaxId = Empty(_taxId.Text);
        _supplier.Address = Empty(_address.Text);
        _supplier.Notes = Empty(_notes.Text);
        try
        {
            await _service.CreateOrUpdateSupplierAsync(_supplier);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to save supplier", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Deactivate_Click(object sender, RoutedEventArgs e)
    {
        if (PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Deactivate this supplier? Existing purchase history will be kept.",
                "Supplier", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await _service.SetSupplierActiveAsync(_supplier.Id, false);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to deactivate supplier", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class PurchaseDetailsDialog : Window
{
    public PurchaseDetailsDialog(PurchaseDocument purchase)
    {
        Title = $"Purchase {purchase.DocumentNumber}";
        Width = 760;
        Height = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var summary = new TextBlock
        {
            Text = $"{purchase.DocumentNumber}   ·   {DateTimeUtilities.ToLocal(purchase.DocumentDate):dd MMM yyyy HH:mm}\n" +
                   $"Supplier: {purchase.Supplier?.Name ?? "(none)"}   ·   Reference: {purchase.ExternalReference ?? "—"}\n" +
                   $"Subtotal: {FormattingUtilities.Money(purchase.Subtotal, App.StoreSettings)}   ·   " +
                   $"Tax: {FormattingUtilities.Money(purchase.TaxTotal, App.StoreSettings)}   ·   " +
                   $"Total: {FormattingUtilities.Money(purchase.Total, App.StoreSettings)}",
            Margin = new Thickness(0, 0, 0, 14),
            FontSize = 14
        };
        root.Children.Add(summary);
        var grid = new DataGrid { ItemsSource = purchase.Items, IsReadOnly = true, AutoGenerateColumns = false };
        grid.Columns.Add(new DataGridTextColumn { Header = "Product", Binding = new System.Windows.Data.Binding("ProductName"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "SKU", Binding = new System.Windows.Data.Binding("Sku"), Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Qty", Binding = new System.Windows.Data.Binding("Quantity") { StringFormat = "0.###" }, Width = 80 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Unit cost", Binding = new System.Windows.Data.Binding("UnitCost") { StringFormat = "0.00" }, Width = 100 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Tax %", Binding = new System.Windows.Data.Binding("TaxRate") { StringFormat = "0.###" }, Width = 75 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Line total", Binding = new System.Windows.Data.Binding("LineTotal") { StringFormat = "0.00" }, Width = 105 });
        Grid.SetRow(grid, 1);
        root.Children.Add(grid);
        var close = new Button { Content = "Close", Style = (Style)FindResource("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        close.Click += (_, _) => Close();
        Grid.SetRow(close, 2);
        root.Children.Add(close);
        Content = root;
    }
}
