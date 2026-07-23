using System.Windows;
using System.Windows.Controls;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;

namespace PosApp.Wpf.Views;

public partial class StoresView : UserControl, IRefreshable
{
    private readonly IStoreService _stores;
    private readonly ISettingsService _settings;

    public StoresView(IStoreService stores, ISettingsService settings)
    {
        InitializeComponent();
        _stores = stores;
        _settings = settings;
    }

    public async Task RefreshAsync()
    {
        var currentId = App.CurrentStore?.Id ?? 0;
        var rows = (await _stores.GetStoresAsync())
            .Select(store => new StoreRow(store, store.Id == currentId))
            .ToList();
        StoresGrid.ItemsSource = rows;
        StoresGrid.SelectedItem = rows.FirstOrDefault(x => x.IsCurrent) ?? rows.FirstOrDefault();
    }

    private StoreRow? Selected => StoresGrid.SelectedItem as StoreRow;

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new StoreEditDialog(null) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
            await SaveAsync(dialog.Result!);
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;
        var dialog = new StoreEditDialog(Selected.Store) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
            await SaveAsync(dialog.Result!);
    }

    private async Task SaveAsync(Store store)
    {
        try
        {
            var saved = await _stores.SaveStoreAsync(store);
            if (saved.Id == App.CurrentStore?.Id)
            {
                App.PublishStore(saved);
                App.PublishSettings(await _settings.GetStoreSettingsAsync());
            }
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Helpers.LocalizedMessageBox.Show(ex.GetBaseException().Message, "Unable to save store",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ToggleActive_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;
        try
        {
            await _stores.SetStoreActiveAsync(Selected.Store.Id, !Selected.Store.IsActive);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Helpers.LocalizedMessageBox.Show(ex.GetBaseException().Message, "Unable to update store",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null || Selected.IsCurrent) return;
        var result = Helpers.LocalizedMessageBox.Show(
            $"Switch to {Selected.Store.Name}? You will return to the login screen.",
            "Switch store", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            await _stores.SelectStoreAsync(Selected.Store.Id);
            App.PublishStore(await _stores.GetCurrentStoreAsync());
            App.PublishSettings(await _settings.GetStoreSettingsAsync());
            App.ApplyTheme(App.StoreSettings.Theme);
            App.ApplyLanguage(App.StoreSettings.Language);
            if (Window.GetWindow(this) is MainWindow main) main.SignOut();
        }
        catch (Exception ex)
        {
            Helpers.LocalizedMessageBox.Show(ex.GetBaseException().Message, "Unable to switch store",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private sealed class StoreRow
    {
        public StoreRow(Store store, bool current)
        {
            Store = store;
            IsCurrent = current;
        }

        public Store Store { get; }
        public int Id => Store.Id;
        public string Code => Store.Code;
        public string Name => Store.Name;
        public string? Address => Store.Address;
        public string? Phone => Store.Phone;
        public bool IsActive => Store.IsActive;
        public bool IsCurrent { get; }
    }
}
