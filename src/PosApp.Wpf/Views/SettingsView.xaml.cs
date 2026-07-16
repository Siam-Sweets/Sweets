using System.Drawing.Printing;
using System.Windows;
using System.Windows.Controls;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;

namespace PosApp.Wpf.Views;

public partial class SettingsView : UserControl, IRefreshable
{
    private readonly ISettingsService _settings;
    private readonly IHardwareService _hardware;
    private StoreSettings _current = new();
    private bool _isLoading;

    public SettingsView(ISettingsService settings, IHardwareService hardware)
    {
        InitializeComponent();
        _settings = settings;
        _hardware = hardware;
    }

    public async void Refresh()
    {
        IsEnabled = false;
        try { await LoadAsync(); }
        finally { IsEnabled = true; }
    }

    private async Task LoadAsync()
    {
        _isLoading = true;
        try
        {
            _current = await _settings.GetStoreSettingsAsync();
            StoreNameBox.Text = _current.StoreName;
            PhoneBox.Text = _current.Phone;
            EmailBox.Text = _current.Email;
            TaxIdBox.Text = _current.TaxId;
            AddressBox.Text = _current.Address;
            CurrencyBox.Text = _current.CurrencySymbol;
            FooterBox.Text = _current.FooterNote;
            DrawerPortBox.Text = _current.CashDrawerPort;
            ScalePortBox.Text = _current.ScalePort;
            AutoPrintCheckbox.IsChecked = _current.PrintReceiptAutomatically;
            OpenDrawerCheckbox.IsChecked = _current.OpenDrawerOnCashSale;

            PrinterCombo.Items.Clear();
            PrinterCombo.Items.Add("(default)");
            try
            {
                foreach (string name in PrinterSettings.InstalledPrinters)
                {
                    PrinterCombo.Items.Add(name);
                    if (name == _current.ReceiptPrinterName) PrinterCombo.SelectedItem = name;
                }
            }
            catch
            {
                // Windows printing can be unavailable when the spooler is stopped.
                // Settings must remain usable even in that state.
            }
            if (PrinterCombo.SelectedIndex < 0) PrinterCombo.SelectedIndex = 0;

            LangEn.IsChecked = _current.Language != "bn";
            LangBn.IsChecked = _current.Language == "bn";
            ThemeLight.IsChecked = !string.Equals(_current.Theme, "Dark", StringComparison.OrdinalIgnoreCase);
            ThemeDark.IsChecked = string.Equals(_current.Theme, "Dark", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to load settings", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void Lang_Checked(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        // Live preview of language switch
        var code = LangBn.IsChecked == true ? "bn" : "en";
        App.ApplyLanguage(code);
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        App.ApplyTheme(ThemeDark.IsChecked == true ? "Dark" : "Light");
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveCurrentSettingsAsync();
            MessageBox.Show("Settings saved.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to save settings", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateCurrentFromForm()
    {
        _current.StoreName = StoreNameBox.Text;
        _current.Phone = PhoneBox.Text;
        _current.Email = EmailBox.Text;
        _current.TaxId = TaxIdBox.Text;
        _current.Address = AddressBox.Text;
        _current.CurrencySymbol = CurrencyBox.Text;
        _current.FooterNote = FooterBox.Text;
        _current.CashDrawerPort = DrawerPortBox.Text;
        _current.ScalePort = ScalePortBox.Text;
        _current.PrintReceiptAutomatically = AutoPrintCheckbox.IsChecked ?? true;
        _current.OpenDrawerOnCashSale = OpenDrawerCheckbox.IsChecked ?? true;
        _current.ReceiptPrinterName = PrinterCombo.SelectedItem as string == "(default)" ? "" : PrinterCombo.SelectedItem as string ?? "";
        _current.Language = LangBn.IsChecked == true ? "bn" : "en";
        _current.Theme = ThemeDark.IsChecked == true ? "Dark" : "Light";
    }

    private async Task SaveCurrentSettingsAsync()
    {
        UpdateCurrentFromForm();
        await _settings.SetStoreSettingsAsync(_current);
        App.StoreSettings = _current;
        App.ApplyLanguage(_current.Language);
        App.ApplyTheme(_current.Theme);
    }

    private async void TestPrint_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Test the values currently visible in the form, not stale saved values.
            await SaveCurrentSettingsAsync();
            var ok = await _hardware.PrintReceiptAsync(BuildTestSale());
            MessageBox.Show(ok ? "Print sent." : "Print failed. Check the selected printer and Windows print service.",
                "Test Print", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Test Print", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void TestDrawer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveCurrentSettingsAsync();
            var ok = await _hardware.OpenCashDrawerAsync();
            MessageBox.Show(ok ? "Drawer pulse sent." : "Drawer not available. Check the COM port and connection.",
                "Test Drawer", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Test Drawer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private PosApp.Core.Entities.Sale BuildTestSale()
    {
        return new PosApp.Core.Entities.Sale
        {
            ReceiptNumber = "TEST-" + DateTime.Now.Ticks,
            SaleDate = DateTime.Now,
            Subtotal = 100,
            DiscountTotal = 0,
            TaxTotal = 15,
            AmountPaid = 115,
            User = new PosApp.Core.Entities.User { FullName = App.CurrentUser?.FullName ?? "Test" }
        };
    }
}
