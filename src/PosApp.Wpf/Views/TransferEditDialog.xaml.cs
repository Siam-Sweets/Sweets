using System.Windows;
using PosApp.Core.Entities;
using PosApp.Core.Models;
using PosApp.Wpf.Helpers;

namespace PosApp.Wpf.Views;

public partial class TransferEditDialog : Window
{
    private readonly List<TransferProductSelectionRow> _rows;
    public StockTransferDraft? Draft { get; private set; }

    public TransferEditDialog(IReadOnlyList<Store> destinations, IReadOnlyList<Product> products)
    {
        InitializeComponent();
        DestinationBox.ItemsSource = destinations;
        DestinationBox.SelectedIndex = destinations.Count > 0 ? 0 : -1;
        _rows = products.Where(x => x.StockQuantity.HasValue).OrderBy(x => x.Name).Select(x => new TransferProductSelectionRow
        {
            ProductId = x.Id,
            ProductName = x.Name,
            Sku = x.Sku,
            Available = x.StockQuantity ?? 0m,
            Unit = x.EffectiveUnit
        }).ToList();
        ProductGrid.ItemsSource = _rows;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ProductGrid.CommitEdit();
        ProductGrid.CommitEdit();
        if (DestinationBox.SelectedValue is not int destinationId)
        {
            LocalizedMessageBox.Show("Select a destination store.", "Stock transfer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var selected = _rows.Where(x => x.IsSelected || x.Quantity > 0m).ToList();
        if (selected.Count == 0 || selected.Any(x => x.Quantity <= 0m || x.Quantity > x.Available))
        {
            LocalizedMessageBox.Show("Select products and enter quantities within the available stock.", "Stock transfer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Draft = new StockTransferDraft
        {
            DestinationStoreId = destinationId,
            Note = NoteBox.Text,
            Items = selected.Select(x => new StockTransferDraftItem { ProductId = x.ProductId, Quantity = x.Quantity }).ToList()
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed class TransferProductSelectionRow
{
    public bool IsSelected { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public decimal Available { get; set; }
    public decimal Quantity { get; set; }
    public UnitOfMeasure Unit { get; set; }
}
