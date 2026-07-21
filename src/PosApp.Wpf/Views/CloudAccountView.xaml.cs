using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Data;
using PosApp.Services;
using PosApp.Wpf.Helpers;

namespace PosApp.Wpf.Views;

public partial class CloudAccountView : UserControl, IRefreshable
{
    private readonly ICloudAccountService _accounts;
    private readonly ICloudSyncService _sync;
    private readonly ICloudMigrationService _migration;
    private readonly ISettingsService _settings;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly LocalOrganizationProfileStore _profiles;
    private CloudAccountState? _state;
    private bool _busy;

    public CloudAccountView(
        ICloudAccountService accounts,
        ICloudSyncService sync,
        ICloudMigrationService migration,
        ISettingsService settings,
        IDbContextFactory<AppDbContext> dbFactory,
        LocalOrganizationProfileStore profiles)
    {
        InitializeComponent();
        _accounts = accounts;
        _sync = sync;
        _migration = migration;
        _settings = settings;
        _dbFactory = dbFactory;
        _profiles = profiles;
        var roles = new[]
        {
            new OnlineRoleChoice(Text("Cloud_RoleCashier", "Cashier"), UserRole.Cashier),
            new OnlineRoleChoice(Text("Cloud_RoleManager", "Manager"), UserRole.Manager),
            new OnlineRoleChoice(Text("Cloud_RoleAdministrator", "Administrator"), UserRole.Admin)
        };
        NewUserRoleCombo.ItemsSource = roles;
        NewUserRoleCombo.SelectedIndex = 0;
        OnlineUserRoleCombo.ItemsSource = roles;
        Loaded += (_, _) => _sync.StatusChanged += Sync_StatusChanged;
        Unloaded += (_, _) => _sync.StatusChanged -= Sync_StatusChanged;
    }

    public async Task RefreshAsync()
    {
        LoadOrganizationProfiles();
        _state = await _accounts.GetAccountStateAsync();
        ApplyStatus(_sync.CurrentStatus);
        if (_state == null || !_state.IsEnabled)
        {
            OrganizationText.Text = Text("Cloud_NotSignedIn", "Not signed in");
            EndpointText.Text = Text("Cloud_SignInFromLogin", "Sign in from the login window to enable online synchronization.");
            StoreCombo.ItemsSource = null;
            OnlineUsersGrid.ItemsSource = null;
            DevicesGrid.ItemsSource = null;
            OnlineUsersCard.Visibility = Visibility.Collapsed;
            CreateStorePanel.Visibility = Visibility.Collapsed;
            InitialMigrationCard.Visibility = Visibility.Collapsed;
            return;
        }

        OrganizationText.Text = _state.TenantName;
        EndpointText.Text = Text("Cloud_ManagedEndpoint", "Connected through the cloud service configured for this PosApp build.");
        CurrentStoreText.Text = _state.CurrentStoreName;
        ReconciliationCard.Visibility = _state.RequiresReconciliation ? Visibility.Visible : Visibility.Collapsed;
        ReconciliationBackupText.Text = string.IsNullOrWhiteSpace(_state.ReconciliationBackupPath)
            ? string.Empty
            : string.Format(Text("Cloud_ReconciliationBackup", "Safety backup: {0}"), _state.ReconciliationBackupPath);
        await LoadStoresAsync();
        OnlineUsersCard.Visibility = App.CurrentUser?.Role == UserRole.Admin
            ? Visibility.Visible : Visibility.Collapsed;
        CreateStorePanel.Visibility = App.CurrentUser?.Role == UserRole.Admin
            ? Visibility.Visible : Visibility.Collapsed;
        InitialMigrationCard.Visibility = App.CurrentUser?.Role == UserRole.Admin
            ? Visibility.Visible : Visibility.Collapsed;
        RevokeDeviceButton.Visibility = App.CurrentUser?.Role == UserRole.Admin
            ? Visibility.Visible : Visibility.Collapsed;
        AuthorizeDeviceButton.Visibility = App.CurrentUser?.Role == UserRole.Admin
            ? Visibility.Visible : Visibility.Collapsed;
        if (OnlineUsersCard.Visibility == Visibility.Visible) await LoadUsersAsync();
        await LoadDevicesAsync();
        await LoadConflictsAsync();
    }

    private void LoadOrganizationProfiles()
    {
        var profiles = _profiles.GetProfiles();
        OrganizationProfilesCombo.ItemsSource = profiles;
        OrganizationProfilesCombo.SelectedItem = profiles.FirstOrDefault(profile => profile.IsActive);
        OrganizationProfileHelpText.Text = string.Format(
            Text("Cloud_ProfileIsolationHelp",
                "{0} local organization profile(s). Each has an isolated database, device ID, backups, and encrypted session."),
            profiles.Count);
    }

    private async void SwitchOrganization_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || OrganizationProfilesCombo.SelectedItem is not LocalOrganizationProfile selected ||
            selected.IsActive) return;
        if (LocalizedMessageBox.Show(
                Text("Cloud_SwitchOrganizationConfirm",
                    "PosApp will save pending synchronized work in this profile and restart using the selected organization. Any unsaved current cart will be cleared. Continue?"),
                Text("Cloud_OrganizationProfiles", "Organization profiles"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RestartOrganizationProfileAsync(selected.Id, createProfile: false);
    }

    private async void AddOrganization_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (LocalizedMessageBox.Show(
                Text("Cloud_AddOrganizationConfirm",
                    "PosApp will create an empty, isolated local profile and restart. Your current database and pending offline work remain unchanged, but any unsaved current cart will be cleared. Continue?"),
                Text("Cloud_AddOrganization", "Add organization"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RestartOrganizationProfileAsync(string.Empty, createProfile: true);
    }

    private async Task RestartOrganizationProfileAsync(string profileId, bool createProfile)
    {
        _busy = true;
        IsEnabled = false;
        try
        {
            await OrganizationProfileSwitcher.SwitchAndRestartAsync(
                _profiles, _sync, profileId, createProfile);
        }
        catch (Exception exception)
        {
            App.LogError("Switch organization profile", exception);
            ErrorDetailsText.Text = Text("Cloud_ProfileRestartFailed",
                "PosApp could not restart into the selected organization profile. The current profile is still active.");
            LoadOrganizationProfiles();
            IsEnabled = true;
            _busy = false;
        }
    }

    private void Sync_StatusChanged(object? sender, CloudSyncStatus status)
        => Dispatcher.InvokeAsync(() => ApplyStatus(status));

    private void ApplyStatus(CloudSyncStatus status)
    {
        StatusLabel.Text = StatusText(status.State);
        StatusDot.Fill = status.IsSyncing
            ? Brush("InfoTextBrush", Brushes.DodgerBlue)
            : status.State == "up_to_date"
                ? Brush("SuccessTextBrush", Brushes.SeaGreen)
                : status.State is "offline" or "signed_out"
                    ? Brush("TextMutedBrush", Brushes.Gray)
                    : Brush("DangerTextBrush", Brushes.IndianRed);
        SyncProgress.Visibility = status.IsSyncing ? Visibility.Visible : Visibility.Collapsed;
        PendingText.Text = status.PendingUploadCount.ToString();
        ConflictCountText.Text = status.ConflictCount.ToString();
        LastSyncText.Text = status.LastSuccessfulSyncAtUtc?.ToLocalTime().ToString("g")
                            ?? Text("Cloud_Never", "Never");
        ErrorDetailsText.Text = string.IsNullOrWhiteSpace(status.LastErrorCode)
            ? string.Empty
            : FormatStructuredError(status.LastErrorCode, status.LastRequestId);
        ReconciliationCard.Visibility = status.RequiresReconciliation ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
        => await RunAsync(async () => { await _sync.SyncNowAsync(true); await RefreshAsync(); });

    private async void Retry_Click(object sender, RoutedEventArgs e)
        => await RunAsync(async () => { await _sync.RetryFailedAsync(); await RefreshAsync(); });

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e)
        => await RunAsync(LoadDevicesAsync);

    private async void RefreshUsers_Click(object sender, RoutedEventArgs e)
        => await RunAsync(LoadUsersAsync);

    private void OnlineUsersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OnlineUsersGrid.SelectedItem is not CloudUserProfile user) return;
        OnlineUserRoleCombo.SelectedItem = OnlineUserRoleCombo.Items.Cast<OnlineRoleChoice>()
            .FirstOrDefault(value => value.Role == user.Role);
        OnlineUserActiveCheck.IsChecked = user.IsActive;
    }

    private async void CreateUser_Click(object sender, RoutedEventArgs e)
    {
        if (NewUserRoleCombo.SelectedItem is not OnlineRoleChoice role ||
            NewUserStoreCombo.SelectedItem is not CloudStoreDto store) return;
        await RunAsync(async () =>
        {
            await _accounts.CreateUserAsync(new CloudUserCreateRequest
            {
                Username = NewUsernameBox.Text,
                Email = NewEmailBox.Text,
                FullName = NewFullNameBox.Text,
                Password = NewUserPasswordBox.Password,
                Role = role.Role,
                StoreId = store.Id
            });
            NewUsernameBox.Clear();
            NewEmailBox.Clear();
            NewFullNameBox.Clear();
            NewUserPasswordBox.Clear();
            await LoadUsersAsync();
            ErrorDetailsText.Text = Text("Cloud_UserCreated", "Online user created successfully.");
        });
    }

    private async void ApplyUserChanges_Click(object sender, RoutedEventArgs e)
    {
        if (OnlineUsersGrid.SelectedItem is not CloudUserProfile user ||
            OnlineUserRoleCombo.SelectedItem is not OnlineRoleChoice role) return;
        var active = OnlineUserActiveCheck.IsChecked == true;
        if (!active && user.IsActive && LocalizedMessageBox.Show(
                Text("Cloud_DeactivateUserConfirm", "Deactivate this online user and revoke their active sessions?"),
                Text("Cloud_OnlineUsers", "Online users"), MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync(async () =>
        {
            await _accounts.UpdateUserAsync(user.Id, new CloudUserUpdateRequest
            {
                Role = role.Role,
                IsActive = active
            });
            await LoadUsersAsync();
            ErrorDetailsText.Text = Text("Cloud_UserUpdated", "Online user access updated successfully.");
        });
    }

    private async Task LoadStoresAsync()
    {
        var stores = await _accounts.GetStoresAsync();
        var activeStores = stores.Where(store => store.IsActive).ToArray();
        StoreCombo.ItemsSource = activeStores;
        StoreCombo.SelectedItem = stores.FirstOrDefault(store => store.Id == _state?.CurrentStoreId);
        NewUserStoreCombo.ItemsSource = activeStores;
        NewUserStoreCombo.SelectedItem = activeStores.FirstOrDefault(store => store.Id == _state?.CurrentStoreId)
                                             ?? activeStores.FirstOrDefault();
    }

    private async Task LoadUsersAsync()
        => OnlineUsersGrid.ItemsSource = await _accounts.GetUsersAsync();

    private async Task LoadDevicesAsync()
        => DevicesGrid.ItemsSource = await _accounts.GetDeviceSessionsAsync();

    private async Task LoadConflictsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        ConflictsGrid.ItemsSource = await db.SyncConflicts.AsNoTracking()
            .Where(value => value.Status == SyncConflictStatus.Unresolved)
            .OrderByDescending(value => value.DetectedAtUtc)
            .Take(200).ToListAsync();
    }

    private async void SwitchStore_Click(object sender, RoutedEventArgs e)
    {
        if (StoreCombo.SelectedItem is not CloudStoreDto store) return;
        if (store.Id == _state?.CurrentStoreId) return;
        if (LocalizedMessageBox.Show(Text("Cloud_SwitchStoreConfirm",
                "Switch store and download its synchronized data? Any unsaved current cart will be cleared."),
                Text("Cloud_Stores", "Stores"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await RunAsync(async () =>
        {
            await _accounts.SelectStoreAsync(store.Id);
            (Window.GetWindow(this) as MainWindow)?.ResetCurrentSaleForStoreSwitch();
            var settings = await _settings.GetStoreSettingsAsync();
            App.PublishSettings(settings);
            App.ApplyTheme(settings.Theme);
            App.ApplyLanguage(settings.Language);
            await RefreshAsync();
        });
    }

    private async void CreateStore_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var id = await _accounts.CreateStoreAsync(NewStoreNameBox.Text, NewStoreCodeBox.Text);
            NewStoreNameBox.Clear();
            NewStoreCodeBox.Clear();
            await LoadStoresAsync();
            StoreCombo.SelectedItem = StoreCombo.Items.Cast<CloudStoreDto>()
                .FirstOrDefault(store => store.Id == id);
            ErrorDetailsText.Text = Text("Cloud_StoreCreated", "Store created successfully. Select Switch store to use it.");
        });
    }

    private async void PreviewMigration_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var preview = await _migration.PreviewInitialMigrationAsync();
            var local = preview.LocalCounts.Sum(value => value.Value);
            var cloud = preview.CloudCounts.Sum(value => value.Value);
            MigrationSummaryText.Text = preview.CloudBusinessDataIsEmpty
                ? string.Format(Text("Cloud_MigrationReady", "Local records: {0}. Cloud records: {1}. The cloud account is ready for the initial upload."), local, cloud)
                : Text($"Cloud_Error_{preview.BlockingCode}", preview.BlockingReason ?? "Migration is blocked.");
            UploadExistingButton.IsEnabled = preview.CloudBusinessDataIsEmpty;
        });
    }

    private async void UploadExisting_Click(object sender, RoutedEventArgs e)
    {
        if (LocalizedMessageBox.Show(Text("Cloud_UploadExistingConfirm",
                "A verified local backup will be created before uploading existing records. Continue?"),
                Text("Cloud_InitialMigration", "Initial cloud migration"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync(async () =>
        {
            var result = await _migration.UploadExistingDataAsync();
            MigrationSummaryText.Text = string.Format(Text("Cloud_MigrationComplete",
                "Migration completed. {0} record groups were verified. Backup: {1}"),
                result.VerifiedCloudCounts.Count, result.BackupPath);
            UploadExistingButton.IsEnabled = false;
            await RefreshAsync();
        });
    }

    private async void RevokeSession_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is not CloudDeviceSessionDto session) return;
        if (LocalizedMessageBox.Show(Text("Cloud_RevokeConfirm", "Revoke the selected device session?"),
                Text("Cloud_DeviceSessions", "Device sessions"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        await RunAsync(async () => { await _accounts.RevokeDeviceSessionAsync(session.SessionId); await LoadDevicesAsync(); });
    }

    private async void RevokeDevice_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is not CloudDeviceSessionDto session) return;
        if (LocalizedMessageBox.Show(Text("Cloud_RevokeDeviceConfirm",
                "Revoke the selected device and all of its sessions? It cannot sign in again until re-authorized."),
                Text("Cloud_DeviceSessions", "Device sessions"), MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync(async () => { await _accounts.RevokeDeviceAsync(session.DeviceId); await LoadDevicesAsync(); });
    }

    private async void AuthorizeDevice_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is not CloudDeviceSessionDto session) return;
        if (LocalizedMessageBox.Show(Text("Cloud_AuthorizeDeviceConfirm",
                "Allow this registered device to create a new session again? Old revoked sessions remain revoked."),
                Text("Cloud_DeviceSessions", "Device sessions"), MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAsync(async () => { await _accounts.AuthorizeDeviceAsync(session.DeviceId); await LoadDevicesAsync(); });
    }

    private async void KeepLocal_Click(object sender, RoutedEventArgs e)
        => await ResolveSelectedConflictAsync(SyncConflictStatus.KeepLocal);

    private async void UseServer_Click(object sender, RoutedEventArgs e)
        => await ResolveSelectedConflictAsync(SyncConflictStatus.UseServer);

    private async Task ResolveSelectedConflictAsync(SyncConflictStatus resolution)
    {
        if (ConflictsGrid.SelectedItem is not SyncConflict conflict) return;
        await RunAsync(async () =>
        {
            await _sync.ResolveConflictAsync(conflict.Id, resolution);
            await LoadConflictsAsync();
            ApplyStatus(_sync.CurrentStatus);
        });
    }

    private async void UseServerAfterRestore_Click(object sender, RoutedEventArgs e)
    {
        if (LocalizedMessageBox.Show(Text("Cloud_UseServerConfirm",
                "Replace the local working copy with synchronized server data? The safety backup will remain available."),
                Text("Cloud_ReconciliationTitle", "Data reconciliation"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync(async () => { await _migration.AcceptServerAfterRestoreAsync(); await RefreshAsync(); });
    }

    private async void UploadRestored_Click(object sender, RoutedEventArgs e)
    {
        if (LocalizedMessageBox.Show(Text("Cloud_UploadRestoredConfirm",
                "Local data can be uploaded only when the cloud organization has no business data. Continue with validation?"),
                Text("Cloud_ReconciliationTitle", "Data reconciliation"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync(async () => { await _migration.PrepareRestoreAsNewCloudStateAsync(); await RefreshAsync(); });
    }

    private async void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            await _accounts.ChangePasswordAsync(CurrentPasswordBox.Password, NewPasswordBox.Password);
            CurrentPasswordBox.Clear();
            NewPasswordBox.Clear();
            ErrorDetailsText.Text = Text("Cloud_PasswordChanged", "Online password changed successfully.");
        });
    }

    private async void CloudLogout_Click(object sender, RoutedEventArgs e)
    {
        if (LocalizedMessageBox.Show(Text("Cloud_LogoutConfirm",
                "Securely sign out of the online account on this computer? Unsynchronized local work will remain on this computer."),
                Text("Cloud_SecureLogout", "Secure online logout"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync(async () => { await _accounts.LogoutAsync(); await RefreshAsync(); });
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        IsEnabled = false;
        try { await action(); }
        catch (CloudApiException exception) { ErrorDetailsText.Text = FormatStructuredError(exception.Code, exception.RequestId); }
        catch (ArgumentException exception) { ErrorDetailsText.Text = RuntimeUiText.Translate(exception.Message); }
        catch (InvalidOperationException exception) { ErrorDetailsText.Text = RuntimeUiText.Translate(exception.Message); }
        catch { ErrorDetailsText.Text = Text("Cloud_UnexpectedError", "The online request could not be completed. Local data remains safe."); }
        finally { IsEnabled = true; _busy = false; }
    }

    private string StatusText(string state) => state switch
    {
        "up_to_date" => Text("Cloud_StatusUpToDate", "Up to date"),
        "syncing" => Text("Cloud_StatusSyncing", "Synchronizing"),
        "offline" => Text("Cloud_StatusOffline", "Offline"),
        "signed_out" => Text("Cloud_StatusSignedOut", "Signed out"),
        "session_expired" => Text("Cloud_StatusExpired", "Session expired"),
        "revoked" => Text("Cloud_StatusRevoked", "Device revoked"),
        "reconciliation_required" => Text("Cloud_StatusReconciliation", "Reconciliation required"),
        "upgrade_required" => Text("Cloud_StatusUpgrade", "Update required"),
        "error" => Text("Cloud_StatusError", "Sync error"),
        _ => Text("Cloud_StatusReady", "Ready")
    };

    private string FriendlyCode(string code) => Text($"Cloud_Error_{code}", code.Replace('_', ' '));
    private string FormatStructuredError(string code, string? requestId)
        => string.IsNullOrWhiteSpace(requestId)
            ? FriendlyCode(code)
            : $"{FriendlyCode(code)} • {Text("Cloud_RequestId", "Request ID")}: {requestId}";
    private string Text(string key, string fallback) => TryFindResource(key) as string ?? fallback;
    private Brush Brush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private sealed record OnlineRoleChoice(string Label, UserRole Role);
}
