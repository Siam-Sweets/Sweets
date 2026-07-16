using System.Diagnostics;
using System.Drawing.Printing;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;

namespace PosApp.Wpf.Views;

public partial class SettingsView : UserControl, IRefreshable
{
    private readonly ISettingsService _settings;
    private readonly IHardwareService _hardware;
    private readonly IBackupService _backup;
    private StoreSettings _current = new();
    private bool _isLoading;

    public SettingsView(ISettingsService settings, IHardwareService hardware, IBackupService backup)
    {
        InitializeComponent();
        _settings = settings;
        _hardware = hardware;
        _backup = backup;
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
            AutoBackupCheckbox.IsChecked = _current.AutomaticBackupEnabled;
            BackupStartupCheckbox.IsChecked = _current.BackupOnStartup;
            BackupExitCheckbox.IsChecked = _current.BackupOnExit;
            BackupRetentionBox.Text = Math.Clamp(_current.BackupRetentionCount, 1, 200).ToString();
            BackupFolderText.Text = _backup.BackupFolder;

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
        _current.AutomaticBackupEnabled = AutoBackupCheckbox.IsChecked == true;
        _current.BackupOnStartup = BackupStartupCheckbox.IsChecked == true;
        _current.BackupOnExit = BackupExitCheckbox.IsChecked == true;
        if (!int.TryParse(BackupRetentionBox.Text, out var retention) || retention < 1 || retention > 200)
            throw new InvalidOperationException("Backup retention must be a whole number from 1 to 200.");
        _current.BackupRetentionCount = retention;
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

    private async void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Create PosApp Backup",
            Filter = "PosApp database backup (*.db)|*.db|All files (*.*)|*.*",
            FileName = $"posapp-backup-{DateTime.Now:yyyyMMdd-HHmmss}.db",
            InitialDirectory = _backup.BackupFolder,
            DefaultExt = ".db"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            IsEnabled = false;
            var path = await _backup.CreateBackupAsync(dialog.FileName);
            MessageBox.Show($"Backup created successfully.\n\n{path}", "Backup",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to create backup", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Restore PosApp Backup",
            Filter = "PosApp database backup (*.db)|*.db|SQLite database (*.sqlite;*.sqlite3)|*.sqlite;*.sqlite3|All files (*.*)|*.*",
            InitialDirectory = _backup.BackupFolder,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (MessageBox.Show(
                "The selected backup will replace the current data on the next start. PosApp will preserve a safety copy of the current database and then close. Continue?",
                "Restore Backup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            IsEnabled = false;
            await _backup.StageRestoreAsync(dialog.FileName);
            MessageBox.Show("Backup validated and staged. PosApp will now close; start it again to finish the restore.",
                "Restore Ready", MessageBoxButton.OK, MessageBoxImage.Information);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to restore backup", MessageBoxButton.OK, MessageBoxImage.Error);
            IsEnabled = true;
        }
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_backup.BackupFolder}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to open backup folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
