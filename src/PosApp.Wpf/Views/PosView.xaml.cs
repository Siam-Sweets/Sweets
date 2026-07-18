using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PosApp.Core.Entities;
using PosApp.Core.Enums;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Wpf.Helpers;
using PosApp.Core.Utilities;

namespace PosApp.Wpf.Views;

public partial class PosView : UserControl, IRefreshable
{
    private readonly DispatcherTimer _searchTimer;

    public PosView(IInventoryService inventory, ISaleService sales,
        ICustomerService customers, IRegisterService register,
        IDiscountService discounts)
    {
        InitializeComponent();
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        var vm = new PosViewModel(inventory, sales, customers, register, discounts);
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            try { await vm.FilterProductsAsync(); }
            catch (Exception ex) { ShowError(ex, "Search failed"); }
        };

        DataContext = vm;
        Loaded += (_, _) => App.SettingsChanged += SettingsChanged;
        Unloaded += (_, _) => App.SettingsChanged -= SettingsChanged;
    }

    private void SettingsChanged(object? sender, EventArgs e)
    {
        if (DataContext is PosViewModel vm) vm.ApplySettings();
    }

    public async Task RefreshAsync()
    {
        if (DataContext is not PosViewModel vm) return;
        IsEnabled = false;
        try
        {
            await vm.RefreshAsync();
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to load POS", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void BarcodeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseSearch();
            BarcodeBox.Clear();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter || DataContext is not PosViewModel vm) return;
        _searchTimer.Stop();
        try
        {
            vm.SearchText = BarcodeBox.Text;
            await vm.SearchBySkuOrTextAsync(identifierOnly: true);
            if (string.IsNullOrWhiteSpace(vm.SearchText))
            {
                BarcodeBox.Clear();
                ProductSearchBox.Clear();
                CloseSearch();
            }
            else
            {
                OpenSearch();
            }
        }
        catch (Exception ex) { ShowError(ex, "Search failed"); }
        e.Handled = true;
    }

    private void BarcodeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is not PosViewModel vm || !IsLoaded) return;

        vm.SearchText = BarcodeBox.Text;
        if (!string.IsNullOrWhiteSpace(BarcodeBox.Text))
        {
            // The always-visible receipt input doubles as the scanner target.
            // Scanner/code entry must search every identifier regardless of the
            // field filter last selected inside the F3 search panel.
            if (SearchAllFilter.IsChecked != true)
                SearchAllFilter.IsChecked = true;
            if (!string.Equals(ProductSearchBox.Text, BarcodeBox.Text, StringComparison.Ordinal))
                ProductSearchBox.Text = BarcodeBox.Text;
            OpenSearch(focusSearchBox: false);
            RestartSearchTimer();
        }
    }

    private void ProductSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is not PosViewModel vm || !IsLoaded) return;
        vm.SearchText = ProductSearchBox.Text;
        RestartSearchTimer();
    }

    private void SearchField_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string fieldName } ||
            DataContext is not PosViewModel vm ||
            !Enum.TryParse<ProductSearchField>(fieldName, out var field)) return;

        vm.SearchField = field;
        RestartSearchTimer();
        if (SearchOverlay.Visibility == Visibility.Visible)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input,
                new Action(() => ProductSearchBox.Focus()));
        }
    }

    private async void ProductSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseSearch();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter || DataContext is not PosViewModel vm) return;
        _searchTimer.Stop();
        try
        {
            await vm.SearchBySkuOrTextAsync();
            if (string.IsNullOrWhiteSpace(vm.SearchText))
            {
                BarcodeBox.Clear();
                ProductSearchBox.Clear();
                CloseSearch();
            }
        }
        catch (Exception ex) { ShowError(ex, "Search failed"); }
        e.Handled = true;
    }

    private void RestartSearchTimer()
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void OpenSearch(bool focusSearchBox = true)
    {
        SearchOverlay.Visibility = Visibility.Visible;
        if (focusSearchBox)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => ProductSearchBox.Focus()));
    }

    private void CloseSearch()
    {
        SearchOverlay.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => BarcodeBox.Focus()));
    }

    private void SearchProducts_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ProductSearchBox.Text) && !string.IsNullOrWhiteSpace(BarcodeBox.Text))
            ProductSearchBox.Text = BarcodeBox.Text;
        OpenSearch();
    }

    private void CloseSearch_Click(object sender, RoutedEventArgs e) => CloseSearch();

    private void SearchScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseSearch();

    private void VirtualKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        if (key == "Back")
        {
            if (ProductSearchBox.Text.Length > 0)
                ProductSearchBox.Text = ProductSearchBox.Text[..^1];
        }
        else if (key == "Space")
        {
            ProductSearchBox.AppendText(" ");
        }
        else
        {
            ProductSearchBox.AppendText(key);
        }
        ProductSearchBox.CaretIndex = ProductSearchBox.Text.Length;
        ProductSearchBox.Focus();
    }

    private async void Category_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && DataContext is PosViewModel vm)
        {
            var catId = (int)btn.Tag;
            try { await vm.FilterByCategoryAsync(catId); }
            catch (Exception ex) { ShowError(ex, "Category filter failed"); }
        }
    }

    private async void Product_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Product p && DataContext is PosViewModel vm)
        {
            await vm.AddToCartAsync(p);
            BarcodeBox.Clear();
            ProductSearchBox.Clear();
            CloseSearch();
        }
    }

    private SaleDraftLine? SelectedLine => ReceiptGrid.SelectedItem as SaleDraftLine;

    private void DeleteLine_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLine is { } line && DataContext is PosViewModel vm) vm.RemoveLine(line);
    }

    private void Quantity_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PosViewModel vm) return;
        var line = SelectedLine ?? vm.CartLines.LastOrDefault();
        if (line == null)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Select a receipt line first.", "Quantity", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new NumericValueDialog("Quantity", "Enter the new quantity", line.Quantity, 0.001m)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true) vm.SetQuantity(line, dialog.Value);
    }

    private async void Checkout_Click(object sender, RoutedEventArgs e)
    {
        await OpenPaymentAsync();
    }

    private async Task OpenPaymentAsync()
    {
        if (DataContext is PosViewModel vm)
            await vm.CheckoutAsync();

        BarcodeBox.Focus();
    }

    private async void PosView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not PosViewModel vm) return;

        // WPF can expose F10 as Key.System because Windows reserves it for
        // keyboard menu activation. Resolve the effective key so the POS
        // payment shortcut works consistently on physical and on-screen
        // keyboards.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Consume recognized shortcuts before awaiting any asynchronous work.
        // Setting Handled after an await is too late: the routed event may have
        // already continued and Windows can enter menu mode instead of opening
        // the payment dialog.
        if (key is not (Key.F2 or Key.F3 or Key.F4 or Key.F7 or Key.F9 or Key.F10 or Key.Delete))
        {
            return;
        }

        e.Handled = true;

        switch (key)
        {
            case Key.F2:
                Discount_Click(sender, e);
                break;
            case Key.F3:
                OpenSearch();
                break;
            case Key.F4:
                Quantity_Click(sender, e);
                break;
            case Key.F7:
                await vm.ShowSuspendedAsync();
                break;
            case Key.F9:
                await vm.SuspendAsync();
                break;
            case Key.F10:
                await OpenPaymentAsync();
                break;
            case Key.Delete:
                DeleteLine_Click(sender, e);
                break;
        }
    }

    private async void Suspend_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PosViewModel vm) await vm.SuspendAsync();
    }

    private async void Recall_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PosViewModel vm) await vm.ShowSuspendedAsync();
    }

    private void CustomerButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PosViewModel vm) vm.PickCustomer();
    }


    private void ServiceType_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PosViewModel vm) return;
        var dialog = new ChoiceDialog("Service type", new[] { "Retail", "Takeaway", "Delivery", "Dine-in" }, vm.ServiceType)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true && dialog.SelectedValue != null)
            vm.ServiceType = dialog.SelectedValue;
    }

    private async void Discount_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PosViewModel vm) return;
        var line = SelectedLine ?? vm.CartLines.LastOrDefault();
        if (line == null)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Select a receipt line first.", "Discount", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var promotions = await vm.GetActivePromotionsAsync();
        var dialog = new DiscountEntryDialog(line, promotions) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
            vm.ApplyLineDiscount(line, dialog.DiscountAmount, dialog.Reason, dialog.PromotionId);
    }

    private void Comment_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PosViewModel vm) return;
        var dialog = new TextEntryDialog("Sale comment", "Add a note to this sale", vm.Note)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true) vm.Note = dialog.Value;
    }

    private void Management_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.ToggleManagementDrawer();

    private void Refund_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.NavigateTo("sales");

    private void VoidOrder_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PosViewModel vm || vm.CartLines.Count == 0) return;
        if (!App.StoreSettings.ConfirmBeforeVoidingOrder ||
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Void the current order? No sale will be recorded.", "Void Order",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            vm.ClearCart();
    }

    private static void ShowError(Exception ex, string title)
        => PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}

public class PosViewModel : ViewModelBase
{
    private readonly IInventoryService _inventory;
    private readonly ISaleService _sales;
    private readonly ICustomerService _customers;
    private readonly IRegisterService _register;
    private readonly IDiscountService _discounts;
    private readonly SemaphoreSlim _productLoadGate = new(1, 1);
    private int _productLoadVersion;
    private int? _suspendedSaleId;
    private bool _checkoutInProgress;
    private string _serviceType;
    private string _note = string.Empty;

    public ObservableCollection<Product> Products { get; } = new();
    public ObservableCollection<Category> Categories { get; } = new();
    public ObservableCollection<SaleDraftLine> CartLines { get; } = new();
    public IReadOnlyList<string> VirtualKeys { get; } =
        "1234567890ABCDEFGHIJKLMNOPQRSTUVWXYZ".Select(character => character.ToString())
            .Concat(new[] { "Space", "Back" }).ToList();

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            // Invalidate an in-flight query immediately. Without this, the old
            // result could repaint the catalog during the debounce interval.
            Interlocked.Increment(ref _productLoadVersion);
        }
    }
    private ProductSearchField _searchField = ProductSearchField.All;
    public ProductSearchField SearchField
    {
        get => _searchField;
        set
        {
            if (!Set(ref _searchField, value)) return;
            Interlocked.Increment(ref _productLoadVersion);
        }
    }

    private int? _selectedCategoryId;
    private Customer? _selectedCustomer;
    public string CustomerDisplay => _selectedCustomer == null
        ? (string)System.Windows.Application.Current.TryFindResource("POS_WalkIn") as string ?? "Walk-in Customer"
        : _selectedCustomer.Name;

    public string ServiceType
    {
        get => _serviceType;
        set
        {
            if (!Set(ref _serviceType, string.IsNullOrWhiteSpace(value) ? "Retail" : value)) return;
            OnPropertyChanged(nameof(ServiceTypeDisplay));
            OnPropertyChanged(nameof(SaleContextDisplay));
        }
    }

    public string Note
    {
        get => _note;
        set
        {
            if (!Set(ref _note, value?.Trim() ?? string.Empty)) return;
            OnPropertyChanged(nameof(NoteDisplay));
            OnPropertyChanged(nameof(ActiveSaleDisplay));
        }
    }

    public string ServiceTypeDisplay => $"Service: {ServiceType}";
    public string SaleContextDisplay => $"{CustomerDisplay}  •  {ServiceType}";
    public string NoteDisplay => string.IsNullOrWhiteSpace(Note) ? "No sale comment" : Note;
    public string ActiveSaleDisplay => _suspendedSaleId.HasValue
        ? $"Editing saved sale #{_suspendedSaleId.Value}"
        : (string.IsNullOrWhiteSpace(Note) ? "Ready for the next item" : Note);


    public decimal Subtotal => CartLines.Sum(l => l.UnitPrice * l.Quantity);
    public decimal DiscountTotal => CartLines.Sum(l => l.DiscountAmount);
    public decimal TaxTotal => CartLines.Sum(l => ((l.UnitPrice * l.Quantity) - l.DiscountAmount) * l.TaxRate / 100m);
    public decimal Total => Subtotal - DiscountTotal + TaxTotal;
    public bool IsCartEmpty => CartLines.Count == 0;
    public bool VirtualKeyboardVisible => App.StoreSettings.EnableVirtualKeyboard;
    public double ProductCardWidth => Math.Clamp(
        (900d - Math.Clamp(App.StoreSettings.ProductGridColumns, 2, 10) * 10d) /
        Math.Clamp(App.StoreSettings.ProductGridColumns, 2, 10), 120d, 280d);
    public double ProductCardHeight => Math.Clamp(
        470d / Math.Clamp(App.StoreSettings.ProductGridRows, 2, 10), 126d, 150d);

    public PosViewModel(IInventoryService inventory, ISaleService sales,
        ICustomerService customers, IRegisterService register,
        IDiscountService discounts)
    {
        _inventory = inventory;
        _sales = sales;
        _customers = customers;
        _register = register;
        _discounts = discounts;
        _serviceType = string.IsNullOrWhiteSpace(App.StoreSettings.DefaultServiceType)
            ? "Retail" : App.StoreSettings.DefaultServiceType;
    }

    public void ApplySettings()
    {
        ServiceType = string.IsNullOrWhiteSpace(App.StoreSettings.DefaultServiceType)
            ? "Retail" : App.StoreSettings.DefaultServiceType;
        OnPropertyChanged(nameof(VirtualKeyboardVisible));
        OnPropertyChanged(nameof(ProductCardWidth));
        OnPropertyChanged(nameof(ProductCardHeight));
        OnPropertyChanged(nameof(Subtotal));
        OnPropertyChanged(nameof(DiscountTotal));
        OnPropertyChanged(nameof(TaxTotal));
        OnPropertyChanged(nameof(Total));
    }

    public async Task LoadAsync()
    {
        ApplySettings();
        await LoadCategoriesAsync();
        await LoadProductsAsync();
    }

    public Task RefreshAsync() => LoadAsync();

    private async Task LoadCategoriesAsync()
    {
        await _productLoadGate.WaitAsync();
        try
        {
            var cats = await _inventory.ListCategoriesAsync();
            Categories.Clear();
            Categories.Add(new Category { Id = 0, Name = (string)System.Windows.Application.Current.TryFindResource("POS_All") as string ?? "All" });
            foreach (var c in cats) Categories.Add(c);
        }
        finally
        {
            _productLoadGate.Release();
        }
    }

    private async Task LoadProductsAsync()
    {
        var version = Interlocked.Increment(ref _productLoadVersion);
        var searchText = SearchText;
        var searchField = SearchField;
        var categoryId = _selectedCategoryId == 0 ? null : _selectedCategoryId;

        await _productLoadGate.WaitAsync();
        try
        {
            if (version != _productLoadVersion) return;
            var products = await _inventory.SearchProductsAsync(searchText, categoryId, searchField);
            if (version != _productLoadVersion ||
                !string.Equals(searchText, SearchText, StringComparison.Ordinal) ||
                searchField != SearchField) return;

            Products.Clear();
            foreach (var p in products) Products.Add(p);
        }
        finally
        {
            _productLoadGate.Release();
        }
    }

    public async Task FilterByCategoryAsync(int categoryId)
    {
        _selectedCategoryId = categoryId;
        await LoadProductsAsync();
    }

    public Task FilterProductsAsync() => LoadProductsAsync();

    public async Task SearchBySkuOrTextAsync(bool identifierOnly = false)
    {
        var term = SearchText?.Trim() ?? "";
        if (string.IsNullOrEmpty(term)) { await LoadProductsAsync(); return; }

        // Scanner entry always tries both identifiers. Inside F3 search, exact
        // auto-add respects the selected Code/Barcode filter; Name filtering
        // only narrows the results and never auto-adds a similarly named item.
        Product? p = null;
        var exactField = identifierOnly ? ProductSearchField.All : SearchField;
        if (exactField != ProductSearchField.Name)
        {
            await _productLoadGate.WaitAsync();
            try
            {
                p = await _inventory.GetProductBySkuAsync(term);
            }
            finally
            {
                _productLoadGate.Release();
            }

            var matchesSelectedField = p != null && (exactField switch
            {
                ProductSearchField.Code => string.Equals(p.Sku, term,
                    StringComparison.OrdinalIgnoreCase),
                ProductSearchField.Barcode => string.Equals(p.Barcode, term,
                    StringComparison.OrdinalIgnoreCase),
                _ => true
            });
            if (p != null && matchesSelectedField)
            {
                await AddToCartAsync(p);
                SearchText = "";
                return;
            }
        }

        await LoadProductsAsync();
    }

    public Task AddToCartAsync(Product product)
    {
        // Weighted products remain supported through decimal quantities.
        // Quantity can be entered with F4; no weighing-scale hardware is required.
        const decimal qty = 1m;

        var existing = CartLines.FirstOrDefault(l => l.ProductId == product.Id);
        if (existing != null)
        {
            existing.Quantity += qty;
        }
        else
        {
            CartLines.Add(new SaleDraftLine
            {
                ProductId = product.Id,
                ProductName = product.Name,
                Sku = product.Sku,
                Quantity = qty,
                UnitPrice = product.Price,
                CostPrice = product.CostPrice,
                TaxRate = product.TaxRate,
                AllowDiscount = product.AllowDiscount,
                IsWeighted = product.IsWeighted
            });
        }
        RecalcTotals();
        return Task.CompletedTask;
    }

    public void ChangeQty(SaleDraftLine line, decimal delta)
    {
        var newQty = line.Quantity + delta;
        if (newQty <= 0) { RemoveLine(line); return; }
        line.Quantity = newQty;
        RecalcTotals();
    }

    public void SetQuantity(SaleDraftLine line, decimal quantity)
    {
        if (quantity <= 0)
        {
            RemoveLine(line);
            return;
        }
        line.Quantity = quantity;
        if (line.DiscountAmount > line.UnitPrice * line.Quantity)
            line.DiscountAmount = line.UnitPrice * line.Quantity;
        RecalcTotals();
    }

    public void ApplyLineDiscount(SaleDraftLine line, decimal amount, string? reason, int? promotionId = null)
    {
        if (!line.AllowDiscount && amount > 0m)
            throw new InvalidOperationException($"Discounts are disabled for {line.ProductName}.");
        var lineGross = line.UnitPrice * line.Quantity;
        line.DiscountAmount = Math.Clamp(amount, 0m, lineGross);
        line.DiscountReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        line.PromotionId = line.DiscountAmount > 0m ? promotionId : null;
        RecalcTotals();
    }

    public void RemoveLine(SaleDraftLine line)
    {
        CartLines.Remove(line);
        RecalcTotals();
    }

    public void ClearCart()
    {
        CartLines.Clear();
        _selectedCustomer = null;
        _suspendedSaleId = null;
        Note = string.Empty;
        ServiceType = string.IsNullOrWhiteSpace(App.StoreSettings.DefaultServiceType)
            ? "Retail" : App.StoreSettings.DefaultServiceType;
        OnPropertyChanged(nameof(CustomerDisplay));
        OnPropertyChanged(nameof(SaleContextDisplay));
        OnPropertyChanged(nameof(ActiveSaleDisplay));
        RecalcTotals();
    }

    private void RecalcTotals()
    {
        OnPropertyChanged(nameof(Subtotal));
        OnPropertyChanged(nameof(DiscountTotal));
        OnPropertyChanged(nameof(TaxTotal));
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(IsCartEmpty));
        OnPropertyChanged(nameof(ActiveSaleDisplay));
    }

    public void PickCustomer()
    {
        var dlg = new CustomerPickerDialog(_customers) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() == true && dlg.SelectedCustomer != null)
        {
            _selectedCustomer = dlg.SelectedCustomer;
            OnPropertyChanged(nameof(CustomerDisplay));
            OnPropertyChanged(nameof(SaleContextDisplay));
        }
    }

    public async Task CheckoutAsync()
    {
        if (_checkoutInProgress || CartLines.Count == 0 || App.CurrentUser == null) return;
        if (!await EnsureRegisterReadyAsync()) return;

        var draft = BuildDraft();
        var paymentDialog = new PaymentDialog();
        paymentDialog.Configure(draft);
        var owner = Application.Current.Windows
            .OfType<MainWindow>()
            .FirstOrDefault(window => window.IsVisible);
        if (owner != null) paymentDialog.Owner = owner;
        if (paymentDialog.ShowDialog() != true) return;

        draft.AmountTendered = paymentDialog.TenderedAmount;
        draft.Payments = paymentDialog.Payments
            .Select(payment => new SalePayment
            {
                Method = payment.Method,
                Amount = payment.Amount,
                Reference = payment.Reference
            })
            .ToList();

        await CompleteCheckoutAsync(draft);
    }


    private async Task<bool> EnsureRegisterReadyAsync()
    {
        if (!App.StoreSettings.RequireOpenRegisterForSales) return true;
        var session = await _register.GetOpenSessionAsync();
        if (session != null) return true;
        PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Open the cash register before completing a sale.", "Register Required",
            MessageBoxButton.OK, MessageBoxImage.Information);
        (Application.Current.MainWindow as MainWindow)?.NavigateTo("register");
        return false;
    }

    public Task<IReadOnlyList<Discount>> GetActivePromotionsAsync() => _discounts.GetActiveAsync();

    private async Task CompleteCheckoutAsync(SaleDraft draft)
    {
        _checkoutInProgress = true;

        try
        {
            var sale = await _sales.CheckoutAsync(draft);

            ClearCart();
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show($"Sale completed. Receipt: {sale.ReceiptNumber}\n" +
                            $"Received: {FormattingUtilities.Money(sale.AmountPaid, App.StoreSettings)}\n" +
                            $"Change: {FormattingUtilities.Money(sale.Change, App.StoreSettings)}\n\n" +
                            "The receipt is available in Sales History if you want to print it.",
                "Sale Completed", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            App.LogError("CHECKOUT FAILED", ex);
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(
                "Checkout could not be saved. No sale or stock changes were committed.\n\n" +
                $"{ex.GetBaseException().Message}\n\n" +
                $"Technical details were written to:\n{App.LogFilePath}",
                "Checkout failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _checkoutInProgress = false;
        }
    }

    public async Task SuspendAsync()
    {
        if (CartLines.Count == 0 || App.CurrentUser == null) return;
        var draft = BuildDraft();
        try
        {
            await _sales.SuspendAsync(draft);
            ClearCart();
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Sale suspended. Use Recall to continue later.", "Suspended",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Suspend failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public async Task ShowSuspendedAsync()
    {
        var suspended = await _sales.GetSuspendedSalesAsync();
        if (suspended.Count == 0)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("No suspended sales to recall.", "Recall", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new SuspendedSalesDialog(suspended, _sales) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() == true && dlg.SelectedSale != null)
        {
            var catalog = (await _inventory.SearchProductsAsync(null, includeInactive: true))
                .ToDictionary(product => product.Id);

            ClearCart();
            foreach (var item in dlg.SelectedSale.Items)
            {
                catalog.TryGetValue(item.ProductId, out var product);
                CartLines.Add(new SaleDraftLine
                {
                    ProductId = item.ProductId,
                    ProductName = item.ProductName,
                    Sku = item.Sku,
                    Quantity = item.Quantity,
                    UnitPrice = item.UnitPrice,
                    CostPrice = item.CostPrice,
                    TaxRate = item.TaxRate,
                    DiscountAmount = item.DiscountAmount,
                    DiscountReason = item.DiscountReason,
                    PromotionId = item.PromotionId,
                    AllowDiscount = product?.AllowDiscount ?? true,
                    IsWeighted = product?.IsWeighted ?? false
                });
            }
            _suspendedSaleId = dlg.SelectedSale.Id;
            _selectedCustomer = dlg.SelectedSale.Customer;
            var recalledNote = dlg.SelectedSale.Note ?? string.Empty;
            var recalledServiceType = string.IsNullOrWhiteSpace(dlg.SelectedSale.ServiceType)
                ? "Retail" : dlg.SelectedSale.ServiceType;
            // Older versions embedded the service type in the note. Strip every
            // legacy prefix during recall so saving again cannot duplicate it.
            while (recalledNote.StartsWith("Service type:", StringComparison.OrdinalIgnoreCase))
            {
                var lineBreak = recalledNote.IndexOfAny(new[] { '\r', '\n' });
                var firstLine = lineBreak < 0 ? recalledNote : recalledNote[..lineBreak];
                var legacyType = firstLine["Service type:".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(legacyType) &&
                    (string.IsNullOrWhiteSpace(dlg.SelectedSale.ServiceType) ||
                     string.Equals(recalledServiceType, "Retail", StringComparison.OrdinalIgnoreCase)))
                    recalledServiceType = legacyType;
                recalledNote = lineBreak < 0
                    ? string.Empty
                    : recalledNote[(lineBreak + 1)..].TrimStart('\r', '\n');
            }
            Note = recalledNote;
            ServiceType = recalledServiceType;
            OnPropertyChanged(nameof(CustomerDisplay));
            OnPropertyChanged(nameof(SaleContextDisplay));
            OnPropertyChanged(nameof(ActiveSaleDisplay));
            RecalcTotals();
        }
    }

    private SaleDraft BuildDraft()
    {
        return new SaleDraft
        {
            UserId = App.CurrentUser!.Id,
            CustomerId = _selectedCustomer?.Id,
            Lines = CartLines.ToList(),
            Note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim(),
            ServiceType = ServiceType,
            SuspendedSaleId = _suspendedSaleId
        };
    }
}

public class CustomerPickerDialog : Window
{
    public Customer? SelectedCustomer { get; private set; }
    private readonly ICustomerService _svc;
    private readonly ObservableCollection<Customer> _list = new();
    private readonly TextBox _search;
    private readonly DispatcherTimer _searchTimer;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private int _reloadVersion;

    public CustomerPickerDialog(ICustomerService svc)
    {
        _svc = svc;
        Title = "Select Customer";
        Width = 500; Height = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BackgroundBrush");

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await Reload();
        };

        _search = new TextBox { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(10, 8, 10, 8) };
        _search.TextChanged += (_, _) =>
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        };
        grid.Children.Add(_search); Grid.SetRow(_search, 0);

        var list = new ListBox
        {
            ItemsSource = _list,
            Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("CardBrush"),
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextDarkBrush")
        };
        list.DisplayMemberPath = "Name";
        grid.Children.Add(list); Grid.SetRow(list, 1);
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is Customer c) SelectedCustomer = c;
        };
        list.MouseDoubleClick += (_, _) => { if (SelectedCustomer != null) { DialogResult = true; Close(); } };

        var okBtn = new Button
        {
            Content = "Select",
            Style = (Style)System.Windows.Application.Current.FindResource("PrimaryButton"),
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(0, 10, 0, 10)
        };
        okBtn.Click += (_, _) => { if (SelectedCustomer != null) { DialogResult = true; Close(); } };
        grid.Children.Add(okBtn); Grid.SetRow(okBtn, 2);

        Content = grid;
        Loaded += async (_, _) => await Reload();
    }

    private async Task Reload()
    {
        var version = ++_reloadVersion;
        var term = _search.Text?.Trim();
        await _reloadGate.WaitAsync();
        try
        {
            if (version != _reloadVersion) return;
            var items = await _svc.SearchCustomersAsync(term);
            if (version != _reloadVersion) return;
            _list.Clear();
            foreach (var c in items) _list.Add(c);
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Customer search failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _reloadGate.Release();
        }
    }
}

public class SuspendedSalesDialog : Window
{
    public Sale? SelectedSale { get; private set; }
    private readonly ISaleService _svc;
    private readonly ObservableCollection<Sale> _list = new();

    public SuspendedSalesDialog(System.Collections.Generic.IReadOnlyList<Sale> suspended, ISaleService svc)
    {
        _svc = svc;
        Title = "Recall Suspended Sale";
        Width = 540; Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BackgroundBrush");

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var list = new ListBox
        {
            ItemsSource = _list,
            Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("CardBrush"),
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextDarkBrush")
        };
        list.DisplayMemberPath = "ReceiptNumber";
        grid.Children.Add(list); Grid.SetRow(list, 0);
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is Sale s) SelectedSale = s;
        };
        list.MouseDoubleClick += (_, _) => { if (SelectedSale != null) { DialogResult = true; Close(); } };

        var recallBtn = new Button
        {
            Content = "Recall",
            Style = (Style)System.Windows.Application.Current.FindResource("SuccessButton"),
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(0, 10, 0, 10)
        };
        recallBtn.Click += (_, _) => { if (SelectedSale != null) { DialogResult = true; Close(); } };
        grid.Children.Add(recallBtn); Grid.SetRow(recallBtn, 1);

        Content = grid;
        foreach (var s in suspended) _list.Add(s);
    }
}

public class NumericValueDialog : Window
{
    private readonly TextBox _valueBox;
    private readonly decimal _minimum;
    public decimal Value { get; private set; }

    public NumericValueDialog(string title, string prompt, decimal value, decimal minimum = 0m)
    {
        Title = title;
        Width = 390;
        Height = 235;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BackgroundBrush");
        _minimum = minimum;
        Value = value;

        var grid = DialogLayout.CreateRoot();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock
        {
            Text = prompt,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextDarkBrush"),
            FontSize = 15,
            Margin = new Thickness(0, 0, 0, 10)
        };
        grid.Children.Add(label);

        _valueBox = new TextBox
        {
            Text = value.ToString("0.###", CultureInfo.CurrentCulture),
            Padding = new Thickness(10, 8, 10, 8),
            FontSize = 20
        };
        Grid.SetRow(_valueBox, 1);
        grid.Children.Add(_valueBox);
        grid.Children.Add(DialogLayout.CreateButtons(2, Accept, () => Close()));
        Content = grid;
        Loaded += (_, _) => { _valueBox.Focus(); _valueBox.SelectAll(); };
    }

    private void Accept()
    {
        if (!DialogLayout.TryParseDecimal(_valueBox.Text, out var value) || value < _minimum)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show($"Enter a value of at least {_minimum:0.###}.", Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Value = value;
        DialogResult = true;
        Close();
    }
}

public class TextEntryDialog : Window
{
    private readonly TextBox _textBox;
    public string Value { get; private set; }

    public TextEntryDialog(string title, string prompt, string? value = null)
    {
        Title = title;
        Width = 520;
        Height = 300;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BackgroundBrush");
        Value = value ?? string.Empty;

        var grid = DialogLayout.CreateRoot();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock
        {
            Text = prompt,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextDarkBrush"),
            FontSize = 15,
            Margin = new Thickness(0, 0, 0, 10)
        };
        grid.Children.Add(label);
        _textBox = new TextBox
        {
            Text = Value,
            Padding = new Thickness(10),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(_textBox, 1);
        grid.Children.Add(_textBox);
        grid.Children.Add(DialogLayout.CreateButtons(2, () =>
        {
            Value = _textBox.Text.Trim();
            DialogResult = true;
            Close();
        }, () => Close()));
        Content = grid;
        Loaded += (_, _) => _textBox.Focus();
    }
}

public class ChoiceDialog : Window
{
    private readonly ListBox _list;
    public string? SelectedValue { get; private set; }

    public ChoiceDialog(string title, IEnumerable<string> choices, string? selected = null)
    {
        Title = title;
        Width = 420;
        Height = 360;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BackgroundBrush");

        var grid = DialogLayout.CreateRoot();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _list = new ListBox
        {
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("CardBrush"),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextDarkBrush"),
            FontSize = 16,
            Padding = new Thickness(6)
        };
        foreach (var choice in choices) _list.Items.Add(choice);
        _list.SelectedItem = selected;
        _list.MouseDoubleClick += (_, _) => Accept();
        grid.Children.Add(_list);
        grid.Children.Add(DialogLayout.CreateButtons(1, Accept, () => Close()));
        Content = grid;
    }

    private void Accept()
    {
        if (_list.SelectedItem is not string value) return;
        SelectedValue = value;
        DialogResult = true;
        Close();
    }
}

public class DiscountEntryDialog : Window
{
    private readonly SaleDraftLine _line;
    private readonly RadioButton _percentage;
    private readonly TextBox _valueBox;
    private readonly TextBox _reasonBox;
    private readonly ComboBox _promotionBox;
    public decimal DiscountAmount { get; private set; }
    public string? Reason { get; private set; }
    public int? PromotionId { get; private set; }

    public DiscountEntryDialog(SaleDraftLine line, IReadOnlyList<Discount>? promotions = null)
    {
        _line = line;
        Title = DialogLayout.Text("POS_LineDiscount", "Line discount");
        Width = 520;
        Height = 510;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BackgroundBrush");

        var root = DialogLayout.CreateRoot();
        for (var i = 0; i < 6; i++)
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var lineLabel = new TextBlock
        {
            Text = $"{line.ProductName}  •  {FormattingUtilities.Money(line.UnitPrice * line.Quantity, App.StoreSettings)}",
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextDarkBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
            Margin = new Thickness(0, 0, 0, 12)
        };
        root.Children.Add(lineLabel);

        var promotionPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        promotionPanel.Children.Add(DialogLayout.CreateFieldLabel(
            "POS_DiscountPromotion", "Promotion (optional)"));
        _promotionBox = new ComboBox
        {
            Padding = new Thickness(10, 8, 10, 8),
            MinHeight = 42
        };
        _promotionBox.Items.Add(new PromotionChoice(null));
        if (promotions != null)
        {
            foreach (var promotion in promotions) _promotionBox.Items.Add(new PromotionChoice(promotion));
        }
        _promotionBox.SelectedIndex = 0;
        _promotionBox.SelectionChanged += (_, _) => ApplyPromotionChoice();
        promotionPanel.Children.Add(_promotionBox);
        Grid.SetRow(promotionPanel, 1);
        root.Children.Add(promotionPanel);

        var typeSection = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        typeSection.Children.Add(DialogLayout.CreateFieldLabel(
            "POS_DiscountType", "Discount type"));
        var typePanel = new StackPanel { Orientation = Orientation.Horizontal };
        _percentage = new RadioButton
        {
            Content = DialogLayout.Text("POS_DiscountPercentage", "Percentage"),
            IsChecked = true,
            Margin = new Thickness(0, 0, 24, 0)
        };
        _percentage.GroupName = "DiscountType";
        typePanel.Children.Add(_percentage);
        typePanel.Children.Add(new RadioButton
        {
            Content = DialogLayout.Text("POS_DiscountFixed", "Fixed amount"),
            GroupName = "DiscountType"
        });
        typeSection.Children.Add(typePanel);
        Grid.SetRow(typeSection, 2);
        root.Children.Add(typeSection);

        var valuePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        valuePanel.Children.Add(DialogLayout.CreateFieldLabel(
            "POS_DiscountValue", "Discount value"));
        _valueBox = new TextBox
        {
            Padding = new Thickness(10, 8, 10, 8),
            FontSize = 18,
            MinHeight = 44
        };
        valuePanel.Children.Add(_valueBox);
        Grid.SetRow(valuePanel, 3);
        root.Children.Add(valuePanel);

        var reasonPanel = new StackPanel();
        reasonPanel.Children.Add(DialogLayout.CreateFieldLabel(
            "POS_DiscountReason", "Reason (optional)"));
        _reasonBox = new TextBox
        {
            Padding = new Thickness(10, 8, 10, 8),
            MinHeight = 40,
            Text = line.DiscountReason ?? string.Empty
        };
        reasonPanel.Children.Add(_reasonBox);
        Grid.SetRow(reasonPanel, 4);
        root.Children.Add(reasonPanel);
        root.Children.Add(DialogLayout.CreateButtons(5, Accept, () => Close()));
        Content = root;
        Loaded += (_, _) => _valueBox.Focus();
    }

    private void ApplyPromotionChoice()
    {
        if (_promotionBox.SelectedItem is not PromotionChoice { Discount: { } promotion }) return;
        _percentage.IsChecked = promotion.Type == DiscountType.Percentage;
        _valueBox.Text = promotion.Value.ToString("0.##");
        _reasonBox.Text = promotion.Name;
    }

    private void Accept()
    {
        if (!DialogLayout.TryParseDecimal(_valueBox.Text, out var value) || value < 0m)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(DialogLayout.Text("POS_DiscountInvalid", "Enter a valid non-negative discount."), Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var gross = _line.UnitPrice * _line.Quantity;
        if (_percentage.IsChecked == true && value > 100m)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(DialogLayout.Text("POS_DiscountPercentLimit", "Percentage cannot exceed 100."), Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DiscountAmount = _percentage.IsChecked == true ? gross * value / 100m : value;
        if (DiscountAmount > gross)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(DialogLayout.Text("POS_DiscountAmountLimit", "Discount cannot exceed the line amount."), Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Reason = _reasonBox.Text.Trim();
        PromotionId = (_promotionBox.SelectedItem as PromotionChoice)?.Discount?.Id;
        DialogResult = true;
        Close();
    }
}

public sealed class PromotionChoice
{
    public PromotionChoice(Discount? discount) => Discount = discount;
    public Discount? Discount { get; }
    public override string ToString() => Discount == null
        ? DialogLayout.Text("POS_CustomDiscount", "Custom discount") :
        string.IsNullOrWhiteSpace(Discount.Code) ? Discount.Name : $"{Discount.Name} ({Discount.Code})";
}

internal static class DialogLayout
{
    public static Grid CreateRoot() => new()
    {
        Margin = new Thickness(18),
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BackgroundBrush")
    };

    public static string Text(string key, string fallback)
        => Application.Current.TryFindResource(key) as string ?? fallback;

    public static TextBlock CreateFieldLabel(string key, string fallback) => new()
    {
        Text = Text(key, fallback),
        Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextMutedBrush"),
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 6)
    };

    public static FrameworkElement CreateButtons(int row, Action accept, Action cancel)
    {
        var panel = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var cancelButton = new Button
        {
            Content = Text("Common_Cancel", "Cancel"),
            Style = (Style)Application.Current.FindResource("OutlineButton"),
            Margin = new Thickness(0, 0, 5, 0),
            Padding = new Thickness(0, 10, 0, 10),
            IsCancel = true
        };
        cancelButton.Click += (_, _) => cancel();
        var acceptButton = new Button
        {
            Content = Text("Common_Confirm", "Confirm"),
            Style = (Style)Application.Current.FindResource("PrimaryButton"),
            Margin = new Thickness(5, 0, 0, 0),
            Padding = new Thickness(0, 10, 0, 10),
            IsDefault = true
        };
        acceptButton.Click += (_, _) => accept();
        panel.Children.Add(cancelButton);
        panel.Children.Add(acceptButton);
        Grid.SetColumn(acceptButton, 1);
        Grid.SetRow(panel, row);
        return panel;
    }

    public static bool TryParseDecimal(string? text, out decimal value)
        => decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value) ||
           decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
}
