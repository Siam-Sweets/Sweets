using System.Drawing.Printing;
using System.Windows;
using System.Windows.Controls;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Localization;

namespace PosApp.Wpf.Views;

public partial class SettingsView : UserControl, IRefreshable
{
    private readonly ISettingsService _settings;
    private readonly IHardwareService _hardware;
    private StoreSettings _current = new();

    public SettingsView(ISettingsService settings, IHardwareService hardware)
    {
        InitializeComponent();
        _settings = settings;
        _hardware = hardware;
        Loaded += async (_, _) => await LoadAsync();
    }

    public async void Refresh() => await LoadAsync();

    private async Task LoadAsync()
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
        foreach (string name in PrinterSettings.InstalledPrinters)
        {
            PrinterCombo.Items.Add(name);
            if (name == _current.ReceiptPrinterName) PrinterCombo.SelectedItem = name;
        }
        if (PrinterCombo.SelectedIndex < 0) PrinterCombo.SelectedIndex = 0;

        LangEn.IsChecked = _current.Language == "en";
        LangBn.IsChecked = _current.Language == "bn";
        ThemeLight.IsChecked = _current.Theme == "Light";
        ThemeDark.IsChecked = _current.Theme == "Dark";
    }

    private void Lang_Checked(object sender, RoutedEventArgs e)
    {
        // Live preview of language switch
        var code = LangBn.IsChecked == true ? "bn" : "en";
        ApplyLanguage(code);
    }

    private static void ApplyLanguage(string code)
    {
        var dict = new System.Windows.ResourceDictionary();
        var path = code == "bn"
            ? "pack://application:,,,/PosApp.Localization;component/Strings.bn.xaml"
            : "pack://application:,,,/PosApp.Localization;component/Strings.en.xaml";
        dict.Source = new Uri(path);
        var mergedDicts = System.Windows.Application.Current.Resources.MergedDictionaries;
        // Remove existing localization dict
        for (int i = mergedDicts.Count - 1; i >= 0; i--)
        {
            if (mergedDicts[i].Source?.OriginalString.Contains("Strings.") == true)
            {
                mergedDicts.RemoveAt(i);
            }
        }
        mergedDicts.Insert(0, dict);
        LocalizationManager.Instance.SetLanguage(code);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
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

        await _settings.SetStoreSettingsAsync(_current);
        App.StoreSettings = _current;
        MessageBox.Show("Settings saved.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void TestPrint_Click(object sender, RoutedEventArgs e)
    {
        var ok = await _hardware.PrintReceiptAsync(BuildTestSale());
        MessageBox.Show(ok ? "Print sent." : "Print failed. Check printer name in Settings.",
            "Test Print", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async void TestDrawer_Click(object sender, RoutedEventArgs e)
    {
        var ok = await _hardware.OpenCashDrawerAsync();
        MessageBox.Show(ok ? "Drawer pulse sent." : "Drawer not available. Check COM port.",
            "Test Drawer", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private PosApp.Core.Entities.Sale BuildTestSale()
    {
        var store = _current;
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
