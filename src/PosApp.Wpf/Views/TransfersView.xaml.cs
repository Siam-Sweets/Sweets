using System.Windows;
using System.Windows.Controls;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Wpf.Helpers;

namespace PosApp.Wpf.Views;

public partial class TransfersView : UserControl, IRefreshable
{
    private readonly IStockTransferService _transfers;
    private readonly IStoreService _stores;
    private readonly IInventoryService _inventory;
    private IReadOnlyList<Store> _storeList = Array.Empty<Store>();
    private IReadOnlyList<StockTransfer> _transferList = Array.Empty<StockTransfer>();
    private bool _loadingFilters;

    public TransfersView(IStockTransferService transfers, IStoreService stores, IInventoryService inventory)
    {
        InitializeComponent();
        _transfers = transfers;
        _stores = stores;
        _inventory = inventory;
    }

    public async Task RefreshAsync()
    {
        IsEnabled = false;
        try
        {
            _storeList = await _stores.GetStoresAsync(false);
            var allStores = App.CurrentUser?.Role == UserRole.Admin;
            _transferList = await _transfers.GetTransfersAsync(allStores ? 0 : App.CurrentStore?.Id);
            var names = _storeList.ToDictionary(x => x.Id, x => x.Name);
            TransferGrid.ItemsSource = _transferList.Select(x => new TransferListRow
            {
                Id = x.Id,
                TransferNumber = x.TransferNumber,
                SourceStore = names.GetValueOrDefault(x.StoreId, $"Store {x.StoreId}"),
                DestinationStore = names.GetValueOrDefault(x.DestinationStoreId, $"Store {x.DestinationStoreId}"),
                Status = LocalizedStatus(x.Status),
                StatusValue = x.Status,
                ItemCount = x.Items.Count,
                CreatedAt = x.CreatedAt.ToLocalTime(),
                LastActionAt = (x.ReceivedAt ?? x.CancelledAt ?? x.DispatchedAt)?.ToLocalTime()
            }).ToList();
            TransferGrid.SelectedIndex = _transferList.Count > 0 ? 0 : -1;
            UpdateTransferDetails();
            await LoadInventoryFiltersAsync();
            await LoadInventoryAsync();
        }
        catch (Exception ex)
        {
            LocalizedMessageBox.Show(ex.GetBaseException().Message, "Stock transfers", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsEnabled = true; }
    }

    private async Task LoadInventoryFiltersAsync()
    {
        _loadingFilters = true;
        try
        {
            var options = new List<StoreFilterOption>();
            if (App.CurrentUser?.Role == UserRole.Admin) options.Add(new StoreFilterOption(0, FindResource("Transfer_AllStores")?.ToString() ?? "All stores"));
            options.AddRange(_storeList.Select(x => new StoreFilterOption(x.Id, x.Name)));
            InventoryStoreFilter.ItemsSource = options;
            var desired = App.CurrentUser?.Role == UserRole.Admin ? 0 : App.CurrentStore?.Id ?? 1;
            InventoryStoreFilter.SelectedValue = desired;
        }
        finally { _loadingFilters = false; }
        await Task.CompletedTask;
    }

    private async Task LoadInventoryAsync()
    {
        var selected = InventoryStoreFilter.SelectedValue is int value ? value : App.CurrentStore?.Id ?? 1;
        var storeId = selected == 0 ? (int?)null : selected;
        InventoryGrid.ItemsSource = await _transfers.GetInventoryAcrossStoresAsync(storeId, InventorySearch.Text);
    }

    private StockTransfer? SelectedTransfer()
    {
        if (TransferGrid.SelectedItem is not TransferListRow row) return null;
        return _transferList.FirstOrDefault(x => x.Id == row.Id);
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sourceId = App.CurrentStore?.Id ?? 1;
            var destinations = _storeList.Where(x => x.Id != sourceId).ToList();
            if (destinations.Count == 0) throw new InvalidOperationException("Create another active store before making a transfer.");
            var products = await _inventory.SearchProductsAsync(null, includeInactive: false);
            var dialog = new TransferEditDialog(destinations, products) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true || dialog.Draft == null) return;
            await _transfers.CreateDraftAsync(dialog.Draft, App.CurrentUser?.Id ?? 0);
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Dispatch_Click(object sender, RoutedEventArgs e)
    {
        var transfer = SelectedTransfer();
        if (transfer == null) return;
        if (!Confirm("Dispatch this transfer and remove the quantities from the source store?")) return;
        try { await _transfers.DispatchAsync(transfer.Id, App.CurrentUser?.Id ?? 0); await RefreshAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Receive_Click(object sender, RoutedEventArgs e)
    {
        var transfer = SelectedTransfer();
        if (transfer == null) return;
        if (!Confirm("Receive this transfer into the destination store?")) return;
        try { await _transfers.ReceiveAsync(transfer.Id, App.CurrentUser?.Id ?? 0); await RefreshAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        var transfer = SelectedTransfer();
        if (transfer == null) return;
        if (!Confirm("Cancel this transfer? Dispatched quantities will be returned to the source store.")) return;
        try { await _transfers.CancelAsync(transfer.Id, App.CurrentUser?.Id ?? 0); await RefreshAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void TransferGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateTransferDetails();

    private void UpdateTransferDetails()
    {
        var transfer = SelectedTransfer();
        ItemsGrid.ItemsSource = transfer?.Items.OrderBy(x => x.ProductName).ToList();
        if (transfer == null)
        {
            TransferAuditText.Text = string.Empty;
            DispatchButton.IsEnabled = ReceiveButton.IsEnabled = CancelButton.IsEnabled = false;
            return;
        }

        var actions = new List<string>
        {
            $"{FindResource("Transfer_Status")}: {LocalizedStatus(transfer.Status)}",
            $"{FindResource("Transfer_Created")}: {transfer.CreatedAt.ToLocalTime():g}"
        };
        if (transfer.DispatchedAt.HasValue) actions.Add($"{FindResource("Transfer_Dispatch")}: {transfer.DispatchedAt.Value.ToLocalTime():g}");
        if (transfer.ReceivedAt.HasValue) actions.Add($"{FindResource("Transfer_Receive")}: {transfer.ReceivedAt.Value.ToLocalTime():g}");
        if (transfer.CancelledAt.HasValue) actions.Add($"{FindResource("Transfer_Cancel")}: {transfer.CancelledAt.Value.ToLocalTime():g}");
        if (!string.IsNullOrWhiteSpace(transfer.Note)) actions.Add($"{FindResource("Transfer_Note")}: {transfer.Note}");
        TransferAuditText.Text = string.Join("  •  ", actions);

        var currentStoreId = App.CurrentStore?.Id ?? 0;
        DispatchButton.IsEnabled = transfer.Status == StockTransferStatus.Draft && transfer.StoreId == currentStoreId;
        ReceiveButton.IsEnabled = transfer.Status == StockTransferStatus.Dispatched && transfer.DestinationStoreId == currentStoreId;
        CancelButton.IsEnabled = (transfer.Status is StockTransferStatus.Draft or StockTransferStatus.Dispatched) && transfer.StoreId == currentStoreId;
    }

    private string LocalizedStatus(StockTransferStatus status) => status switch
    {
        StockTransferStatus.Draft => FindResource("Transfer_StatusDraft")?.ToString() ?? "Draft",
        StockTransferStatus.Dispatched => FindResource("Transfer_StatusDispatched")?.ToString() ?? "Dispatched",
        StockTransferStatus.Received => FindResource("Transfer_StatusReceived")?.ToString() ?? "Received",
        StockTransferStatus.Cancelled => FindResource("Transfer_StatusCancelled")?.ToString() ?? "Cancelled",
        _ => status.ToString()
    };

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void InventoryRefresh_Click(object sender, RoutedEventArgs e) => await LoadInventoryAsync();
    private async void InventoryFilter_Changed(object sender, SelectionChangedEventArgs e) { if (!_loadingFilters) await LoadInventoryAsync(); }
    private async void InventorySearch_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded && !_loadingFilters) await LoadInventoryAsync(); }

    private static bool Confirm(string text) => LocalizedMessageBox.Show(text, "Stock transfers", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    private static void ShowError(Exception ex) => LocalizedMessageBox.Show(ex.GetBaseException().Message, "Stock transfers", MessageBoxButton.OK, MessageBoxImage.Error);
}

public sealed class TransferListRow
{
    public int Id { get; set; }
    public string TransferNumber { get; set; } = string.Empty;
    public string SourceStore { get; set; } = string.Empty;
    public string DestinationStore { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public StockTransferStatus StatusValue { get; set; }
    public int ItemCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastActionAt { get; set; }
}

public sealed record StoreFilterOption(int Id, string Name);
