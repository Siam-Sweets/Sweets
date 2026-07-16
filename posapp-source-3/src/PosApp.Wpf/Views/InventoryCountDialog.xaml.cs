using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;

namespace PosApp.Wpf.Views;

public partial class InventoryCountDialog : Window
{
    private readonly IInventoryService _inventory;
    private readonly int? _userId;
    private readonly ObservableCollection<InventoryCountRow> _rows;

    public InventoryCountDialog(
        IInventoryService inventory,
        IReadOnlyList<Product> products,
        int? userId)
    {
        InitializeComponent();
        _inventory = inventory;
        _userId = userId;
        _rows = new ObservableCollection<InventoryCountRow>(products.Select(product =>
            new InventoryCountRow(product)));
        CountGrid.ItemsSource = _rows;
        Loaded += (_, _) => SearchBox.Focus();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        var term = SearchBox.Text.Trim();
        CountGrid.ItemsSource = string.IsNullOrEmpty(term)
            ? _rows
            : _rows.Where(row =>
                    row.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    (row.Sku?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (row.Barcode?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        CountGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        CountGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var counted = _rows.Where(row => row.CountedQuantity.HasValue).ToList();
        if (counted.Count == 0)
        {
            MessageBox.Show("Enter at least one counted quantity.",
                "Inventory Count", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (counted.Any(row => row.CountedQuantity.HasValue && row.CountedQuantity.Value < 0m))
        {
            MessageBox.Show("Counted quantities cannot be negative.",
                "Inventory Count", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var changed = counted.Count(row => row.Difference != 0m);
        if (MessageBox.Show(
                $"Post this count for {counted.Count} products? {changed} stock balances will change.",
                "Post Inventory Count", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        try
        {
            IsEnabled = false;
            await _inventory.ApplyInventoryCountAsync(
                counted.Select(row => new InventoryCountEntry
                {
                    ProductId = row.ProductId,
                    CountedQuantity = row.CountedQuantity!.Value
                }).ToList(),
                NoteBox.Text,
                _userId);
            MessageBox.Show("Inventory count posted.", "Inventory Count",
                MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to post inventory count",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }
}

public sealed class InventoryCountRow : INotifyPropertyChanged
{
    private decimal? _countedQuantity;

    public InventoryCountRow(Product product)
    {
        ProductId = product.Id;
        Name = product.Name;
        Sku = product.Sku;
        Barcode = product.Barcode;
        SystemQuantity = product.StockQuantity.GetValueOrDefault();
    }

    public int ProductId { get; }
    public string Name { get; }
    public string? Sku { get; }
    public string? Barcode { get; }
    public decimal SystemQuantity { get; }
    public decimal? CountedQuantity
    {
        get => _countedQuantity;
        set
        {
            if (_countedQuantity == value) return;
            _countedQuantity = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Difference));
        }
    }
    public decimal? Difference => CountedQuantity.HasValue
        ? CountedQuantity.Value - SystemQuantity
        : null;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
