using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Wpf.Helpers;

namespace PosApp.Wpf.Views;

public partial class PosView : UserControl, IRefreshable
{
    private readonly DispatcherTimer _searchTimer;

    public PosView(IInventoryService inventory, ISaleService sales,
        ICustomerService customers, IHardwareService hardware)
    {
        InitializeComponent();
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        var vm = new PosViewModel(inventory, sales, customers, hardware);
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            try { await vm.FilterProductsAsync(); }
            catch (Exception ex) { ShowError(ex, "Search failed"); }
        };

        DataContext = vm;
    }

    public async void Refresh()
    {
        if (DataContext is not PosViewModel vm) return;
        IsEnabled = false;
        try
        {
            await vm.RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to load POS", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchBox.Clear();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && DataContext is PosViewModel vm)
        {
            _searchTimer.Stop();
            try { await vm.SearchBySkuOrTextAsync(); }
            catch (Exception ex) { ShowError(ex, "Search failed"); }
            e.Handled = true;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is not PosViewModel vm) return;
        vm.SearchText = SearchBox.Text;
        if (!IsLoaded) return;
        _searchTimer.Stop();
        _searchTimer.Start();
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
        }
    }

    private void QtyPlus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SaleDraftLine line && DataContext is PosViewModel vm)
            vm.ChangeQty(line, +1);
    }

    private void QtyMinus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SaleDraftLine line && DataContext is PosViewModel vm)
            vm.ChangeQty(line, -1);
    }

    private void RemoveLine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SaleDraftLine line && DataContext is PosViewModel vm)
            vm.RemoveLine(line);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PosViewModel vm) vm.ClearCart();
    }

    private async void Checkout_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PosViewModel vm) await vm.CheckoutAsync();
        SearchBox.Focus();
    }

    private async void QuickCash_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PosViewModel vm) await vm.QuickCashCheckoutAsync();
        SearchBox.Focus();
    }

    private async void PosView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not PosViewModel vm || vm.CartLines.Count == 0) return;

        if (e.Key == Key.F10)
        {
            await vm.CheckoutAsync();
            SearchBox.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.F12)
        {
            await vm.QuickCashCheckoutAsync();
            SearchBox.Focus();
            e.Handled = true;
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

    private void Customer_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is PosViewModel vm) vm.PickCustomer();
    }

    private async void Weigh_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PosViewModel vm) await vm.WeighLastAsync();
    }

    private static void ShowError(Exception ex, string title)
        => MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}

public class PosViewModel : ViewModelBase
{
    private readonly IInventoryService _inventory;
    private readonly ISaleService _sales;
    private readonly ICustomerService _customers;
    private readonly IHardwareService _hardware;
    private readonly SemaphoreSlim _productLoadGate = new(1, 1);
    private int _productLoadVersion;
    private int? _suspendedSaleId;
    private bool _checkoutInProgress;

    public ObservableCollection<Product> Products { get; } = new();
    public ObservableCollection<Category> Categories { get; } = new();
    public ObservableCollection<SaleDraftLine> CartLines { get; } = new();

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
            OnPropertyChanged(nameof(SearchPlaceholderVisible));
        }
    }
    public bool SearchPlaceholderVisible => string.IsNullOrEmpty(SearchText);

    private int? _selectedCategoryId;
    private Customer? _selectedCustomer;
    public string CustomerDisplay => _selectedCustomer == null
        ? (string)System.Windows.Application.Current.TryFindResource("POS_WalkIn") as string ?? "Walk-in Customer"
        : _selectedCustomer.Name;

    private bool _isScaleConnected;
    public bool IsScaleConnected { get => _isScaleConnected; set => Set(ref _isScaleConnected, value); }

    public decimal Subtotal => CartLines.Sum(l => l.UnitPrice * l.Quantity);
    public decimal DiscountTotal => CartLines.Sum(l => l.DiscountAmount);
    public decimal TaxTotal => CartLines.Sum(l => ((l.UnitPrice * l.Quantity) - l.DiscountAmount) * l.TaxRate / 100m);
    public decimal Total => Subtotal - DiscountTotal + TaxTotal;

    public PosViewModel(IInventoryService inventory, ISaleService sales,
        ICustomerService customers, IHardwareService hardware)
    {
        _inventory = inventory;
        _sales = sales;
        _customers = customers;
        _hardware = hardware;
    }

    public async Task LoadAsync()
    {
        await LoadCategoriesAsync();
        await LoadProductsAsync();
        IsScaleConnected = await _hardware.IsScaleConnected();
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
        var categoryId = _selectedCategoryId == 0 ? null : _selectedCategoryId;

        await _productLoadGate.WaitAsync();
        try
        {
            if (version != _productLoadVersion) return;
            var products = await _inventory.SearchProductsAsync(searchText, categoryId);
            if (version != _productLoadVersion ||
                !string.Equals(searchText, SearchText, StringComparison.Ordinal)) return;

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

    public async Task SearchBySkuOrTextAsync()
    {
        var term = SearchText?.Trim() ?? "";
        if (string.IsNullOrEmpty(term)) { await LoadProductsAsync(); return; }

        // Try exact SKU/barcode match first - this is the scanner case.
        Product? p;
        await _productLoadGate.WaitAsync();
        try
        {
            p = await _inventory.GetProductBySkuAsync(term);
        }
        finally
        {
            _productLoadGate.Release();
        }
        if (p != null)
        {
            await AddToCartAsync(p);
            SearchText = "";
            return;
        }

        await LoadProductsAsync();
    }

    public async Task AddToCartAsync(Product product)
    {
        // Weighted items: read scale if connected
        decimal qty = 1m;
        if (product.IsWeighted && await _hardware.IsScaleConnected())
        {
            var w = await _hardware.ReadScaleAsync();
            if (w.HasValue && w.Value > 0) qty = w.Value;
        }

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
                TaxRate = product.TaxRate,
                IsWeighted = product.IsWeighted
            });
        }
        RecalcTotals();
    }

    public void ChangeQty(SaleDraftLine line, decimal delta)
    {
        var newQty = line.Quantity + delta;
        if (newQty <= 0) { RemoveLine(line); return; }
        line.Quantity = newQty;
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
        OnPropertyChanged(nameof(CustomerDisplay));
        RecalcTotals();
    }

    private void RecalcTotals()
    {
        OnPropertyChanged(nameof(Subtotal));
        OnPropertyChanged(nameof(DiscountTotal));
        OnPropertyChanged(nameof(TaxTotal));
        OnPropertyChanged(nameof(Total));
    }

    public void PickCustomer()
    {
        var dlg = new CustomerPickerDialog(_customers) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() == true && dlg.SelectedCustomer != null)
        {
            _selectedCustomer = dlg.SelectedCustomer;
            OnPropertyChanged(nameof(CustomerDisplay));
        }
    }

    public async Task WeighLastAsync()
    {
        var last = CartLines.LastOrDefault(l => l.IsWeighted);
        if (last == null)
        {
            MessageBox.Show("Add a weighted product first, then press Weigh.", "Weigh",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var w = await _hardware.ReadScaleAsync();
        if (!w.HasValue) { MessageBox.Show("Scale not responding.", "Weigh", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        last.Quantity = w.Value;
        RecalcTotals();
    }

    public async Task CheckoutAsync()
    {
        if (_checkoutInProgress || CartLines.Count == 0 || App.CurrentUser == null) return;

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

    public async Task QuickCashCheckoutAsync()
    {
        if (_checkoutInProgress || CartLines.Count == 0 || App.CurrentUser == null) return;

        var draft = BuildDraft();
        draft.AmountTendered = draft.Total;
        draft.Payments = new List<SalePayment>
        {
            new() { Method = PaymentMethod.Cash, Amount = draft.Total }
        };

        await CompleteCheckoutAsync(draft);
    }

    private async Task CompleteCheckoutAsync(SaleDraft draft)
    {
        _checkoutInProgress = true;

        try
        {
            var sale = await _sales.CheckoutAsync(draft);

            // Hardware: print receipt + open drawer
            var store = App.StoreSettings;
            if (store.PrintReceiptAutomatically)
                _ = await _hardware.PrintReceiptAsync(sale);
            if (store.OpenDrawerOnCashSale && draft.Payments.Any(p => p.Method == PaymentMethod.Cash))
                _ = await _hardware.OpenCashDrawerAsync();

            ClearCart();
            MessageBox.Show($"Sale completed. Receipt: {sale.ReceiptNumber}\n" +
                            $"Received: {App.StoreSettings.CurrencySymbol} {sale.AmountPaid:0.00}\n" +
                            $"Change: {App.StoreSettings.CurrencySymbol} {sale.Change:0.00}",
                "Sale Completed", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Checkout failed", MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show("Sale suspended. Use Recall to continue later.", "Suspended",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Suspend failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public async Task ShowSuspendedAsync()
    {
        var suspended = await _sales.GetSuspendedSalesAsync();
        if (suspended.Count == 0)
        {
            MessageBox.Show("No suspended sales to recall.", "Recall", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new SuspendedSalesDialog(suspended, _sales) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() == true && dlg.SelectedSale != null)
        {
            ClearCart();
            foreach (var item in dlg.SelectedSale.Items)
            {
                CartLines.Add(new SaleDraftLine
                {
                    ProductId = item.ProductId,
                    ProductName = item.ProductName,
                    Sku = item.Sku,
                    Quantity = item.Quantity,
                    UnitPrice = item.UnitPrice,
                    TaxRate = item.TaxRate,
                    DiscountAmount = item.DiscountAmount,
                    DiscountReason = item.DiscountReason
                });
            }
            _suspendedSaleId = dlg.SelectedSale.Id;
            _selectedCustomer = dlg.SelectedSale.Customer;
            OnPropertyChanged(nameof(CustomerDisplay));
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
            MessageBox.Show(ex.Message, "Customer search failed", MessageBoxButton.OK, MessageBoxImage.Error);
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
