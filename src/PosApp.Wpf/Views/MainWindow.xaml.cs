using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PosApp.Core.Entities;
using PosApp.Localization;

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
    private bool _fullScreen;

    public MainWindow(PosView pos, DashboardView dashboard, ProductsView products, PromotionsView promotions,
        InventoryView inventory, CustomersView customers, SalesView sales,
        ReportsView reports, UsersView users, SettingsView settings,
        PurchasesView purchases, RegisterView register)
    {
        InitializeComponent();
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
        DrawerManagement.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        DrawerReports.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        DrawerSettings.Visibility = admin ? Visibility.Visible : Visibility.Collapsed;

        NavigateTo("pos");
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LocalizationManager.Instance.CultureChanged += (_, _) => { };
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
            MessageBox.Show("Your account does not have permission to open this page.",
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
            MessageBox.Show(ex.GetBaseException().Message, "Unable to refresh page",
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
            _ => false
        };
    }

    private void ResetNavigationStyles()
    {
        var inactiveStyle = (Style)FindResource("NavButton");
        foreach (var button in new[]
                 {
                     NavDashboard, NavSales, NavProducts, NavInventory, NavPurchases,
                     NavRegister, NavCustomers, NavReports, NavPromotions, NavUsers, NavSettings
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

    private void DrawerSettings_Click(object sender, RoutedEventArgs e) => NavigateTo("settings");

    private void UserInfo_Click(object sender, RoutedEventArgs e)
    {
        var user = App.CurrentUser;
        if (user == null) return;
        MessageBox.Show($"{user.FullName}\nUsername: {user.Username}\nRole: {user.Role}\n\nThis terminal is working offline.",
            "User Information", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void DrawerFullscreen_Click(object sender, RoutedEventArgs e)
    {
        _fullScreen = !_fullScreen;
        if (_fullScreen)
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Maximized;
        }
        CloseManagementDrawer();
    }

    private void DrawerExit_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Exit PosApp?", "Exit", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Close();
    }

    private void DrawerSignOut_Click(object sender, RoutedEventArgs e) => SignOut();
    private void Logout_Click(object sender, RoutedEventArgs e) => SignOut();

    public void SignOut()
    {
        App.CurrentUser = null;
        var login = App.Services.GetService(typeof(LoginView)) as LoginView;
        if (login == null) return;
        Application.Current.MainWindow = login;
        login.Show();
        Close();
    }
}

public interface IRefreshable
{
    Task RefreshAsync();
}
