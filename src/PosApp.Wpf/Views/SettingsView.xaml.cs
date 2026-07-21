using System.Diagnostics;
using System.Drawing.Printing;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;

using PosApp.Core.Utilities;

namespace PosApp.Wpf.Views;

public partial class SettingsView : UserControl, IRefreshable
{
    private readonly ISettingsService _settings;
    private readonly IHardwareService _hardware;
    private readonly IBackupService _backup;
    private readonly IUpdateService _updates;
    private readonly ICloudSyncService _cloudSync;
    private StoreSettings _current = new();
    private bool _isLoading;
    private readonly SemaphoreSlim _appearanceSaveGate = new(1, 1);

    public SettingsView(ISettingsService settings, IHardwareService hardware, IBackupService backup,
        IUpdateService updates, ICloudSyncService cloudSync)
    {
        InitializeComponent();
        _settings = settings;
        _hardware = hardware;
        _backup = backup;
        _updates = updates;
        _cloudSync = cloudSync;
    }

    public async Task RefreshAsync()
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
            "email" => 4,
            "print" => 5,
            "database" => 6,
            "update" => 7,
            "about" => 8,
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
            CurrencyDecimalsBox.Text = Math.Clamp(_current.CurrencyDecimals, 0, 4).ToString();
            CountryBox.Text = _current.Country;
            FooterBox.Text = _current.FooterNote;
            ReceiptWidthBox.Text = Math.Clamp(_current.ReceiptWidth, 40, 120).ToString();
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
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to load settings", MessageBoxButton.OK, MessageBoxImage.Error);
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
            App.PublishSettings(await _settings.GetStoreSettingsAsync());
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to save appearance", MessageBoxButton.OK, MessageBoxImage.Error);
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
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Settings saved.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to save settings", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private StoreSettings BuildSettingsFromForm()
    {
        var candidate = JsonSerializer.Deserialize<StoreSettings>(JsonSerializer.Serialize(_current)) ?? new StoreSettings();
        candidate.StoreName = StoreNameBox.Text.Trim();
        if (candidate.StoreName.Length == 0) throw new InvalidOperationException("Store name is required.");
        candidate.Phone = PhoneBox.Text.Trim();
        candidate.Email = EmailBox.Text.Trim();
        candidate.TaxId = TaxIdBox.Text.Trim();
        candidate.Address = AddressBox.Text.Trim();
        candidate.CurrencySymbol = string.IsNullOrWhiteSpace(CurrencyBox.Text) ? "¤" : CurrencyBox.Text.Trim();
        candidate.CurrencyCode = string.IsNullOrWhiteSpace(CurrencyCodeBox.Text) ? "BDT" : CurrencyCodeBox.Text.Trim().ToUpperInvariant();
        candidate.Country = CountryBox.Text.Trim();
        candidate.FooterNote = FooterBox.Text.Trim();
        if (!int.TryParse(CurrencyDecimalsBox.Text, out var decimals) || decimals is < 0 or > 4)
            throw new InvalidOperationException("Currency decimal places must be a whole number from 0 to 4.");
        candidate.CurrencyDecimals = decimals;
        if (!int.TryParse(ReceiptWidthBox.Text, out var receiptWidth) || receiptWidth is < 40 or > 120)
            throw new InvalidOperationException("Receipt width must be a whole number from 40 to 120 mm.");
        candidate.ReceiptWidth = receiptWidth;
        candidate.PrintReceiptAutomatically = false;
        candidate.ReceiptPrinterName = PrinterCombo.SelectedItem as string == "(default)" ? string.Empty : PrinterCombo.SelectedItem as string ?? string.Empty;
        candidate.Language = LangBn.IsChecked == true ? "bn" : "en";
        candidate.Theme = ThemeDark.IsChecked == true ? "Dark" : "Light";
        candidate.AutomaticBackupEnabled = AutoBackupCheckbox.IsChecked == true;
        candidate.BackupOnStartup = BackupStartupCheckbox.IsChecked == true;
        candidate.BackupOnExit = BackupExitCheckbox.IsChecked == true;
        candidate.DefaultServiceType = (DefaultServiceCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Retail";
        if (!FormattingUtilities.TryParseDecimal(DefaultTaxBox.Text, out var defaultTax) || defaultTax is < 0m or > 100m)
            throw new InvalidOperationException("Default tax must be from 0 to 100.");
        candidate.DefaultTaxRate = defaultTax;
        candidate.RequireOpenRegisterForSales = RequireOpenRegisterCheckbox.IsChecked == true;
        candidate.ConfirmBeforeVoidingOrder = ConfirmVoidCheckbox.IsChecked != false;
        candidate.ShowCashInOnStartup = false;
        candidate.SelectBusinessDayOnStartup = false;
        candidate.EnableVirtualKeyboard = VirtualKeyboardCheckbox.IsChecked == true;
        if (!int.TryParse(GridRowsBox.Text, out var rows) || rows is < 2 or > 10)
            throw new InvalidOperationException("Product rows must be a whole number from 2 to 10.");
        if (!int.TryParse(GridColumnsBox.Text, out var columns) || columns is < 2 or > 10)
            throw new InvalidOperationException("Product columns must be a whole number from 2 to 10.");
        if (!int.TryParse(MessageDurationBox.Text, out var seconds) || seconds is < 1 or > 60)
            throw new InvalidOperationException("Message duration must be a whole number from 1 to 60 seconds.");
        candidate.ProductGridRows = rows;
        candidate.ProductGridColumns = columns;
        candidate.MessageDurationSeconds = seconds;
        var scaleText = (UiScaleCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()?.TrimEnd('%');
        candidate.UiScalePercent = int.TryParse(scaleText, out var scale) ? Math.Clamp(scale, 90, 125) : 100;
        if (!int.TryParse(BackupRetentionBox.Text, out var retention) || retention is < 1 or > 200)
            throw new InvalidOperationException("Backup retention must be a whole number from 1 to 200.");
        candidate.BackupRetentionCount = retention;
        return candidate;
    }

    private async Task SaveCurrentSettingsAsync()
    {
        var candidate = BuildSettingsFromForm();
        await _appearanceSaveGate.WaitAsync();
        try
        {
            await _settings.SetStoreSettingsAsync(candidate);
            _current = await _settings.GetStoreSettingsAsync();
            App.PublishSettings(_current);
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
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ok ? "Print sent." : "Print failed. Check the selected printer and Windows print service.",
                "Test Print", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Test Print", MessageBoxButton.OK, MessageBoxImage.Error);
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
            SaleDate = DateTime.UtcNow,
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
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show($"Backup created successfully.\n\n{path}", "Backup",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to create backup", MessageBoxButton.OK, MessageBoxImage.Error);
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
        if (PosApp.Wpf.Helpers.LocalizedMessageBox.Show(
                "The selected backup will replace the current data on the next start. PosApp will preserve a safety copy of the current database and then close. Continue?",
                "Restore Backup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            IsEnabled = false;
            await _backup.StageRestoreAsync(dialog.FileName);
            App.RequestDatabaseRestoreShutdown();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _cloudSync.StopAsync(timeout.Token);
                await _cloudSync.WaitForIdleAsync(timeout.Token);
            }
            catch (Exception stopError)
            {
                // Startup also waits for an exclusive database lease. Keep closing
                // even if a network request needed the full bounded shutdown time.
                App.LogError("Cloud sync did not drain before database restore shutdown", stopError);
            }
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Backup validated and staged. PosApp will now close. Wait until it has closed completely, then start it again to finish the full replacement.",
                "Restore Ready", MessageBoxButton.OK, MessageBoxImage.Information);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to restore backup", MessageBoxButton.OK, MessageBoxImage.Error);
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
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to open backup folder", MessageBoxButton.OK, MessageBoxImage.Error);
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

        var completed = last.CompletedAtUtc.HasValue ? DateTimeUtilities.ToLocal(last.CompletedAtUtc.Value).ToString("g") : "unknown time";
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
                $"{package.FileName}\nVersion {package.TargetVersion} • {sizeMb:0.0} MB\n" +
                $"Publisher: {package.Publisher}\nSHA-256: {package.Sha256}";
            IsEnabled = true;

            var confirmation =
                $"Update PosApp {package.CurrentVersion} to {package.TargetVersion}?\n\n" +
                "Before opening the installer, PosApp will create and validate a complete SQLite backup. " +
                "The live database remains under your Windows profile and is not stored in Program Files.\n\n" +
                $"Recovery backups:\n{_updates.UpdateBackupFolder}\n\n" +
                $"Windows verified the installer's digital signature from:\n{package.Publisher}\n\n" +
                "PosApp will close after the installer starts.";
            if (PosApp.Wpf.Helpers.LocalizedMessageBox.Show(confirmation, "Safe PosApp Update",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            IsEnabled = false;
            await _updates.PrepareAndLaunchAsync(package.InstallerPath);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            IsEnabled = true;
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Update stopped", MessageBoxButton.OK, MessageBoxImage.Error);
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
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to open update backups", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
