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

    public async Task RefreshAsync()
    {
        IsEnabled = false;
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to load users", MessageBoxButton.OK, MessageBoxImage.Error);
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
        if (dlg.ShowDialog() == true) _ = RefreshAsync();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is User u)
        {
            var dlg = new UserEditDialog(_db, u) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true) _ = RefreshAsync();
        }
    }


    private async void Active_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not User user)
            return;

        e.Handled = true;
        var previousState = user.IsActive;
        var requestedState = !previousState;
        checkBox.IsChecked = requestedState;

        if (!requestedState && user.Id == App.CurrentUser?.Id)
        {
            user.IsActive = previousState;
            checkBox.IsChecked = previousState;
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("You cannot deactivate the account that is currently signed in.",
                "Unable to update user", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        checkBox.IsEnabled = false;
        try
        {
            if (!requestedState && user.Role == UserRole.Admin)
            {
                var activeAdminCount = await _db.Users.CountAsync(x => x.Role == UserRole.Admin && x.IsActive);
                if (activeAdminCount <= 1)
                {
                    user.IsActive = previousState;
                    checkBox.IsChecked = previousState;
                    PosApp.Wpf.Helpers.LocalizedMessageBox.Show("At least one active administrator account is required.",
                        "Unable to update user", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            var updatedAt = DateTime.UtcNow;
            var trackedUser = _db.Users.Local.FirstOrDefault(x => x.Id == user.Id);

            if (trackedUser != null)
            {
                trackedUser.IsActive = requestedState;
                trackedUser.UpdatedAt = updatedAt;
                await _db.SaveChangesAsync();
            }
            else
            {
                var affected = await _db.Users
                    .Where(x => x.Id == user.Id)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.IsActive, requestedState)
                        .SetProperty(x => x.UpdatedAt, updatedAt));

                if (affected != 1)
                    throw new InvalidOperationException("The selected user could not be found.");
            }

            user.IsActive = requestedState;
        }
        catch (Exception ex)
        {
            user.IsActive = previousState;
            checkBox.IsChecked = previousState;
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to update user", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            checkBox.IsEnabled = true;
        }
    }

    private async void ResetPin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is User u)
        {
            var dlg = new ResetPinDialog() { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.NewPin))
            {
                try
                {
                    await _auth.ChangePasswordAsync(u.Id, dlg.NewPin);
                    PosApp.Wpf.Helpers.LocalizedMessageBox.Show("PIN reset successfully.", "Reset", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.GetBaseException().Message, "Unable to reset PIN", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: User user }) return;

        if (user.Id == App.CurrentUser?.Id)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Cannot delete your own account.", "Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            if (user.Role == UserRole.Admin && user.IsActive)
            {
                var adminCount = await _db.Users.CountAsync(x => x.Role == UserRole.Admin && x.IsActive);
                if (adminCount <= 1)
                {
                    PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Cannot delete the last admin account.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            var confirm = PosApp.Wpf.Helpers.LocalizedMessageBox.Show($"Delete user '{user.Username}'?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            var hasHistory = await _db.Sales.AnyAsync(sale => sale.UserId == user.Id) ||
                             await _db.PurchaseDocuments.AnyAsync(purchase => purchase.UserId == user.Id) ||
                             await _db.CashSessions.AnyAsync(session =>
                                 session.OpenedByUserId == user.Id || session.ClosedByUserId == user.Id) ||
                             await _db.CashMovements.AnyAsync(movement => movement.UserId == user.Id) ||
                             await _db.StockTransactions.AnyAsync(transaction => transaction.UserId == user.Id);
            var tracked = await _db.Users.FindAsync(user.Id)
                ?? throw new InvalidOperationException("User not found.");
            if (hasHistory)
            {
                // Keep historical receipts valid while removing login access.
                tracked.IsActive = false;
                tracked.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                _db.Users.Remove(tracked);
            }

            await _db.SaveChangesAsync();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.GetBaseException().Message,
                "Unable to delete user", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

public class UserEditDialog : Window
{
    private readonly Data.AppDbContext _db;
    private readonly int _userId;
    private readonly UserRole _originalRole;
    private readonly bool _originalActive;
    private readonly bool _isNew;
    private readonly ComboBox _roleCombo = new();
    private readonly CheckBox _activeCheckbox = new() { Content = "Active" };
    private readonly TextBox _usernameBox = new() { MaxLength = 60 };
    private readonly TextBox _fullnameBox = new() { MaxLength = 100 };
    private readonly PasswordBox _pinBox = new() { MaxLength = 12 };

    public UserEditDialog(Data.AppDbContext db, User? existing)
    {
        _db = db;
        _isNew = existing == null;
        _userId = existing?.Id ?? 0;
        _originalRole = existing?.Role ?? UserRole.Cashier;
        _originalActive = existing?.IsActive ?? true;

        Title = _isNew ? "Add User" : "Edit User";
        Width = 460;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BackgroundBrush");

        _usernameBox.Text = existing?.Username ?? string.Empty;
        _fullnameBox.Text = existing?.FullName ?? string.Empty;
        _activeCheckbox.IsChecked = existing?.IsActive ?? true;
        _roleCombo.Items.Add(new ComboBoxItem { Content = "Cashier", Tag = UserRole.Cashier });
        _roleCombo.Items.Add(new ComboBoxItem { Content = "Manager", Tag = UserRole.Manager });
        _roleCombo.Items.Add(new ComboBoxItem { Content = "Admin", Tag = UserRole.Admin });
        _roleCombo.SelectedIndex = Math.Max(0, (int)_originalRole);

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(MakeRow("Username", _usernameBox));
        panel.Children.Add(MakeRow("Full Name", _fullnameBox));
        panel.Children.Add(MakeRow("Role", _roleCombo));
        if (_isNew) panel.Children.Add(MakeRow("Initial PIN (4-12 digits)", _pinBox));
        _activeCheckbox.Margin = new Thickness(0, 0, 0, 16);
        panel.Children.Add(_activeCheckbox);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button
        {
            Content = "Cancel",
            Style = (Style)Application.Current.FindResource("OutlineButton"),
            Padding = new Thickness(20, 10, 20, 10),
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = new Button
        {
            Content = "Save",
            Style = (Style)Application.Current.FindResource("PrimaryButton"),
            Padding = new Thickness(20, 10, 20, 10)
        };
        save.Click += Save_Click;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);
        Content = panel;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var username = _usernameBox.Text.Trim();
        var fullName = _fullnameBox.Text.Trim();
        if (username.Length == 0 || fullName.Length == 0)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Username and full name are required.", "Invalid user", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (username.Any(char.IsWhiteSpace))
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Username cannot contain spaces.", "Invalid user", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (username.Length > 60 || fullName.Length > 100)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(
                "Username cannot exceed 60 characters and full name cannot exceed 100 characters.",
                "Invalid user", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var role = (UserRole)((ComboBoxItem)_roleCombo.SelectedItem).Tag!;
        var isActive = _activeCheckbox.IsChecked == true;
        if (!_isNew && _userId == App.CurrentUser?.Id && (!isActive || role != _originalRole))
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("You cannot deactivate or change the role of the account currently signed in.",
                "Unable to update user", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        IsEnabled = false;
        try
        {
            if (await _db.Users.AsNoTracking().AnyAsync(u =>
                    u.Id != _userId && u.Username.ToLower() == username.ToLower()))
            {
                PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Another user already has this username.",
                    "Invalid user", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_isNew && _originalRole == UserRole.Admin && _originalActive &&
                (!isActive || role != UserRole.Admin))
            {
                var otherActiveAdmins = await _db.Users.AsNoTracking().CountAsync(u =>
                    u.Id != _userId && u.Role == UserRole.Admin && u.IsActive);
                if (otherActiveAdmins == 0)
                {
                    PosApp.Wpf.Helpers.LocalizedMessageBox.Show("At least one active administrator account is required.",
                        "Unable to update user", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            if (_isNew)
            {
                Data.DbSeeder.ValidatePin(_pinBox.Password);
                var (hash, salt) = Data.DbSeeder.HashPin(_pinBox.Password);
                _db.Users.Add(new User
                {
                    Username = username,
                    FullName = fullName,
                    Role = role,
                    IsActive = isActive,
                    PasswordHash = hash,
                    PasswordSalt = salt,
                    CreatedAt = DateTime.UtcNow
                });
            }
            else
            {
                var tracked = await _db.Users.FindAsync(_userId)
                    ?? throw new InvalidOperationException("User not found.");
                tracked.Username = username;
                tracked.FullName = fullName;
                tracked.Role = role;
                tracked.IsActive = isActive;
                tracked.UpdatedAt = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync();
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.GetBaseException().Message, "Unable to save user", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private static Border MakeRow(string label, FrameworkElement control)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextMutedBrush"),
            Margin = new Thickness(0, 0, 0, 4)
        });
        control.Margin = new Thickness(0, 0, 0, 12);
        stack.Children.Add(control);
        return new Border { Child = stack };
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
            try
            {
                Data.DbSeeder.ValidatePin(pin.Password);
            }
            catch (Exception ex)
            {
                PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Invalid PIN", MessageBoxButton.OK, MessageBoxImage.Warning);
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
