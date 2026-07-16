using System.Windows;
using System.Windows.Controls;
using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;

namespace PosApp.Wpf.Views;

public partial class UsersView : UserControl, IRefreshable
{
    private readonly Data.AppDbContext _db;
    private readonly IAuthService _auth;

    public UsersView(Data.AppDbContext db, IAuthService auth)
    {
        InitializeComponent();
        _db = db;
        _auth = auth;
    }

    public async void Refresh()
    {
        IsEnabled = false;
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to load users", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async Task LoadAsync()
    {
        var users = await _db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync();
        UsersGrid.ItemsSource = users;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new UserEditDialog(_db, null) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) Refresh();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is User u)
        {
            var dlg = new UserEditDialog(_db, u) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true) Refresh();
        }
    }

    private async void ResetPin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is User u)
        {
            var dlg = new ResetPinDialog() { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.NewPin))
            {
                await _auth.ChangePasswordAsync(u.Id, dlg.NewPin);
                MessageBox.Show("PIN reset successfully.", "Reset", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is User u)
        {
            if (u.Id == App.CurrentUser?.Id)
            {
                MessageBox.Show("Cannot delete your own account.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (u.Role == UserRole.Admin)
            {
                var adminCount = await _db.Users.CountAsync(x => x.Role == UserRole.Admin && x.IsActive);
                if (adminCount <= 1)
                {
                    MessageBox.Show("Cannot delete the last admin account.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            var confirm = MessageBox.Show($"Delete user '{u.Username}'?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            try
            {
                var hasHistory = await _db.Sales.AnyAsync(sale => sale.UserId == u.Id) ||
                                 await _db.PurchaseDocuments.AnyAsync(purchase => purchase.UserId == u.Id) ||
                                 await _db.CashSessions.AnyAsync(session =>
                                     session.OpenedByUserId == u.Id || session.ClosedByUserId == u.Id) ||
                                 await _db.CashMovements.AnyAsync(movement => movement.UserId == u.Id);
                if (hasHistory)
                {
                    // Keep historical receipts valid while removing login access.
                    u.IsActive = false;
                    u.UpdatedAt = DateTime.UtcNow;
                    _db.Users.Update(u);
                }
                else
                {
                    _db.Users.Remove(u);
                }
                await _db.SaveChangesAsync();
                Refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Unable to delete user", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}

public class UserEditDialog : Window
{
    private readonly Data.AppDbContext _db;
    private readonly User _user;
    private readonly bool _isNew;
    private readonly ComboBox _roleCombo;
    private readonly CheckBox _activeCheckbox;
    private readonly TextBox _usernameBox;
    private readonly TextBox _fullnameBox;
    private readonly PasswordBox _pinBox;

    public UserEditDialog(Data.AppDbContext db, User? existing)
    {
        _db = db;
        _isNew = existing == null;
        _user = existing ?? new User { IsActive = true, Role = UserRole.Cashier };

        Title = _isNew ? "Add User" : "Edit User";
        Width = 460; Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BackgroundBrush");

        var panel = new StackPanel { Margin = new Thickness(24) };

        _usernameBox = new TextBox { Text = _user.Username, Margin = new Thickness(0, 0, 0, 12) };
        _fullnameBox = new TextBox { Text = _user.FullName, Margin = new Thickness(0, 0, 0, 12) };
        _roleCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 12) };
        _roleCombo.Items.Add(new ComboBoxItem { Content = "Cashier", Tag = UserRole.Cashier });
        _roleCombo.Items.Add(new ComboBoxItem { Content = "Manager", Tag = UserRole.Manager });
        _roleCombo.Items.Add(new ComboBoxItem { Content = "Admin", Tag = UserRole.Admin });
        for (int i = 0; i < _roleCombo.Items.Count; i++)
        {
            if (_roleCombo.Items[i] is ComboBoxItem ci && (UserRole)ci.Tag! == _user.Role) { _roleCombo.SelectedIndex = i; break; }
        }
        if (_roleCombo.SelectedIndex < 0) _roleCombo.SelectedIndex = 0;

        _pinBox = new PasswordBox { Margin = new Thickness(0, 0, 0, 12) };
        _activeCheckbox = new CheckBox { Content = "Active", IsChecked = _user.IsActive, Margin = new Thickness(0, 0, 0, 16) };

        panel.Children.Add(MakeRow("Username", _usernameBox));
        panel.Children.Add(MakeRow("Full Name", _fullnameBox));
        panel.Children.Add(MakeRow("Role", _roleCombo));
        if (_isNew) panel.Children.Add(MakeRow("Initial PIN", _pinBox));
        panel.Children.Add(_activeCheckbox);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelBtn = new Button
        {
            Content = "Cancel",
            Style = (Style)System.Windows.Application.Current.FindResource("OutlineButton"),
            Padding = new Thickness(20, 10, 20, 10),
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };
        btnRow.Children.Add(cancelBtn);

        var saveBtn = new Button
        {
            Content = "Save",
            Style = (Style)System.Windows.Application.Current.FindResource("PrimaryButton"),
            Padding = new Thickness(20, 10, 20, 10)
        };
        saveBtn.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_usernameBox.Text) || string.IsNullOrWhiteSpace(_fullnameBox.Text))
            {
                MessageBox.Show("Username and full name are required", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _user.Username = _usernameBox.Text.Trim();
            _user.FullName = _fullnameBox.Text.Trim();
            _user.Role = (UserRole)((ComboBoxItem)_roleCombo.SelectedItem).Tag!;
            _user.IsActive = _activeCheckbox.IsChecked ?? true;
            if (_isNew)
            {
                if (string.IsNullOrEmpty(_pinBox.Password))
                {
                    MessageBox.Show("Initial PIN is required", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var (hash, salt) = Data.DbSeeder.HashPin(_pinBox.Password);
                _user.PasswordHash = hash;
                _user.PasswordSalt = salt;
                _db.Users.Add(_user);
            }
            else
            {
                _user.UpdatedAt = DateTime.UtcNow;
                _db.Users.Update(_user);
            }
            try
            {
                await _db.SaveChangesAsync();
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Unable to save user", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        btnRow.Children.Add(saveBtn);
        panel.Children.Add(btnRow);

        Content = panel;
    }

    private static System.Windows.Controls.Border MakeRow(string label, FrameworkElement ctrl)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextMutedBrush"), Margin = new Thickness(0, 0, 0, 4) });
        stack.Children.Add(ctrl);
        return new System.Windows.Controls.Border { Child = stack };
    }
}

public class ResetPinDialog : Window
{
    public string NewPin { get; private set; } = "";
    public ResetPinDialog()
    {
        Title = "Reset PIN";
        Width = 360; Height = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BackgroundBrush");

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Enter new PIN", Margin = new Thickness(0, 0, 0, 8) });
        var pin = new PasswordBox { Margin = new Thickness(0, 0, 0, 16) };
        panel.Children.Add(pin);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelBtn = new Button
        {
            Content = "Cancel",
            Style = (Style)System.Windows.Application.Current.FindResource("OutlineButton"),
            Padding = new Thickness(20, 10, 20, 10),
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };
        btnRow.Children.Add(cancelBtn);

        var okBtn = new Button
        {
            Content = "Reset",
            Style = (Style)System.Windows.Application.Current.FindResource("PrimaryButton"),
            Padding = new Thickness(20, 10, 20, 10)
        };
        okBtn.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(pin.Password) || pin.Password.Length < 4)
            {
                MessageBox.Show("PIN must be at least 4 characters", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            NewPin = pin.Password;
            DialogResult = true;
            Close();
        };
        btnRow.Children.Add(okBtn);
        panel.Children.Add(btnRow);

        Content = panel;
    }
}
