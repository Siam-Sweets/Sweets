using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Data;
using PosApp.Services;
using PosApp.Wpf.Helpers;

namespace PosApp.Wpf.Views;

public partial class CloudAccountWindow : Window
{
    private static readonly Regex UsernamePattern = new(
        "^[a-z0-9][a-z0-9._-]{2,59}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex EmailPattern = new(
        "^[^\\s@]+@[^\\s@]+\\.[^\\s@]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex OfflinePinPattern = new(
        "^[0-9]{4,12}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ICloudAccountService _accounts;
    private readonly ISetupService _setup;
    private readonly ICloudSyncService _sync;
    private readonly ISettingsService _settings;
    private readonly LocalOrganizationProfileStore _profiles;
    private bool _busy;
    private StoreSettings? _requestedStoreSettings;
    private bool _includeSampleProducts;
    private string? _diagnosticAttemptId;

    public CloudAccountWindow(
        ICloudAccountService accounts,
        ISetupService setup,
        ICloudSyncService sync,
        ISettingsService settings,
        LocalOrganizationProfileStore profiles)
    {
        InitializeComponent();
        ConstrainToWorkingArea();
        _accounts = accounts;
        _setup = setup;
        _sync = sync;
        _settings = settings;
        _profiles = profiles;
        Loaded += CloudAccountWindow_Loaded;
    }

    public User? AuthenticatedUser { get; private set; }
    public CloudAuthenticationResult? AuthenticationResult { get; private set; }
    public bool CreatedOrganization { get; private set; }

    private void ConstrainToWorkingArea()
    {
        const double preferredWidth = 780;
        const double preferredHeight = 760;
        const double preferredMinWidth = 560;
        const double preferredMinHeight = 500;
        const double screenMargin = 16;

        var workArea = SystemParameters.WorkArea;
        var availableWidth = Math.Max(360, workArea.Width - screenMargin);
        var availableHeight = Math.Max(360, workArea.Height - screenMargin);

        MinWidth = Math.Min(preferredMinWidth, availableWidth);
        MinHeight = Math.Min(preferredMinHeight, availableHeight);
        MaxWidth = availableWidth;
        MaxHeight = availableHeight;
        Width = Math.Min(preferredWidth, availableWidth);
        Height = Math.Min(preferredHeight, availableHeight);
    }

    private async void CloudAccountWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoadOrganizationProfiles();
        LoginDeviceName.Text = Environment.MachineName;
        CreateDeviceName.Text = Environment.MachineName;

        try
        {
            var defaults = await _settings.GetStoreSettingsAsync();
            if (!string.Equals(defaults.StoreName, "My Store", StringComparison.OrdinalIgnoreCase))
            {
                CreateStoreName.Text = defaults.StoreName;
                CreateOrganizationName.Text = defaults.StoreName;
            }
            CreateStorePhone.Text = defaults.Phone;
            CreateStoreAddress.Text = defaults.Address;
            CreateCurrencySymbol.Text = string.IsNullOrWhiteSpace(defaults.CurrencySymbol)
                ? "৳"
                : defaults.CurrencySymbol;
            CreateReceiptFooter.Text = defaults.FooterNote;
            CreateLanguageBn.IsChecked = string.Equals(defaults.Language, "bn", StringComparison.OrdinalIgnoreCase);
            CreateLanguageEn.IsChecked = CreateLanguageBn.IsChecked != true;
            CreateThemeDark.IsChecked = string.Equals(defaults.Theme, "Dark", StringComparison.OrdinalIgnoreCase);
            CreateThemeLight.IsChecked = CreateThemeDark.IsChecked != true;
            CreateAutomaticBackup.IsChecked = defaults.AutomaticBackupEnabled;
            CreateSampleProducts.IsChecked = true;
        }
        catch (Exception exception)
        {
            App.LogError("Load online account defaults", exception);
        }

        if (!CloudDeploymentSettings.IsConfigured)
        {
            AccountTabs.IsEnabled = false;
            SetStatus(Text("Cloud_DeploymentEndpointMissing",
                CloudDeploymentSettings.ConfigurationError ??
                "Online synchronization is not configured for this PosApp build."), StatusKind.Error);
        }
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !ValidateLoginForm()) return;

        _requestedStoreSettings = null;
        _includeSampleProducts = false;
        await RunAsync(async () =>
        {
            var result = await _accounts.LoginAsync(new CloudLoginRequest
            {
                ApiBaseUrl = CloudDeploymentSettings.RequireApiBaseUrl(),
                UsernameOrEmail = LoginIdentifier.Text,
                Password = LoginPassword.Password,
                OfflinePin = LoginOfflinePin.Password,
                DeviceName = LoginDeviceName.Text
            });
            AuthenticationResult = result;
            CreatedOrganization = false;
            AuthenticatedUser = result.LocalUser;
        }, "Online account sign in");
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var linkedState = await _accounts.GetAccountStateAsync();
        if (!string.IsNullOrWhiteSpace(linkedState?.TenantId))
        {
            await AddOrganizationProfileAsync();
            return;
        }

        if (!ValidateCreateForm()) return;

        _requestedStoreSettings = BuildRequestedStoreSettings();
        _includeSampleProducts = CreateSampleProducts.IsChecked == true;
        await RunAsync(async () =>
        {
            var result = await _accounts.CreateOrganizationAsync(new CloudOrganizationRequest
            {
                ApiBaseUrl = CloudDeploymentSettings.RequireApiBaseUrl(),
                OrganizationName = CreateOrganizationName.Text,
                StoreName = CreateStoreName.Text,
                FullName = CreateFullName.Text,
                UsernameOrEmail = CreateUsername.Text,
                Email = CreateEmail.Text,
                Password = CreatePassword.Password,
                OfflinePin = CreateOfflinePin.Password,
                DeviceName = CreateDeviceName.Text
            });
            AuthenticationResult = result;
            CreatedOrganization = true;
            AuthenticatedUser = result.LocalUser;
        }, "Create online organization");
    }

    private void LoadOrganizationProfiles()
    {
        var profiles = _profiles.GetProfiles();
        OrganizationProfileCombo.ItemsSource = profiles;
        OrganizationProfileCombo.SelectedItem = profiles.FirstOrDefault(profile => profile.IsActive);
        var active = profiles.First(profile => profile.IsActive);
        ActiveProfileText.Text = string.Format(
            Text("Cloud_ActiveOrganizationProfile", "Active local profile: {0}"),
            active.DisplayName);
    }

    private async void SwitchOrganizationProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || OrganizationProfileCombo.SelectedItem is not LocalOrganizationProfile selected ||
            selected.IsActive) return;

        var answer = LocalizedMessageBox.Show(this,
            Text("Cloud_SwitchOrganizationConfirm",
                "PosApp will save pending synchronized work in this profile and restart using the selected organization. Any unsaved current cart will be cleared. Continue?"),
            Text("Cloud_OrganizationProfiles", "Organization profiles"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        await RestartIntoProfileAsync(selected.Id, createProfile: false);
    }

    private async void AddOrganizationProfile_Click(object sender, RoutedEventArgs e)
        => await AddOrganizationProfileAsync();

    private async Task AddOrganizationProfileAsync()
    {
        if (_busy) return;
        var answer = LocalizedMessageBox.Show(this,
            Text("Cloud_AddOrganizationConfirm",
                "PosApp will create an empty, isolated local profile and restart. Your current database and pending offline work remain unchanged, but any unsaved current cart will be cleared. Continue?"),
            Text("Cloud_AddOrganization", "Add organization"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        await RestartIntoProfileAsync(string.Empty, createProfile: true);
    }

    private async Task RestartIntoProfileAsync(string profileId, bool createProfile)
    {
        _busy = true;
        AccountTabs.IsEnabled = false;
        BusyBar.Visibility = Visibility.Visible;
        SetStatus(Text("Cloud_ProfileRestarting",
            "Saving this organization's local work and restarting PosApp..."), StatusKind.Busy);
        await RenderAsync();
        try
        {
            await OrganizationProfileSwitcher.SwitchAndRestartAsync(
                _profiles, _sync, profileId, createProfile);
        }
        catch (Exception exception)
        {
            App.LogError("Switch organization profile", exception);
            ShowOperationFailure(Text("Cloud_ProfileRestartFailed",
                "PosApp could not restart into the selected organization profile. The current profile is still active."));
            _busy = false;
            AccountTabs.IsEnabled = CloudDeploymentSettings.IsConfigured;
            BusyBar.Visibility = Visibility.Collapsed;
            LoadOrganizationProfiles();
        }
    }

    private StoreSettings BuildRequestedStoreSettings()
    {
        var automaticBackup = CreateAutomaticBackup.IsChecked == true;
        return new StoreSettings
        {
            StoreName = CreateStoreName.Text.Trim(),
            Phone = CreateStorePhone.Text.Trim(),
            Address = CreateStoreAddress.Text.Trim(),
            CurrencySymbol = CreateCurrencySymbol.Text.Trim(),
            CurrencyCode = "BDT",
            Country = "Bangladesh",
            FooterNote = CreateReceiptFooter.Text.Trim(),
            Language = CreateLanguageBn.IsChecked == true ? "bn" : "en",
            Theme = CreateThemeDark.IsChecked == true ? "Dark" : "Light",
            AutomaticBackupEnabled = automaticBackup,
            BackupOnStartup = automaticBackup,
            BackupOnExit = automaticBackup
        };
    }

    private bool ValidateLoginForm()
    {
        if (string.IsNullOrWhiteSpace(LoginIdentifier.Text))
            return ValidationFailure(Text("Cloud_ValidationUsernameOrEmail",
                "Username or email is required."), LoginIdentifier);

        if (!IsValidPassword(LoginPassword.Password))
            return ValidationFailure(PasswordRequirementText(), LoginPassword);

        if (!OfflinePinPattern.IsMatch(LoginOfflinePin.Password))
            return ValidationFailure(Text("Cloud_ValidationOfflinePin",
                "The offline PIN must contain 4 to 12 digits."), LoginOfflinePin);

        if (LoginDeviceName.Text.Trim().Length > 160)
            return ValidationFailure(Text("Cloud_ValidationDeviceName",
                "Device name must be 160 characters or fewer."), LoginDeviceName);

        return true;
    }

    private bool ValidateCreateForm()
    {
        if (string.IsNullOrWhiteSpace(CreateOrganizationName.Text) ||
            CreateOrganizationName.Text.Trim().Length > 160)
            return ValidationFailure(Text("Cloud_ValidationOrganizationName",
                    "Organization name is required and must be 160 characters or fewer."),
                CreateOrganizationName);

        if (string.IsNullOrWhiteSpace(CreateStoreName.Text) || CreateStoreName.Text.Trim().Length > 100)
            return ValidationFailure(Text("Cloud_ValidationStoreName",
                "Store name is required and must be 100 characters or fewer."), CreateStoreName);

        if (CreateStorePhone.Text.Trim().Length > 30)
            return ValidationFailure(Text("Cloud_ValidationStorePhone",
                "Store phone must be 30 characters or fewer."), CreateStorePhone);

        if (CreateStoreAddress.Text.Trim().Length > 500)
            return ValidationFailure(Text("Cloud_ValidationStoreAddress",
                "Store address must be 500 characters or fewer."), CreateStoreAddress);

        if (CreateCurrencySymbol.Text.Trim().Length is < 1 or > 8)
            return ValidationFailure(Text("Cloud_ValidationCurrencySymbol",
                "Currency symbol must contain 1 to 8 characters."), CreateCurrencySymbol);

        if (CreateReceiptFooter.Text.Trim().Length > 500)
            return ValidationFailure(Text("Cloud_ValidationReceiptFooter",
                "Receipt footer must be 500 characters or fewer."), CreateReceiptFooter);

        if (string.IsNullOrWhiteSpace(CreateFullName.Text) || CreateFullName.Text.Trim().Length > 100)
            return ValidationFailure(Text("Cloud_ValidationFullName",
                "Full name is required and must be 100 characters or fewer."), CreateFullName);

        if (!UsernamePattern.IsMatch(CreateUsername.Text.Trim()))
            return ValidationFailure(Text("Cloud_ValidationUsername",
                "Username must be 3 to 60 letters, numbers, dots, underscores, or hyphens."),
                CreateUsername);

        if (!EmailPattern.IsMatch(CreateEmail.Text.Trim()) || CreateEmail.Text.Trim().Length > 255)
            return ValidationFailure(Text("Cloud_ValidationEmail",
                "Enter a valid email address."), CreateEmail);

        if (!IsValidPassword(CreatePassword.Password))
            return ValidationFailure(PasswordRequirementText(), CreatePassword);

        if (CreatePassword.Password != CreatePasswordConfirm.Password)
            return ValidationFailure(Text("Cloud_PasswordMismatch",
                "The passwords do not match."), CreatePasswordConfirm);

        if (!OfflinePinPattern.IsMatch(CreateOfflinePin.Password))
            return ValidationFailure(Text("Cloud_ValidationOfflinePin",
                "The offline PIN must contain 4 to 12 digits."), CreateOfflinePin);

        if (CreateDeviceName.Text.Trim().Length > 160)
            return ValidationFailure(Text("Cloud_ValidationDeviceName",
                "Device name must be 160 characters or fewer."), CreateDeviceName);

        return true;
    }

    private static bool IsValidPassword(string password)
        => password.Length is >= 10 and <= 128 &&
           password.Any(char.IsLetter) &&
           password.Any(char.IsDigit);

    private string PasswordRequirementText()
        => Text("Cloud_ValidationPasswordLength",
            "The online password must contain 10 to 128 characters and include at least one letter and one number.");

    private bool ValidationFailure(string message, Control target)
    {
        SetStatus(message, StatusKind.Error);
        LocalizedMessageBox.Show(this, message,
            Text("Cloud_ValidationTitle", "Check account details"),
            MessageBoxButton.OK, MessageBoxImage.Warning);
        target.Focus();
        return false;
    }

    private async Task RunAsync(Func<Task> authenticationOperation, string logContext)
    {
        _diagnosticAttemptId = CloudDiagnosticLogger.CreateAttemptId();
        using var diagnosticScope = CloudDiagnosticLogger.BeginScope(_diagnosticAttemptId);
        _busy = true;
        AccountTabs.IsEnabled = false;
        BusyBar.Visibility = Visibility.Visible;
        OpenDiagnosticFolderButton.Visibility = Visibility.Collapsed;
        SetStatus(Text("Cloud_Connecting", "Connecting securely..."), StatusKind.Busy);
        await RenderAsync();

        try
        {
            await CloudDiagnosticLogger.WriteAsync("onboarding.authentication_started", "started",
                new Dictionary<string, object?>
                {
                    ["operation"] = _requestedStoreSettings != null
                        ? "create_organization"
                        : "sign_in",
                    ["setupAlreadyComplete"] = await _setup.IsSetupCompleteAsync()
                });
            await authenticationOperation();
            if (AuthenticatedUser == null || AuthenticationResult == null)
                throw new InvalidOperationException(
                    "The online account did not provide a protected local user profile.");

            await CloudDiagnosticLogger.WriteAsync("onboarding.authentication_completed", "success",
                new Dictionary<string, object?>
                {
                    ["createdOrganization"] = CreatedOrganization,
                    ["role"] = AuthenticationResult.User.Role,
                    ["permissionCount"] = AuthenticationResult.User.Permissions?.Count ?? 0
                });

            if (!await _setup.IsSetupCompleteAsync())
                await CompleteOnlineOnboardingAsync();

            await CloudDiagnosticLogger.WriteAsync("onboarding.completed", "success");
            DialogResult = true;
        }
        catch (CloudApiException exception)
        {
            await CloudDiagnosticLogger.WriteAsync("onboarding.api_failed", "error",
                new Dictionary<string, object?>
                {
                    ["errorCode"] = exception.Code,
                    ["requestId"] = exception.RequestId,
                    ["httpStatus"] = (int)exception.StatusCode
                }, exception);
            var message = string.IsNullOrWhiteSpace(exception.RequestId)
                ? FriendlyError(exception.Code, exception.Message)
                : $"{FriendlyError(exception.Code, exception.Message)} • {Text("Cloud_RequestId", "Request ID")}: {exception.RequestId}";
            ShowOperationFailure(message);
        }
        catch (ArgumentException exception)
        {
            await CloudDiagnosticLogger.WriteAsync("onboarding.validation_failed", "error", exception: exception);
            ShowOperationFailure(RuntimeUiText.Translate(exception.Message), MessageBoxImage.Warning);
        }
        catch (InvalidOperationException exception)
        {
            await CloudDiagnosticLogger.WriteAsync("onboarding.incomplete", "error", exception: exception);
            ShowOperationFailure(RuntimeUiText.Translate(exception.Message), MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            App.LogError(logContext, exception);
            await CloudDiagnosticLogger.WriteAsync("onboarding.unexpected_failure", "error", exception: exception);
            ShowOperationFailure(Text("Cloud_UnexpectedError",
                "The online account request could not be completed."));
        }
        finally
        {
            _busy = false;
            AccountTabs.IsEnabled = CloudDeploymentSettings.IsConfigured;
            BusyBar.Visibility = Visibility.Collapsed;
        }
    }

    private async Task CompleteOnlineOnboardingAsync()
    {
        var authentication = AuthenticationResult
                             ?? throw new InvalidOperationException("Online authentication is unavailable.");

        SetStatus(Text("Cloud_PreparingOnlineSetup",
            "Preparing this computer for the online organization..."), StatusKind.Busy);
        await RenderAsync();
        await CloudDiagnosticLogger.WriteAsync("onboarding.cache_preparation_started", "started",
            new Dictionary<string, object?> { ["createdOrganization"] = CreatedOrganization });

        // Startup may still be completing a cached-session sync from a previous
        // onboarding attempt. Stop future background scheduling and wait for
        // the active database cycle before resetting the disposable cache.
        await _sync.StopAsync();
        await CloudDiagnosticLogger.WriteAsync("onboarding.background_sync_stopped", "success");
        await _sync.WaitForIdleAsync();
        await CloudDiagnosticLogger.WriteAsync("onboarding.sync_idle_confirmed", "success");

        await _setup.CompleteOnlineSetupAsync(
            authentication,
            CreatedOrganization,
            _requestedStoreSettings,
            _includeSampleProducts);
        await CloudDiagnosticLogger.WriteAsync("onboarding.local_cache_prepared", "success");

        await _accounts.InitializeCachedSessionAsync();
        await CloudDiagnosticLogger.WriteAsync("onboarding.cached_session_initialized", "success");
        SetStatus(Text("Cloud_DownloadingCompleteStore",
            "Downloading all organization data to this computer..."), StatusKind.Busy);
        await RenderAsync();

        await CloudDiagnosticLogger.WriteAsync("onboarding.initial_sync_started", "started");
        var status = await _sync.SyncNowAsync(true);
        await CloudDiagnosticLogger.WriteStatusAsync("onboarding.initial_sync_finished", status,
            IsComplete(status, requireNoConflicts: true) ? "success" : "incomplete");
        if (status.State != "up_to_date" || status.PendingUploadCount != 0 || status.ConflictCount != 0)
            throw new InvalidOperationException(BuildIncompleteSyncMessage(status));

        await EnsureStoreConfigurationAsync(authentication.Store.Name, _requestedStoreSettings);

        await CloudDiagnosticLogger.WriteAsync("onboarding.finalization_started", "started");
        await _setup.FinalizeOnlineSetupAsync();
        var settings = await _settings.GetStoreSettingsAsync();
        App.PublishSettings(settings);
        App.ApplyLanguage(settings.Language);
        App.ApplyTheme(settings.Theme);
        SetStatus(Text("Cloud_OnlineSetupComplete",
            "Online setup and full synchronization completed successfully."), StatusKind.Normal);
        await RenderAsync();
        await CloudDiagnosticLogger.WriteAsync("onboarding.finalization_completed", "success");
    }

    private async Task EnsureStoreConfigurationAsync(string storeName, StoreSettings? requestedSettings)
    {
        if (!string.IsNullOrWhiteSpace(await _settings.GetAsync("store:config")))
        {
            await CloudDiagnosticLogger.WriteAsync("onboarding.store_configuration_present", "success");
            return;
        }

        await CloudDiagnosticLogger.WriteAsync("onboarding.store_configuration_created", "started");
        var settings = requestedSettings ?? new StoreSettings();
        settings.StoreName = storeName.Trim();
        await _settings.SetStoreSettingsAsync(settings);
        var status = await _sync.SyncNowAsync(true);
        await CloudDiagnosticLogger.WriteStatusAsync("onboarding.store_configuration_sync_finished", status,
            IsComplete(status, requireNoConflicts: false) ? "success" : "incomplete");
        if (status.State != "up_to_date" || status.PendingUploadCount != 0)
            throw new InvalidOperationException(BuildIncompleteSyncMessage(status));
    }

    private static bool IsComplete(CloudSyncStatus status, bool requireNoConflicts)
        => status.State == "up_to_date" && status.PendingUploadCount == 0 &&
           (!requireNoConflicts || status.ConflictCount == 0);

    private string BuildIncompleteSyncMessage(CloudSyncStatus status)
    {
        var summary = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Text("Cloud_SyncFailureSummary",
                "State: {0} • Pending uploads: {1} • Conflicts: {2} • Downloaded: {3} • Cursor: {4}"),
            status.State,
            status.PendingUploadCount,
            status.ConflictCount,
            status.DownloadedChangeCount,
            status.Cursor);
        var detail = string.Join(" • ", new[]
        {
            status.LastErrorCode,
            status.LastErrorMessage,
            string.IsNullOrWhiteSpace(status.LastRequestId)
                ? null
                : $"{Text("Cloud_RequestId", "Request ID")}: {status.LastRequestId}"
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var message = Text("Cloud_InitialDownloadIncomplete",
            "The complete organization synchronization did not finish. Sign in again to resume after checking the connection.");
        return string.IsNullOrWhiteSpace(detail)
            ? $"{message}\n\n{summary}"
            : $"{message}\n\n{summary}\n{detail}";
    }

    private Task RenderAsync()
        => Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render).Task;

    private void ShowOperationFailure(string message, MessageBoxImage icon = MessageBoxImage.Error)
    {
        var diagnosticId = _diagnosticAttemptId ?? Text("Cloud_DiagnosticUnavailable", "Unavailable");
        var diagnosticMessage =
            $"{message}\n\n{Text("Cloud_DiagnosticId", "Diagnostic ID")}: {diagnosticId}\n" +
            $"{Text("Cloud_DiagnosticLogSaved", "Diagnostic log")}: {CloudDiagnosticLogger.LogFilePath}";
        SetStatus($"{message} • {Text("Cloud_DiagnosticId", "Diagnostic ID")}: {diagnosticId}",
            StatusKind.Error);
        OpenDiagnosticFolderButton.Visibility = Visibility.Visible;
        LocalizedMessageBox.Show(this, diagnosticMessage,
            Text("Cloud_AccountTitle", "Online PosApp account"),
            MessageBoxButton.OK, icon);
    }

    private void OpenDiagnosticFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(CloudDiagnosticLogger.LogDirectoryPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = CloudDiagnosticLogger.LogDirectoryPath,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            App.LogError("Open cloud diagnostic folder", exception);
            LocalizedMessageBox.Show(this,
                Text("Cloud_OpenDiagnosticFolderFailed",
                    "The diagnostic log folder could not be opened."),
                Text("Cloud_AccountTitle", "Online PosApp account"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void SetStatus(string message, StatusKind kind)
    {
        StatusText.Text = message;
        var backgroundKey = kind switch
        {
            StatusKind.Error => "DangerSurfaceBrush",
            StatusKind.Busy => "InfoSurfaceBrush",
            _ => "CardBrush"
        };
        var foregroundKey = kind switch
        {
            StatusKind.Error => "DangerTextBrush",
            StatusKind.Busy => "InfoTextBrush",
            _ => "TextMutedBrush"
        };

        if (TryFindResource(backgroundKey) is Brush background)
            StatusBorder.Background = background;
        if (TryFindResource(foregroundKey) is Brush foreground)
            StatusText.Foreground = foreground;
    }

    private string FriendlyError(string code, string fallback)
    {
        if (string.Equals(code, "VALIDATION_ERROR", StringComparison.Ordinal))
            return fallback;

        var structured = TryFindResource($"Cloud_Error_{code}") as string;
        if (!string.IsNullOrWhiteSpace(structured)) return structured;
        return code switch
        {
            "NETWORK_UNAVAILABLE" => Text("Cloud_ErrorNoInternet", fallback),
            "NETWORK_TIMEOUT" => Text("Cloud_ErrorTimeout", fallback),
            "INVALID_CREDENTIALS" => Text("Cloud_ErrorInvalidCredentials", fallback),
            "LOGIN_RATE_LIMITED" => Text("Cloud_ErrorRateLimited", fallback),
            "DEVICE_REVOKED" => Text("Cloud_ErrorDeviceRevoked", fallback),
            "USER_DISABLED" => Text("Cloud_ErrorUserDisabled", fallback),
            "CLIENT_VERSION_INCOMPATIBLE" => Text("Cloud_ErrorUpgradeRequired", fallback),
            _ => fallback
        };
    }

    private string Text(string key, string fallback)
        => TryFindResource(key) as string ?? fallback;

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) Close();
    }

    private enum StatusKind
    {
        Normal,
        Busy,
        Error
    }
}
