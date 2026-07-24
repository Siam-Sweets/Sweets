using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Wpf.Helpers;

namespace PosApp.Wpf.Views;

public partial class SetupView : Window
{
    private readonly ISetupService _setup;
    private readonly ICloudAccountService _cloud;
    private readonly ICloudSyncService _sync;
    private StoreSettings _storeSettings = new();
    private bool _isLoading = true;
    private bool _busy;

    public SetupView(
        ISetupService setup,
        ICloudAccountService cloud,
        ICloudSyncService sync)
    {
        InitializeComponent();
        _setup = setup;
        _cloud = cloud;
        _sync = sync;
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
            CurrencyBox.Text = string.IsNullOrWhiteSpace(_storeSettings.CurrencySymbol)
                ? "৳"
                : _storeSettings.CurrencySymbol;
            FooterBox.Text = _storeSettings.FooterNote;
            AdminNameBox.Text = defaults.AdminFullName;
            UsernameBox.Text = defaults.AdminUsername;
            LangBn.IsChecked = string.Equals(
                _storeSettings.Language, "bn", StringComparison.OrdinalIgnoreCase);
            LangEn.IsChecked = LangBn.IsChecked != true;
            ThemeDark.IsChecked = string.Equals(
                _storeSettings.Theme, "Dark", StringComparison.OrdinalIgnoreCase);
            ThemeLight.IsChecked = ThemeDark.IsChecked != true;
            BackupCheck.IsChecked = _storeSettings.AutomaticBackupEnabled;
            SampleProductsToggle.IsChecked = defaults.IncludeSampleProducts;

            var account = await _cloud.GetStatusAsync();
            var preparedEmail = await _setup.GetPreparedOnlineAccountEmailAsync();
            if (account.IsConfigured)
            {
                SignInEmailBox.Text = account.Email;
                CreateAccountEmailBox.Text = string.IsNullOrWhiteSpace(preparedEmail)
                    ? account.Email
                    : preparedEmail;
            }
            else if (!string.IsNullOrWhiteSpace(preparedEmail))
            {
                CreateAccountEmailBox.Text = preparedEmail;
            }

            if (!string.IsNullOrWhiteSpace(preparedEmail))
            {
                SetupTabs.SelectedIndex = 1;
                StoreNameBox.Focus();
            }
            else
            {
                SetupTabs.SelectedIndex = 0;
                SignInEmailBox.Focus();
            }
        }
        catch (Exception ex)
        {
            ShowCreateStatus(ex.Message, isError: true);
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void FitToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        var availableWidth = Math.Max(620d, workArea.Width - 24d);
        var availableHeight = Math.Max(520d, workArea.Height - 24d);
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

    private async void SignInPasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SignInAndRestoreAsync();
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
        => await SignInAndRestoreAsync();

    private async Task SignInAndRestoreAsync()
    {
        if (_busy) return;

        ShowSignInStatus(
            Text("Setup_SignInProgress", "Signing in securely..."),
            isError: false);
        SetBusy(isBusy: true, signInOperation: true);
        try
        {
            await _cloud.SignInAsync(new CloudSignInRequest
            {
                Email = SignInEmailBox.Text,
                Password = SignInPasswordBox.Password
            });
            SignInPasswordBox.Clear();

            ShowSignInStatus(
                Text("Setup_DownloadProgress",
                    "Downloading all stores, users, inventory, settings, and transactions..."),
                isError: false);
            var restored = await _sync.RestoreLatestSnapshotsAsync(replaceLocalData: true);

            var message = string.Format(
                Text("Setup_RestoreComplete",
                    "Signed in and restored {0} store(s) with {1:N0} rows. " +
                    "PosApp will close now. Reopen it and use your existing local username and PIN."),
                restored.StoreCount,
                restored.RestoredRows);
            LocalizedMessageBox.Show(
                message,
                Text("Setup_RestoreCompleteTitle", "Existing account restored"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            SignInPasswordBox.Clear();
            ShowSignInStatus(
                RuntimeUiText.Translate(ex.GetBaseException().Message),
                isError: true);
            SignInPasswordBox.Focus();
        }
        finally
        {
            if (!Dispatcher.HasShutdownStarted)
                SetBusy(isBusy: false, signInOperation: true);
        }
    }

    private async void CreateAccount_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (!string.Equals(PinBox.Password, ConfirmPinBox.Password, StringComparison.Ordinal))
        {
            ShowCreateStatus(
                Text("Setup_PinMismatch", "The administrator PINs do not match."),
                isError: true);
            ConfirmPinBox.Focus();
            return;
        }

        var email = CreateAccountEmailBox.Text.Trim().ToLowerInvariant();
        CloudAccountStatus existingAccount;
        string? preparedEmail;
        try
        {
            existingAccount = await _cloud.GetStatusAsync();
            preparedEmail = await _setup.GetPreparedOnlineAccountEmailAsync();
        }
        catch (Exception ex)
        {
            ShowCreateStatus(
                RuntimeUiText.Translate(ex.GetBaseException().Message),
                isError: true);
            return;
        }

        var isResumingPreparedAccount =
            existingAccount.IsConfigured &&
            string.Equals(existingAccount.Email, email, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(preparedEmail, email, StringComparison.OrdinalIgnoreCase);

        if (existingAccount.IsConfigured && !isResumingPreparedAccount)
        {
            ShowCreateStatus(
                Text("Setup_PreparedAccountMismatch",
                    "This device is already connected to another online account. " +
                    "Use Sign in to restore that account."),
                isError: true);
            return;
        }

        if (!isResumingPreparedAccount &&
            !string.Equals(
                CreateAccountPasswordBox.Password,
                ConfirmAccountPasswordBox.Password,
                StringComparison.Ordinal))
        {
            ShowCreateStatus(
                Text("Setup_CloudPasswordMismatch", "The online account passwords do not match."),
                isError: true);
            ConfirmAccountPasswordBox.Focus();
            return;
        }

        PopulateStoreSettings();
        var request = new InitialSetupRequest
        {
            StoreSettings = _storeSettings,
            AdminFullName = AdminNameBox.Text,
            AdminUsername = UsernameBox.Text,
            AdminPin = PinBox.Password,
            IncludeSampleProducts = SampleProductsToggle.IsChecked == true
        };

        var completed = false;
        ShowCreateStatus(
            Text("Setup_CreateProgress", "Preparing the new online organization..."),
            isError: false);
        SetBusy(isBusy: true, signInOperation: false);
        try
        {
            await _setup.PrepareOnlineSetupAsync(request, email);

            if (!isResumingPreparedAccount)
            {
                await _cloud.SignUpAsync(new CloudSignUpRequest
                {
                    Email = email,
                    Password = CreateAccountPasswordBox.Password,
                    DisplayName = AdminNameBox.Text,
                    RegistrationKey = RegistrationKeyBox.Password
                });
            }

            CreateAccountPasswordBox.Clear();
            ConfirmAccountPasswordBox.Clear();
            RegistrationKeyBox.Clear();
            ShowCreateStatus(
                Text("Setup_UploadProgress",
                    "Uploading the complete store to the online organization..."),
                isError: false);

            await _cloud.UploadInitialSnapshotsAsync();
            await _setup.FinalizeOnlineSetupAsync();

            App.PublishSettings(_storeSettings);
            App.ApplyLanguage(_storeSettings.Language);
            App.ApplyTheme(_storeSettings.Theme);
            ShowCreateStatus(
                Text("Setup_CreateComplete",
                    "The organization was created and the complete store was synchronized."),
                isError: false);
            completed = true;
        }
        catch (Exception ex)
        {
            ShowCreateStatus(
                RuntimeUiText.Translate(ex.GetBaseException().Message),
                isError: true);
        }
        finally
        {
            if (!Dispatcher.HasShutdownStarted)
                SetBusy(isBusy: false, signInOperation: false);
        }

        if (completed)
        {
            LocalizedMessageBox.Show(
                Text("Setup_CreateComplete",
                    "The organization was created and the complete store was synchronized."),
                Text("Setup_CreateCompleteTitle", "Online setup complete"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            DialogResult = true;
        }
    }

    private void PopulateStoreSettings()
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
    }

    private void SetBusy(bool isBusy, bool signInOperation)
    {
        _busy = isBusy;
        SetupTabs.IsEnabled = !isBusy;
        ExitButton.IsEnabled = !isBusy;
        SignInButton.IsEnabled = !isBusy;
        CreateAccountButton.IsEnabled = !isBusy;
        SignInProgress.Visibility =
            isBusy && signInOperation ? Visibility.Visible : Visibility.Collapsed;
        CreateProgress.Visibility =
            isBusy && !signInOperation ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowSignInStatus(string message, bool isError)
        => ShowStatus(SignInStatusText, message, isError);

    private void ShowCreateStatus(string message, bool isError)
        => ShowStatus(CreateStatusText, message, isError);

    private static void ShowStatus(System.Windows.Controls.TextBlock target, string message, bool isError)
    {
        target.Text = message;
        target.Foreground = Application.Current.TryFindResource(
            isError ? "DangerBrush" : "TextMutedBrush") as Brush ?? Brushes.Gray;
        target.Visibility = Visibility.Visible;
    }

    private static string Text(string key, string fallback)
        => Application.Current.TryFindResource(key) as string ?? fallback;

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy)
            DialogResult = false;
    }
}
