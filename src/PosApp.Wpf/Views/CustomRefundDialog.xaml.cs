using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PosApp.Core.Entities;
using PosApp.Core.Models;
using PosApp.Core.Utilities;

namespace PosApp.Wpf.Views;

public partial class CustomRefundDialog : Window
{
    private readonly Sale _original;
    private readonly decimal _remainingFinancialTotal;
    public ObservableCollection<RefundLineRow> Lines { get; } = new();
    public RefundDraft? Draft { get; private set; }

    public CustomRefundDialog(Sale original, IReadOnlyCollection<Sale> priorRefunds)
    {
        InitializeComponent();
        _original = original;
        _remainingFinancialTotal = Math.Max(0m,
            Math.Abs(original.Total) - priorRefunds.Sum(refund => Math.Abs(refund.Total)));
        ReceiptText.Text = $"Receipt {original.ReceiptNumber}  •  {FormattingUtilities.Money(original.Total, App.StoreSettings)}";

        var returned = RefundQuantityUtilities.BuildReturnedByLine(
            original.Items, priorRefunds.SelectMany(refund => refund.Items));
        foreach (var item in original.Items.OrderBy(item => item.Id))
        {
            var previouslyRefunded = returned.GetValueOrDefault(item.Id);
            var remaining = Math.Max(0m, item.Quantity - previouslyRefunded);
            if (remaining <= 0.0001m) continue;
            var row = new RefundLineRow(item, previouslyRefunded, remaining);
            row.PropertyChanged += Line_PropertyChanged;
            Lines.Add(row);
        }

        RefundGrid.ItemsSource = Lines;
        PaymentMethodCombo.SelectedIndex = FindPaymentMethodIndex(
            original.Payments.FirstOrDefault()?.Method ?? PaymentMethod.Cash);
        UpdateTotal();
    }

    private static int FindPaymentMethodIndex(PaymentMethod method) => method switch
    {
        PaymentMethod.Card => 1,
        PaymentMethod.MobileWallet => 2,
        PaymentMethod.BankTransfer => 3,
        PaymentMethod.StoreCredit => 4,
        PaymentMethod.Coupon => 5,
        PaymentMethod.Other => 6,
        _ => 0
    };

    private void Line_PropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateTotal();

    private void RefundGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        => Dispatcher.BeginInvoke(new Action(UpdateTotal), DispatcherPriority.Background);

    private void RefundSelection_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not RefundLineRow line) return;

        // DataGrid normally consumes the first click to select/edit its cell. Toggle
        // the draft row explicitly so one click always changes the refund selection.
        e.Handled = true;
        line.IsSelected = !line.IsSelected;
        checkBox.IsChecked = line.IsSelected;
        if (line.IsSelected && line.Quantity <= 0m)
            line.Quantity = line.RemainingQuantity;
        UpdateTotal();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var line in Lines)
        {
            line.Quantity = line.RemainingQuantity;
            line.IsSelected = true;
        }
        UpdateTotal();
    }

    private void UpdateTotal()
    {
        var selected = Lines.Where(line => line.IsSelected).ToList();
        var isFinalRefund = Lines.Count > 0 && Lines.All(line =>
            line.IsSelected && Math.Abs(line.Quantity - line.RemainingQuantity) <= 0.0001m);
        var total = isFinalRefund
            ? _remainingFinancialTotal
            : selected.Sum(line => line.RefundAmount);
        RefundTotalText.Text = FormattingUtilities.Money(
            total, App.StoreSettings);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        RefundGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RefundGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var selected = Lines.Where(line => line.IsSelected).ToList();
        if (selected.Count == 0)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Select at least one item to refund.", "Custom Refund",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var invalid = selected.FirstOrDefault(line =>
            line.Quantity <= 0m || line.Quantity > line.RemainingQuantity + 0.0001m);
        if (invalid != null)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(
                $"Enter a quantity from 0.001 to {invalid.RemainingQuantity:0.###} for {invalid.ProductName}.",
                "Invalid refund quantity", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (PaymentMethodCombo.SelectedItem is not ComboBoxItem { Tag: string methodTag } ||
            !Enum.TryParse<PaymentMethod>(methodTag, out var paymentMethod))
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Select a refund payment method.", "Custom Refund",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Draft = new RefundDraft
        {
            SaleId = _original.Id,
            UserId = App.CurrentUser?.Id ?? 0,
            PaymentMethod = paymentMethod,
            Reason = string.IsNullOrWhiteSpace(ReasonBox.Text) ? null : ReasonBox.Text.Trim(),
            Lines = selected.Select(line => new RefundDraftLine
            {
                SaleItemId = line.SaleItemId,
                Quantity = line.Quantity
            }).ToList()
        };
        DialogResult = true;
    }
}

public sealed class RefundLineRow : INotifyPropertyChanged
{
    private bool _isSelected;
    private decimal _quantity;

    public int SaleItemId { get; }
    public string ProductName { get; }
    public decimal SoldQuantity { get; }
    public decimal PreviouslyRefunded { get; }
    public decimal RemainingQuantity { get; }
    public decimal UnitPrice { get; }
    public decimal DiscountAmount { get; }
    public decimal TaxRate { get; }
    public UnitOfMeasure Unit { get; }
    public string UnitSymbol => Unit.ToSymbol();
    public string SoldDisplay => $"{SoldQuantity:0.###} {UnitSymbol}";
    public string PreviouslyRefundedDisplay => $"{PreviouslyRefunded:0.###} {UnitSymbol}";
    public string RemainingDisplay => $"{RemainingQuantity:0.###} {UnitSymbol}";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RefundAmount));
            OnPropertyChanged(nameof(DisplayAmount));
        }
    }

    public decimal Quantity
    {
        get => _quantity;
        set
        {
            if (_quantity == value) return;
            _quantity = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RefundAmount));
            OnPropertyChanged(nameof(DisplayAmount));
        }
    }

    public decimal RefundAmount
    {
        get
        {
            if (!IsSelected || Quantity <= 0m || SoldQuantity == 0m) return 0m;
            var ratio = Quantity / SoldQuantity;
            var discount = Math.Round(DiscountAmount * ratio, 4, MidpointRounding.AwayFromZero);
            var taxable = UnitPrice * Quantity - discount;
            var tax = Math.Round(taxable * TaxRate / 100m, 4, MidpointRounding.AwayFromZero);
            return taxable + tax;
        }
    }

    public string DisplayAmount => FormattingUtilities.Money(RefundAmount, App.StoreSettings);

    public RefundLineRow(SaleItem item, decimal previouslyRefunded, decimal remainingQuantity)
    {
        SaleItemId = item.Id;
        ProductName = item.ProductName;
        SoldQuantity = item.Quantity;
        PreviouslyRefunded = previouslyRefunded;
        RemainingQuantity = remainingQuantity;
        UnitPrice = item.UnitPrice;
        DiscountAmount = item.DiscountAmount;
        TaxRate = item.TaxRate;
        Unit = item.Unit;
        _quantity = remainingQuantity;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
