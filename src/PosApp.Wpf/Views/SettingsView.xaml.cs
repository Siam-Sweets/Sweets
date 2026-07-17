using System.Diagnostics;
using System.Drawing.Printing;
using System.IO;
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
    private readonly IUpdateService _updates;
    private StoreSettings _current = new();
    private bool _isLoading;
    private readonly SemaphoreSlim _appearanceSaveGate = new(1, 1);

    public SettingsView(ISettingsService settings, IHardwareService hardware, IBackupService backup,
        IUpdateService updates)
    {
        InitializeComponent();
        _settings = settings;
        _hardware = hardware;
        _backup = backup;
        _updates = updates;
    }

    public async void Refresh()
    {
        IsEnabled = false;
        try { await LoadAsync(); }
        finally { IsEnabled = true; }
    }

    public void SelectSection(string section)
    {
        SettingsTabs.SelectedIndex = section.ToLowerInvariant() switch
        {
            "company" => 0,
            "order" => 1,
            "payment" => 1,
            "tax" => 1,
            "products" => 2,
            "documents" => 3,
            "scale" => 4,
            "display" => 5,
            "email" => 6,
            "print" => 7,
            "database" => 8,
            "update" => 9,
            "about" => 10,
            _ => 0
        };
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
            CurrencyCodeBox.Text = _current.CurrencyCode;
            CountryBox.Text = _current.Country;
            FooterBox.Text = _current.FooterNote;
            ReceiptWidthBox.Text = Math.Clamp(_current.ReceiptWidth, 40, 120).ToString();
            DrawerPortBox.Text = _current.CashDrawerPort;
            ScalePortBox.Text = _current.ScalePort;
            AutoPrintCheckbox.IsChecked = _current.PrintReceiptAutomatically;
            OpenDrawerCheckbox.IsChecked = _current.OpenDrawerOnCashSale;
            AutoBackupCheckbox.IsChecked = _current.AutomaticBackupEnabled;
            BackupStartupCheckbox.IsChecked = _current.BackupOnStartup;
            BackupExitCheckbox.IsChecked = _current.BackupOnExit;
            BackupRetentionBox.Text = Math.Clamp(_current.BackupRetentionCount, 1, 200).ToString();
            BackupFolderText.Text = _backup.BackupFolder;
            CurrentVersionText.Text = $"PosApp {_updates.CurrentVersion}";
            AboutVersionText.Text = $"Version {_updates.CurrentVersion}";
            UpdateDataFolderText.Text = _updates.DataFolder;
            UpdateBackupFolderText.Text = _updates.UpdateBackupFolder;
            SelectedInstallerText.Text = "No update installer selected.";
            await LoadUpdateStatusAsync();
            DefaultServiceCombo.SelectedIndex = _current.DefaultServiceType switch
            {
                "Takeaway" => 1,
                "Delivery" => 2,
                "Dine-in" => 3,
                _ => 0
            };
            DefaultTaxBox.Text = _current.DefaultTaxRate.ToString("0.##");
            RequireOpenRegisterCheckbox.IsChecked = _current.RequireOpenRegisterForSales;
            ConfirmVoidCheckbox.IsChecked = _current.ConfirmBeforeVoidingOrder;
            ShowCashInCheckbox.IsChecked = _current.ShowCashInOnStartup;
            BusinessDayCheckbox.IsChecked = _current.SelectBusinessDayOnStartup;
            GridRowsBox.Text = Math.Clamp(_current.ProductGridRows, 2, 10).ToString();
            GridColumnsBox.Text = Math.Clamp(_current.ProductGridColumns, 2, 10).ToString();
            VirtualKeyboardCheckbox.IsChecked = _current.EnableVirtualKeyboard;
            MessageDurationBox.Text = Math.Clamp(_current.MessageDurationSeconds, 1, 60).ToString();
            SelectComboByText(UiScaleCombo, $"{Math.Clamp(_current.UiScalePercent, 90, 125)}%", "100%");

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

    private async void Lang_Checked(object sender, RoutedEventArgs e)
    {
        if (_isLoading || sender is not RadioButton selected || selected.IsChecked != true) return;

        // WPF raises Checked before unchecking the other radio button. Use the
        // button that raised the event instead of reading the old selection.
        var code = ReferenceEquals(selected, LangBn) ? "bn" : "en";
        _current.Language = code;
        App.ApplyLanguage(code);
        await PersistAppearanceAsync();
    }

    private async void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_isLoading || sender is not RadioButton selected || selected.IsChecked != true) return;

        var theme = ReferenceEquals(selected, ThemeDark) ? "Dark" : "Light";
        _current.Theme = theme;
        App.ApplyTheme(theme);
        await PersistAppearanceAsync();
    }

    private async Task PersistAppearanceAsync()
    {
        await _appearanceSaveGate.WaitAsync();
        try
        {
            await _settings.SetStoreSettingsAsync(_current);
            App.StoreSettings = _current;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to save appearance", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _appearanceSaveGate.Release();
        }
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
        _current.CurrencyCode = string.IsNullOrWhiteSpace(CurrencyCodeBox.Text) ? "BDT" : CurrencyCodeBox.Text.Trim().ToUpperInvariant();
        _current.Country = CountryBox.Text.Trim();
        _current.FooterNote = FooterBox.Text;
        if (!int.TryParse(ReceiptWidthBox.Text, out var receiptWidth) || receiptWidth < 40 || receiptWidth > 120)
            throw new InvalidOperationException("Receipt width must be a whole number from 40 to 120 mm.");
        _current.ReceiptWidth = receiptWidth;
        _current.CashDrawerPort = DrawerPortBox.Text;
        _current.ScalePort = ScalePortBox.Text.Trim();
        _current.PrintReceiptAutomatically = AutoPrintCheckbox.IsChecked ?? true;
        _current.OpenDrawerOnCashSale = OpenDrawerCheckbox.IsChecked ?? true;
        _current.ReceiptPrinterName = PrinterCombo.SelectedItem as string == "(default)" ? "" : PrinterCombo.SelectedItem as string ?? "";
        _current.Language = LangBn.IsChecked == true ? "bn" : "en";
        _current.Theme = ThemeDark.IsChecked == true ? "Dark" : "Light";
        _current.AutomaticBackupEnabled = AutoBackupCheckbox.IsChecked == true;
        _current.BackupOnStartup = BackupStartupCheckbox.IsChecked == true;
        _current.BackupOnExit = BackupExitCheckbox.IsChecked == true;
        _current.DefaultServiceType = (DefaultServiceCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Retail";
        if (!decimal.TryParse(DefaultTaxBox.Text, out var defaultTax) || defaultTax < 0m || defaultTax > 100m)
            throw new InvalidOperationException("Default tax must be from 0 to 100.");
        _current.DefaultTaxRate = defaultTax;
        _current.RequireOpenRegisterForSales = RequireOpenRegisterCheckbox.IsChecked == true;
        _current.ConfirmBeforeVoidingOrder = ConfirmVoidCheckbox.IsChecked != false;
        _current.ShowCashInOnStartup = ShowCashInCheckbox.IsChecked == true;
        _current.SelectBusinessDayOnStartup = BusinessDayCheckbox.IsChecked == true;
        _current.EnableVirtualKeyboard = VirtualKeyboardCheckbox.IsChecked == true;
        if (!int.TryParse(GridRowsBox.Text, out var productRows) || productRows < 2 || productRows > 10)
            throw new InvalidOperationException("Product rows must be a whole number from 2 to 10.");
        if (!int.TryParse(GridColumnsBox.Text, out var productColumns) || productColumns < 2 || productColumns > 10)
            throw new InvalidOperationException("Product columns must be a whole number from 2 to 10.");
        if (!int.TryParse(MessageDurationBox.Text, out var messageSeconds) || messageSeconds < 1 || messageSeconds > 60)
            throw new InvalidOperationException("Message duration must be a whole number from 1 to 60 seconds.");
        _current.ProductGridRows = productRows;
        _current.ProductGridColumns = productColumns;
        _current.MessageDurationSeconds = messageSeconds;
        var scaleText = (UiScaleCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()?.TrimEnd('%');
        _current.UiScalePercent = int.TryParse(scaleText, out var scale) ? scale : 100;
        if (!int.TryParse(BackupRetentionBox.Text, out var retention) || retention < 1 || retention > 200)
            throw new InvalidOperationException("Backup retention must be a whole number from 1 to 200.");
        _current.BackupRetentionCount = retention;
    }

    private async Task SaveCurrentSettingsAsync()
    {
        UpdateCurrentFromForm();
        await _appearanceSaveGate.WaitAsync();
        try
        {
            await _settings.SetStoreSettingsAsync(_current);
            App.StoreSettings = _current;
            App.ApplyLanguage(_current.Language);
            App.ApplyTheme(_current.Theme);
            (Application.Current.MainWindow as MainWindow)?.ApplyUiScale(_current.UiScalePercent);
        }
        finally
        {
            _appearanceSaveGate.Release();
        }
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

    private async void TestScale_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveCurrentSettingsAsync();
            var configuredPort = _current.ScalePort;
            if (string.IsNullOrWhiteSpace(configuredPort))
            {
                MessageBox.Show("Enter the scale COM port before testing (for example, COM3).",
                    "Test Scale", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var connected = await _hardware.IsScaleConnected();
            if (!connected)
            {
                MessageBox.Show(
                    $"Unable to open scale port {configuredPort}. Confirm the port in Windows Device Manager, close any other program using it, and check the cable and power.",
                    "Test Scale", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var weight = await _hardware.ReadScaleAsync();
            MessageBox.Show(weight.HasValue
                    ? $"Scale connected on {configuredPort}. Current reading: {weight.Value:0.###} kg."
                    : $"Scale port {configuredPort} opened, but no readable weight was received. Place an item on the scale and confirm that it uses 9600 baud, 8 data bits, no parity, one stop bit, and the R command protocol.",
                "Test Scale", MessageBoxButton.OK,
                weight.HasValue ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Test Scale", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void SelectComboByText(ComboBox combo, string preferred, string fallback)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), preferred, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), fallback, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
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

    private async Task LoadUpdateStatusAsync()
    {
        var pending = await _updates.GetPendingUpdateAsync();
        if (pending != null)
        {
            UpdateStatusText.Text =
                $"An update to {pending.TargetVersion} is pending. Recovery backup: {pending.BackupPath}";
            return;
        }

        var last = await _updates.GetLastUpdateAsync();
        if (last == null)
        {
            UpdateStatusText.Text = "No safe update has been run on this computer yet.";
            return;
        }

        var completed = last.CompletedAtUtc?.ToLocalTime().ToString("g") ?? "unknown time";
        UpdateStatusText.Text = last.State switch
        {
            "Completed" =>
                $"Last safe update completed at {completed}: {last.FromVersion} to {last.RunningVersion}. " +
                $"Recovery backup retained at {last.BackupPath}",
            "InstallerNotApplied" =>
                $"The last installer was not applied. PosApp is still {last.RunningVersion}. " +
                $"The safety backup is retained at {last.BackupPath}",
            "LaunchFailed" =>
                $"The last installer could not start. The safety backup is retained at {last.BackupPath}",
            _ => $"Last update status: {last.State}. Recovery backup: {last.BackupPath}"
        };
    }

    private async void SelectUpdate_Click(object sender, RoutedEventArgs e)
    {
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var dialog = new OpenFileDialog
        {
            Title = "Choose a newer PosApp setup installer",
            Filter = "PosApp setup installer (PosApp-*-Setup.exe)|PosApp-*-Setup.exe|Windows executable (*.exe)|*.exe",
            InitialDirectory = Directory.Exists(downloads) ? downloads : Environment.CurrentDirectory,
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            IsEnabled = false;
            var package = await _updates.InspectInstallerAsync(dialog.FileName);
            if (!package.IsValid)
                throw new InvalidOperationException(package.ValidationMessage);

            var sizeMb = package.SizeBytes / 1024d / 1024d;
            SelectedInstallerText.Text =
                $"{package.FileName}\nVersion {package.TargetVersion} • {sizeMb:0.0} MB\nSHA-256: {package.Sha256}";
            IsEnabled = true;

            var confirmation =
                $"Update PosApp {package.CurrentVersion} to {package.TargetVersion}?\n\n" +
                "Before opening the installer, PosApp will create and validate a complete SQLite backup. " +
                "The live database remains under your Windows profile and is not stored in Program Files.\n\n" +
                $"Recovery backups:\n{_updates.UpdateBackupFolder}\n\n" +
                "Only continue if this installer came from a PosApp release source you trust. " +
                "PosApp will close after the installer starts.";
            if (MessageBox.Show(confirmation, "Safe PosApp Update",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            IsEnabled = false;
            await _updates.PrepareAndLaunchAsync(package.InstallerPath);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            IsEnabled = true;
            MessageBox.Show(ex.Message, "Update stopped", MessageBoxButton.OK, MessageBoxImage.Error);
            await LoadUpdateStatusAsync();
        }
    }

    private void OpenUpdateBackups_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_updates.UpdateBackupFolder}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to open update backups", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
