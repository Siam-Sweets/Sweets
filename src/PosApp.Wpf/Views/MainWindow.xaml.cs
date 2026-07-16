using System.Windows;
using System.Windows.Controls;
using PosApp.Core.Entities;
using PosApp.Localization;

namespace PosApp.Wpf.Views;

public partial class MainWindow : Window
{
    private readonly PosView _pos;
    private readonly ProductsView _products;
    private readonly InventoryView _inventory;
    private readonly CustomersView _customers;
    private readonly SalesView _sales;
    private readonly ReportsView _reports;
    private readonly UsersView _users;
    private readonly SettingsView _settings;
    private Button? _activeNav;

    public MainWindow(PosView pos, ProductsView products, InventoryView inventory,
        CustomersView customers, SalesView sales, ReportsView reports,
        UsersView users, SettingsView settings)
    {
        InitializeComponent();
        _pos = pos;
        _products = products;
        _inventory = inventory;
        _customers = customers;
        _sales = sales;
        _reports = reports;
        _users = users;
        _settings = settings;
    }

    public void SetCurrentUser(User user)
    {
        UserName.Text = user.FullName;
        UserRoleLabel.Text = user.Role.ToString();
        UserInitials.Text = string.IsNullOrEmpty(user.FullName) ? "?" : user.FullName[..1].ToUpper();

        // Role-based access: Cashier sees POS + Sales; Manager adds Inventory/Customers/Reports; Admin sees all.
        NavProducts.Visibility = user.Role >= UserRole.Manager ? Visibility.Visible : Visibility.Collapsed;
        NavInventory.Visibility = user.Role >= UserRole.Manager ? Visibility.Visible : Visibility.Collapsed;
        NavCustomers.Visibility = user.Role >= UserRole.Manager ? Visibility.Visible : Visibility.Collapsed;
        NavReports.Visibility = user.Role >= UserRole.Manager ? Visibility.Visible : Visibility.Collapsed;
        NavUsers.Visibility = user.Role >= UserRole.Admin ? Visibility.Visible : Visibility.Collapsed;
        NavSettings.Visibility = user.Role >= UserRole.Admin ? Visibility.Visible : Visibility.Collapsed;

        NavigateTo("pos");
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Re-apply language on every UI thread start.
        LocalizationManager.Instance.CultureChanged += (_, _) => { };
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    public void NavigateTo(string tag)
    {
        UserControl? view = tag switch
        {
            "pos" => _pos,
            "products" => _products,
            "inventory" => _inventory,
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

        // Update active button
        Button? newActive = tag switch
        {
            "pos" => NavPos,
            "products" => NavProducts,
            "inventory" => NavInventory,
            "customers" => NavCustomers,
            "sales" => NavSales,
            "reports" => NavReports,
            "users" => NavUsers,
            "settings" => NavSettings,
            _ => null
        };

        if (_activeNav != null) _activeNav.Style = (Style)FindResource("NavButton");
        if (newActive != null)
        {
            newActive.Style = (Style)FindResource("NavButtonActive");
            _activeNav = newActive;
        }

        // Refresh data on navigation
        if (contentChanged && view.IsEnabled && view is IRefreshable r) r.Refresh();
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        App.CurrentUser = null;
        var login = App.Services.GetService(typeof(LoginView)) as LoginView;
        if (login != null)
        {
            Application.Current.MainWindow = login;
            login.Show();
            Close();
        }
    }
}

/// <summary>Marker interface for views that can refresh their data when navigated to.</summary>
public interface IRefreshable
{
    void Refresh();
}
