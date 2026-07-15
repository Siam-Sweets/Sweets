using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Wpf.Helpers;

namespace PosApp.Wpf.Views;

public partial class PosView : UserControl, IRefreshable
{
    public PosView(IInventoryService inventory, ISaleService sales,
        ICustomerService customers, IHardwareService hardware,
        PaymentDialog paymentDialog)
    {
        InitializeComponent();
        var vm = new PosViewModel(inventory, sales, customers, hardware, paymentDialog);
        DataContext = vm;
        Loaded += async (_, _) => await vm.LoadAsync();
    }

    public void Refresh()
    {
        if (DataContext is PosViewModel vm) vm.Refresh();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is PosViewModel vm)
        {
            vm.SearchBySkuOrText();
        }
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PosViewModel vm) vm.SearchBySkuOrText();
    }

    private void Category_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && DataContext is PosViewModel vm)
        {
            var catId = (int)btn.Tag;
            vm.FilterByCategory(catId);
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
    }

    private void Suspend_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PosViewModel vm) vm.Suspend();
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
}

public class PosViewModel : ViewModelBase
{
    private readonly IInventoryService _inventory;
    private readonly ISaleService _sales;
    private readonly ICustomerService _customers;
    private readonly IHardwareService _hardware;
    private readonly PaymentDialog _paymentDialog;

    public ObservableCollection<Product> Products { get; } = new();
    public ObservableCollection<Category> Categories { get; } = new();
    public ObservableCollection<SaleDraftLine> CartLines { get; } = new();

    private string _searchText = "";
    public string SearchText { get => _searchText; set { Set(ref _searchText, value); OnPropertyChanged(nameof(SearchPlaceholderVisible)); } }
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
        ICustomerService customers, IHardwareService hardware,
        PaymentDialog paymentDialog)
    {
        _inventory = inventory;
        _sales = sales;
        _customers = customers;
        _hardware = hardware;
        _paymentDialog = paymentDialog;
    }

    public async Task LoadAsync()
    {
        await LoadCategoriesAsync();
        await LoadProductsAsync();
        IsScaleConnected = await _hardware.IsScaleConnected();
    }

    public void Refresh()
    {
        _ = LoadProductsAsync();
    }

    private async Task LoadCategoriesAsync()
    {
        var cats = await _inventory.ListCategoriesAsync();
        Categories.Clear();
        Categories.Add(new Category { Id = 0, Name = (string)System.Windows.Application.Current.TryFindResource("POS_All") as string ?? "All" });
        foreach (var c in cats) Categories.Add(c);
    }

    private async Task LoadProductsAsync()
    {
        var products = await _inventory.SearchProductsAsync(SearchText, _selectedCategoryId == 0 ? null : _selectedCategoryId);
        Products.Clear();
        foreach (var p in products) Products.Add(p);
    }

    public void FilterByCategory(int categoryId)
    {
        _selectedCategoryId = categoryId;
        _ = LoadProductsAsync();
    }

    public async void SearchBySkuOrText()
    {
        var term = SearchText?.Trim() ?? "";
        if (string.IsNullOrEmpty(term)) { await LoadProductsAsync(); return; }

        // Try exact SKU/barcode match first - this is the scanner case.
        var p = await _inventory.GetProductBySkuAsync(term);
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
        if (CartLines.Count == 0) return;
        if (App.CurrentUser == null) return;

        var draft = BuildDraft();
        _paymentDialog.Configure(draft);
        _paymentDialog.Owner = Application.Current.MainWindow;
        if (_paymentDialog.ShowDialog() != true) return;

        try
        {
            var sale = await _sales.CheckoutAsync(draft);
            // Attach payments captured by the dialog
            foreach (var p in _paymentDialog.Payments)
            {
                p.SaleId = sale.Id;
            }

            // Persist payments via direct DB context (sale service lives in different scope)
            using var scope = App.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
            foreach (var p in _paymentDialog.Payments)
            {
                db.SalePayments.Add(new SalePayment
                {
                    SaleId = sale.Id,
                    Method = p.Method,
                    Amount = p.Amount,
                    Reference = p.Reference
                });
            }
            sale.AmountPaid = _paymentDialog.Payments.Sum(p => p.Amount);
            sale.Change = Math.Max(0, sale.AmountPaid - sale.Total);
            await db.SaveChangesAsync();

            // Hardware: print receipt + open drawer
            var store = App.StoreSettings;
            if (store.PrintReceiptAutomatically)
                _ = await _hardware.PrintReceiptAsync(sale);
            if (store.OpenDrawerOnCashSale && _paymentDialog.Payments.Any(p => p.Method == PaymentMethod.Cash))
                _ = await _hardware.OpenCashDrawerAsync();

            ClearCart();
            MessageBox.Show($"Sale completed. Receipt: {sale.ReceiptNumber}\nChange: ৳ {sale.Change:0.00}",
                "Sale Completed", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Checkout failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public async void Suspend()
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
            SuspendedSaleId = null
        };
    }
}

public class CustomerPickerDialog : Window
{
    public Customer? SelectedCustomer { get; private set; }
    private readonly ICustomerService _svc;
    private readonly ObservableCollection<Customer> _list = new();
    private readonly TextBox _search;

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

        _search = new TextBox { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(10, 8, 10, 8) };
        _search.TextChanged += async (_, _) => await Reload();
        grid.Children.Add(_search); Grid.SetRow(_search, 0);

        var list = new ListBox { ItemsSource = _list, Background = System.Windows.Media.Brushes.White };
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
        var term = _search.Text?.Trim();
        var items = await _svc.SearchCustomersAsync(term);
        _list.Clear();
        foreach (var c in items) _list.Add(c);
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

        var list = new ListBox { ItemsSource = _list, Background = System.Windows.Media.Brushes.White };
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
