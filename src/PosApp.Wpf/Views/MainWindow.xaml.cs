using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PosApp.Core.Entities;

namespace PosApp.Wpf.Views;

public partial class MainWindow : Window
{
    private readonly PosView _pos;
    private readonly DashboardView _dashboard;
    private readonly ProductsView _products;
    private readonly PromotionsView _promotions;
    private readonly InventoryView _inventory;
    private readonly TransfersView _transfers;
    private readonly CustomersView _customers;
    private readonly SalesView _sales;
    private readonly ReportsView _reports;
    private readonly UsersView _users;
    private readonly SettingsView _settings;
    private readonly StoresView _stores;
    private readonly PurchasesView _purchases;
    private readonly RegisterView _register;
    private bool _fullScreen;
    private WindowStyle _windowedStyle;
    private WindowState _windowedState;
    private ResizeMode _windowedResizeMode;
    private bool _windowedTopmost;
    private Rect _windowedBounds;

    public MainWindow(PosView pos, DashboardView dashboard, ProductsView products, PromotionsView promotions,
        InventoryView inventory, TransfersView transfers, CustomersView customers, SalesView sales,
        ReportsView reports, UsersView users, SettingsView settings, StoresView stores,
        PurchasesView purchases, RegisterView register)
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
        _transfers = transfers;
        _customers = customers;
        _sales = sales;
        _reports = reports;
        _users = users;
        _settings = settings;
        _stores = stores;
        _purchases = purchases;
        _register = register;
        App.StoreChanged += App_StoreChanged;
        Closed += (_, _) => App.StoreChanged -= App_StoreChanged;
    }

    private void App_StoreChanged(object? sender, EventArgs e)
    {
        if (CurrentStoreName != null)
            CurrentStoreName.Text = App.CurrentStore?.Name ?? App.StoreSettings.StoreName;
    }

    public void SetCurrentUser(User user)
    {
        UserName.Text = user.FullName;
        UserRoleLabel.Text = user.Role.ToString();
        UserInitials.Text = string.IsNullOrWhiteSpace(user.FullName) ? "?" : user.FullName[..1].ToUpperInvariant();
        DrawerTitle.Text = $"POS – {user.FullName}";
        CurrentStoreName.Text = App.CurrentStore?.Name ?? App.StoreSettings.StoreName;
        DrawerDate.Text = DateTime.Now.ToString("D");
        ApplyUiScale(App.StoreSettings.UiScalePercent);

        var manager = user.Role >= UserRole.Manager;
        var admin = user.Role >= UserRole.Admin;
        NavDashboard.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        NavProducts.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        NavInventory.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        NavTransfers.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        NavPurchases.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        NavCustomers.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        NavReports.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        NavPromotions.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        NavUsers.Visibility = admin ? Visibility.Visible : Visibility.Collapsed;
        NavStores.Visibility = admin ? Visibility.Visible : Visibility.Collapsed;
        NavSettings.Visibility = admin ? Visibility.Visible : Visibility.Collapsed;
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
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Your account does not have permission to open this page.",
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
            "transfers" => _transfers,
            "purchases" => _purchases,
            "customers" => _customers,
            "sales" => _sales,
            "reports" => _reports,
            "users" => _users,
            "stores" => _stores,
            "settings" => _settings,
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
            "transfers" => NavTransfers,
            "purchases" => NavPurchases,
            "customers" => NavCustomers,
            "sales" => NavSales,
            "reports" => NavReports,
            "users" => NavUsers,
            "stores" => NavStores,
            "settings" => NavSettings,
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
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.GetBaseException().Message, "Unable to refresh page",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }


    private static bool IsAuthorized(string tag)
    {
        var role = App.CurrentUser?.Role ?? UserRole.Cashier;
        return tag.ToLowerInvariant() switch
        {
            "pos" or "register" => true,
            "dashboard" or "products" or "promotions" or "inventory" or "transfers" or
            "purchases" or "customers" or "sales" or "reports" => role >= UserRole.Manager,
            "users" or "stores" or "settings" => role >= UserRole.Admin,
            _ => false
        };
    }

    private void ResetNavigationStyles()
    {
        var inactiveStyle = (Style)FindResource("NavButton");
        foreach (var button in new[]
                 {
                     NavDashboard, NavSales, NavProducts, NavInventory, NavTransfers, NavPurchases,
                     NavRegister, NavCustomers, NavReports, NavPromotions, NavUsers, NavStores, NavSettings
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
        PosApp.Wpf.Helpers.LocalizedMessageBox.Show($"{user.FullName}\nUsername: {user.Username}\nRole: {user.Role}\n\nThis terminal is working offline.",
            "User Information", MessageBoxButton.OK, MessageBoxImage.Information);
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
        if (PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Exit PosApp?", "Exit", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
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
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(
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
