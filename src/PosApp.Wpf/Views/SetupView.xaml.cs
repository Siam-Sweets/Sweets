using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Wpf.Helpers;

namespace PosApp.Wpf.Views;

public partial class SetupView : Window
{
    private readonly ISetupService _setup;
    private readonly IServiceProvider _services;
    private StoreSettings _storeSettings = new();
    private bool _isLoading = true;

    public SetupView(ISetupService setup, IServiceProvider services)
    {
        InitializeComponent();
        _setup = setup;
        _services = services;
    }

    private async void SetupView_Loaded(object sender, RoutedEventArgs e)
    {
        FitToWorkArea();
        try
        {
            var defaults = await _setup.GetSetupDefaultsAsync();
            _storeSettings = defaults.StoreSettings;

            StoreNameBox.Text = string.Equals(_storeSettings.StoreName, "My Store", StringComparison.Ordinal)
                ? string.Empty
                : _storeSettings.StoreName;
            PhoneBox.Text = _storeSettings.Phone;
            AddressBox.Text = _storeSettings.Address;
            CurrencyBox.Text = string.IsNullOrWhiteSpace(_storeSettings.CurrencySymbol) ? "৳" : _storeSettings.CurrencySymbol;
            FooterBox.Text = _storeSettings.FooterNote;
            AdminNameBox.Text = defaults.AdminFullName;
            UsernameBox.Text = defaults.AdminUsername;
            LangBn.IsChecked = string.Equals(_storeSettings.Language, "bn", StringComparison.OrdinalIgnoreCase);
            LangEn.IsChecked = LangBn.IsChecked != true;
            ThemeDark.IsChecked = string.Equals(_storeSettings.Theme, "Dark", StringComparison.OrdinalIgnoreCase);
            ThemeLight.IsChecked = ThemeDark.IsChecked != true;
            BackupCheck.IsChecked = _storeSettings.AutomaticBackupEnabled;
            SampleProductsToggle.IsChecked = defaults.IncludeSampleProducts;
            StoreNameBox.Focus();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void FitToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        var availableWidth = Math.Max(520d, workArea.Width - 24d);
        var availableHeight = Math.Max(460d, workArea.Height - 24d);
        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        MaxWidth = availableWidth;
        MaxHeight = availableHeight;
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
    }

    private void Language_Checked(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        App.ApplyLanguage(LangBn.IsChecked == true ? "bn" : "en");
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        App.ApplyTheme(ThemeDark.IsChecked == true ? "Dark" : "Light");
    }

    private void PinBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = e.Text.Any(character => !char.IsDigit(character));

    private async void Finish_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        if (!string.Equals(PinBox.Password, ConfirmPinBox.Password, StringComparison.Ordinal))
        {
            ShowError(Application.Current.TryFindResource("Setup_PinMismatch") as string ??
                      "The administrator PINs do not match.");
            ConfirmPinBox.Focus();
            return;
        }

        FinishButton.IsEnabled = false;
        try
        {
            _storeSettings.StoreName = StoreNameBox.Text;
            _storeSettings.Phone = PhoneBox.Text;
            _storeSettings.Address = AddressBox.Text;
            _storeSettings.CurrencySymbol = CurrencyBox.Text;
            _storeSettings.FooterNote = FooterBox.Text;
            _storeSettings.Language = LangBn.IsChecked == true ? "bn" : "en";
            _storeSettings.Theme = ThemeDark.IsChecked == true ? "Dark" : "Light";
            _storeSettings.AutomaticBackupEnabled = BackupCheck.IsChecked == true;
            _storeSettings.BackupOnStartup = BackupCheck.IsChecked == true;
            _storeSettings.BackupOnExit = BackupCheck.IsChecked == true;

            var request = new InitialSetupRequest
            {
                StoreSettings = _storeSettings,
                AdminFullName = AdminNameBox.Text,
                AdminUsername = UsernameBox.Text,
                AdminPin = PinBox.Password,
                IncludeSampleProducts = SampleProductsToggle.IsChecked == true
            };

            await _setup.CompleteSetupAsync(request);
            App.PublishSettings(_storeSettings);
            App.ApplyLanguage(_storeSettings.Language);
            App.ApplyTheme(_storeSettings.Theme);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            FinishButton.IsEnabled = true;
        }
    }

    private async void OnlineAccount_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        var accountWindow = _services.GetRequiredService<CloudAccountWindow>();
        accountWindow.Owner = this;
        if (accountWindow.ShowDialog() != true || accountWindow.AuthenticationResult == null) return;

        OnlineAccountButton.IsEnabled = false;
        FinishButton.IsEnabled = false;
        try
        {
            var requiresInitialMigration = await _setup.CompleteOnlineSetupAsync(
                accountWindow.AuthenticationResult, accountWindow.CreatedOrganization);
            var accounts = _services.GetRequiredService<ICloudAccountService>();
            await accounts.InitializeCachedSessionAsync();

            if (requiresInitialMigration)
                await _services.GetRequiredService<ICloudMigrationService>().UploadExistingDataAsync();
            else
            {
                var status = await _services.GetRequiredService<ICloudSyncService>().SyncNowAsync(true);
                if (status.State != "up_to_date")
                    throw new InvalidOperationException(
                        Text("Setup_OnlineDownloadIncomplete", "The initial download is incomplete. Check the connection and sign in again to resume."));
            }

            await _setup.FinalizeOnlineSetupAsync();

            _storeSettings = await _services.GetRequiredService<ISettingsService>().GetStoreSettingsAsync();
            App.PublishSettings(_storeSettings);
            App.ApplyLanguage(_storeSettings.Language);
            App.ApplyTheme(_storeSettings.Theme);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            OnlineAccountButton.IsEnabled = true;
            FinishButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = RuntimeUiText.Translate(message);
        ErrorText.Visibility = Visibility.Visible;
    }

    private string Text(string key, string fallback)
        => TryFindResource(key) as string ?? fallback;

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
