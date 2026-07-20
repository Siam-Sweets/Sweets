using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Wpf.Helpers;

namespace PosApp.Wpf.Views;

public partial class MainWindow : Window
{
    private readonly PosView _pos;
    private readonly DashboardView _dashboard;
    private readonly ProductsView _products;
    private readonly PromotionsView _promotions;
    private readonly InventoryView _inventory;
    private readonly CustomersView _customers;
    private readonly SalesView _sales;
    private readonly ReportsView _reports;
    private readonly UsersView _users;
    private readonly SettingsView _settings;
    private readonly PurchasesView _purchases;
    private readonly RegisterView _register;
    private readonly CloudAccountView _cloud;
    private readonly ICloudSyncService _cloudSync;
    private readonly ISettingsService _settingsService;
    private bool _fullScreen;
    private WindowStyle _windowedStyle;
    private WindowState _windowedState;
    private ResizeMode _windowedResizeMode;
    private bool _windowedTopmost;
    private bool _terminalSessionHandled;
    private Rect _windowedBounds;

    public MainWindow(PosView pos, DashboardView dashboard, ProductsView products, PromotionsView promotions,
        InventoryView inventory, CustomersView customers, SalesView sales,
        ReportsView reports, UsersView users, SettingsView settings,
        PurchasesView purchases, RegisterView register, CloudAccountView cloud,
        ICloudSyncService cloudSync, ISettingsService settingsService)
    {
        InitializeComponent();
        _windowedStyle = WindowStyle;
        _windowedState = WindowState;
        _windowedResizeMode = ResizeMode;
        _windowedTopmost = Topmost;
        _windowedBounds = RestoreBounds;
        _pos = pos;
        _dashboard = dashboard;
        _products = products;
        _promotions = promotions;
        _inventory = inventory;
        _customers = customers;
        _sales = sales;
        _reports = reports;
        _users = users;
        _settings = settings;
        _purchases = purchases;
        _register = register;
        _cloud = cloud;
        _cloudSync = cloudSync;
        _settingsService = settingsService;
        _cloudSync.StatusChanged += CloudSync_StatusChanged;
        Closed += (_, _) => _cloudSync.StatusChanged -= CloudSync_StatusChanged;
        UpdateCloudStatus(_cloudSync.CurrentStatus);
    }

    public void SetCurrentUser(User user)
    {
        UserName.Text = user.FullName;
        UserRoleLabel.Text = user.Role.ToString();
        UserInitials.Text = string.IsNullOrWhiteSpace(user.FullName) ? "?" : user.FullName[..1].ToUpperInvariant();
        DrawerTitle.Text = $"POS – {user.FullName}";
        DrawerDate.Text = DateTime.Now.ToString("D");
        ApplyUiScale(App.StoreSettings.UiScalePercent);

        var manager = user.Role >= UserRole.Manager;
        var admin = user.Role >= UserRole.Admin;
        NavDashboard.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        NavProducts.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        NavInventory.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        NavPurchases.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        NavCustomers.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        NavReports.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        NavPromotions.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        NavUsers.Visibility = admin ? Visibility.Visible : Visibility.Collapsed;
        NavSettings.Visibility = admin ? Visibility.Visible : Visibility.Collapsed;
        NavCloud.Visibility = Visibility.Visible;
        DrawerManagement.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        DrawerReports.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;

        NavigateTo("pos");
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag }) NavigateTo(tag);
    }

    public void NavigateTo(string tag)
    {
        string? settingsSection = null;
        if (tag.StartsWith("settings:", StringComparison.OrdinalIgnoreCase))
        {
            settingsSection = tag[(tag.IndexOf(':') + 1)..];
            tag = "settings";
        }
        if (!IsAuthorized(tag))
        {
            LocalizedMessageBox.Show("Your account does not have permission to open this page.",
                "Access denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var view = tag switch
        {
            "pos" => (UserControl)_pos,
            "dashboard" => _dashboard,
            "register" => _register,
            "products" => _products,
            "promotions" => _promotions,
            "inventory" => _inventory,
            "purchases" => _purchases,
            "customers" => _customers,
            "sales" => _sales,
            "reports" => _reports,
            "users" => _users,
            "settings" => _settings,
            "cloud" => _cloud,
            _ => null
        };
        if (view == null) return;

        var contentChanged = !ReferenceEquals(ContentArea.Content, view);
        if (contentChanged) ContentArea.Content = view;
        if (settingsSection != null) _settings.SelectSection(settingsSection);
        SidebarColumn.Width = tag == "pos" ? new GridLength(0) : new GridLength(244);
        CloseManagementDrawer();

        var newActive = tag switch
        {
            "dashboard" => NavDashboard,
            "register" => NavRegister,
            "products" => NavProducts,
            "promotions" => NavPromotions,
            "inventory" => NavInventory,
            "purchases" => NavPurchases,
            "customers" => NavCustomers,
            "sales" => NavSales,
            "reports" => NavReports,
            "users" => NavUsers,
            "settings" => NavSettings,
            "cloud" => NavCloud,
            _ => null
        };
        ResetNavigationStyles();
        if (newActive != null)
            newActive.Style = (Style)FindResource("NavButtonActive");

        if (contentChanged && view.IsEnabled && view is IRefreshable refreshable)
            _ = RefreshSafelyAsync(refreshable);
    }


    private static async Task RefreshSafelyAsync(IRefreshable refreshable)
    {
        try
        {
            await refreshable.RefreshAsync();
        }
        catch (Exception ex)
        {
            App.LogError("View refresh", ex);
            LocalizedMessageBox.Show(ex.GetBaseException().Message, "Unable to refresh page",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }


    private static bool IsAuthorized(string tag)
    {
        var role = App.CurrentUser?.Role ?? UserRole.Cashier;
        return tag.ToLowerInvariant() switch
        {
            "pos" or "register" => true,
            "dashboard" or "products" or "promotions" or "inventory" or
            "purchases" or "customers" or "sales" or "reports" => role >= UserRole.Manager,
            "users" or "settings" => role >= UserRole.Admin,
            "cloud" => true,
            _ => false
        };
    }

    private void ResetNavigationStyles()
    {
        var inactiveStyle = (Style)FindResource("NavButton");
        foreach (var button in new[]
                 {
                     NavDashboard, NavSales, NavProducts, NavInventory, NavPurchases,
                     NavRegister, NavCustomers, NavReports, NavPromotions, NavUsers, NavSettings,
                     NavCloud
                 })
        {
            button.Style = inactiveStyle;
        }
    }

    public void ToggleManagementDrawer()
    {
        // Re-publish the active palette before revealing the drawer so an
        // appearance change can never leave a stale dark panel in Light mode.
        App.ApplyTheme(App.StoreSettings.Theme);
        DrawerDate.Text = DateTime.Now.ToString("D");
        ManagementOverlay.Visibility = ManagementOverlay.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
    }

    public void CloseManagementDrawer() => ManagementOverlay.Visibility = Visibility.Collapsed;

    public void ResetCurrentSaleForStoreSwitch()
    {
        if (_pos.DataContext is PosViewModel viewModel)
            viewModel.ResetForStoreSwitch();
    }

    public void ApplyUiScale(int percent)
    {
        var scale = Math.Clamp(percent, 90, 125) / 100d;
        RootGrid.LayoutTransform = new ScaleTransform(scale, scale);
    }

    private void CloseManagementDrawer_Click(object sender, RoutedEventArgs e) => CloseManagementDrawer();
    private void DrawerBackground_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseManagementDrawer();

    private void ManagementAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag }) NavigateTo(tag);
    }

    private async void OpenSales_Click(object sender, RoutedEventArgs e)
    {
        CloseManagementDrawer();
        NavigateTo("pos");
        if (_pos.DataContext is PosViewModel vm) await vm.ShowSuspendedAsync();
    }

    private void UserInfo_Click(object sender, RoutedEventArgs e)
    {
        var user = App.CurrentUser;
        if (user == null) return;
        var connection = CloudStatusText.Text;
        LocalizedMessageBox.Show($"{user.FullName}\nUsername: {user.Username}\nRole: {user.Role}\n\n{connection}",
            "User Information", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CloudSync_StatusChanged(object? sender, CloudSyncStatus status)
        => Dispatcher.InvokeAsync(() =>
        {
            UpdateCloudStatus(status);
            if (status.State == "up_to_date" && status.DownloadedChangeCount > 0)
                _ = ApplyDownloadedChangesAsync();
            if (IsVisible && App.CurrentUser != null && IsTerminalCloudSessionError(status.LastErrorCode))
                EndTerminalCloudSession(status.LastErrorCode!);
        });

    private static bool IsTerminalCloudSessionError(string? code) => code is
        "DEVICE_REVOKED" or "USER_DISABLED" or "ORGANIZATION_DISABLED" or "STORE_DISABLED" or
        "SESSION_REVOKED" or "REFRESH_TOKEN_REVOKED" or "REFRESH_TOKEN_EXPIRED" or "REFRESH_TOKEN_REUSE";

    private void EndTerminalCloudSession(string code)
    {
        if (_terminalSessionHandled) return;
        _terminalSessionHandled = true;
        var reason = TryFindResource($"Cloud_Error_{code}") as string
                     ?? TryFindResource("Cloud_StatusExpired") as string
                     ?? "The online session is no longer active.";
        var format = TryFindResource("Cloud_SessionEndedNotice") as string
                     ?? "{0}\n\nThis active session has ended. Return to sign-in to continue safely.";
        LocalizedMessageBox.Show(string.Format(format, reason),
            TryFindResource("Cloud_StatusExpired") as string ?? "Online session expired",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        SignOut();
    }

    private async Task ApplyDownloadedChangesAsync()
    {
        try
        {
            // Settings are synchronized records too. Publish the selected
            // branch's current copy before refreshing the visible page so
            // receipts, totals, language, theme, and printer state update live.
            var settings = await _settingsService.GetStoreSettingsAsync();
            App.PublishSettings(settings);
            App.ApplyTheme(settings.Theme);
            App.ApplyLanguage(settings.Language);
            if (ContentArea.Content is IRefreshable refreshable)
                await refreshable.RefreshAsync();
        }
        catch (Exception exception)
        {
            App.LogError("Apply downloaded cloud changes", exception);
        }
    }

    private void UpdateCloudStatus(CloudSyncStatus status)
    {
        var key = status.State switch
        {
            "up_to_date" => "Cloud_StatusUpToDate",
            "syncing" => "Cloud_StatusSyncing",
            "signed_out" => "Cloud_StatusSignedOut",
            "session_expired" => "Cloud_StatusExpired",
            "revoked" => "Cloud_StatusRevoked",
            "reconciliation_required" => "Cloud_StatusReconciliation",
            "upgrade_required" => "Cloud_StatusUpgrade",
            "error" => "Cloud_StatusError",
            "ready" => "Cloud_StatusReady",
            _ => "Cloud_StatusOffline"
        };
        CloudStatusText.Text = TryFindResource(key) as string ?? status.State;
        DrawerConnectionStatus.Text = CloudStatusText.Text;
        CloudSyncProgress.Visibility = status.IsSyncing ? Visibility.Visible : Visibility.Collapsed;
        var pendingLabel = TryFindResource("Cloud_PendingShort") as string ?? "Pending";
        var conflictLabel = TryFindResource("Cloud_ConflictsShort") as string ?? "Conflicts";
        var last = status.LastSuccessfulSyncAtUtc?.ToLocalTime().ToString("g")
                   ?? (TryFindResource("Cloud_Never") as string ?? "Never");
        CloudStatusDetails.Text = $"{pendingLabel}: {status.PendingUploadCount}  •  {conflictLabel}: {status.ConflictCount}  •  {last}";
        CloudStatusDot.Fill = status.IsSyncing
            ? FindBrush("InfoTextBrush", Brushes.DodgerBlue)
            : status.State == "up_to_date"
                ? FindBrush("SuccessTextBrush", Brushes.SeaGreen)
                : status.State is "offline" or "signed_out"
                    ? FindBrush("TextMutedBrush", Brushes.Gray)
                    : FindBrush("DangerTextBrush", Brushes.IndianRed);
    }

    private Brush FindBrush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private void OpenCloud_Click(object sender, RoutedEventArgs e) => NavigateTo("cloud");

    private async void ManualCloudSync_Click(object sender, RoutedEventArgs e)
    {
        try { await _cloudSync.SyncNowAsync(true); }
        catch
        {
            LocalizedMessageBox.Show(
                TryFindResource("Cloud_UnexpectedError") as string ?? "Synchronization could not be completed. Local data remains safe.",
                TryFindResource("Cloud_OnlineSync") as string ?? "Online synchronization",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DrawerFullscreen_Click(object sender, RoutedEventArgs e)
    {
        SetFullScreen(!_fullScreen);
        CloseManagementDrawer();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F11)
        {
            SetFullScreen(!_fullScreen);
            CloseManagementDrawer();
            e.Handled = true;
        }
        else if (key == Key.Escape && _fullScreen)
        {
            SetFullScreen(false);
            CloseManagementDrawer();
            e.Handled = true;
        }
    }

    private void SetFullScreen(bool enabled)
    {
        if (_fullScreen == enabled) return;

        if (enabled)
        {
            _windowedStyle = WindowStyle;
            _windowedState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
            _windowedResizeMode = ResizeMode;
            _windowedTopmost = Topmost;
            _windowedBounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;

            // Changing the chrome of an already maximized WPF window is not
            // reliably applied by Windows. Return to Normal first, then enter
            // a borderless maximized state so both the title bar and taskbar
            // are removed consistently.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            WindowState = WindowState.Maximized;
            _fullScreen = true;
        }
        else
        {
            WindowState = WindowState.Normal;
            Topmost = _windowedTopmost;
            WindowStyle = _windowedStyle;
            ResizeMode = _windowedResizeMode;

            if (_windowedState == WindowState.Maximized)
            {
                WindowState = WindowState.Maximized;
            }
            else
            {
                Left = _windowedBounds.Left;
                Top = _windowedBounds.Top;
                Width = _windowedBounds.Width;
                Height = _windowedBounds.Height;
                WindowState = WindowState.Normal;
            }
            _fullScreen = false;
        }

        DrawerFullscreen.SetResourceReference(
            ContentControl.ContentProperty,
            _fullScreen ? "Shell_ExitFullScreen" : "Shell_FullScreen");
    }

    private void DrawerExit_Click(object sender, RoutedEventArgs e)
    {
        if (LocalizedMessageBox.Show("Exit PosApp?", "Exit", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Close();
    }

    private void DrawerSignOut_Click(object sender, RoutedEventArgs e) => SignOut();
    private void Logout_Click(object sender, RoutedEventArgs e) => SignOut();

    public void SignOut()
    {
        var signedInUser = App.CurrentUser;
        Microsoft.Extensions.DependencyInjection.IServiceScope? previousSession = null;
        var newSessionCreated = false;
        try
        {
            var login = App.CreateLoginSession(out previousSession);
            newSessionCreated = true;
            App.CurrentUser = null;
            Application.Current.MainWindow = login;
            login.Show();
            Close();
            App.DisposePreviousSession(previousSession);
        }
        catch (Exception ex)
        {
            App.CurrentUser = signedInUser;
            if (newSessionCreated) App.RestorePreviousSession(previousSession);
            App.LogError("Sign out", ex);
            LocalizedMessageBox.Show(
                ex.GetBaseException().Message,
                "Unable to sign out",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}

public interface IRefreshable
{
    Task RefreshAsync();
}
