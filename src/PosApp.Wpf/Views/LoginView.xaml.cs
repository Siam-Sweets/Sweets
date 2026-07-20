using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Wpf.Helpers;

namespace PosApp.Wpf.Views;

public partial class LoginView : Window
{
    private LoginViewModel _vm = null!;
    private readonly IServiceProvider _services;

    public LoginView(IAuthService auth, ICloudAccountService accounts, ISettingsService settings,
        MainWindow mainWindow, IServiceProvider services)
    {
        InitializeComponent();
        ConstrainToWorkingArea();
        _vm = new LoginViewModel(auth, accounts, settings, this, mainWindow);
        _services = services;
        DataContext = _vm;
    }

    private void ConstrainToWorkingArea()
    {
        const double preferredWidth = 520;
        const double preferredHeight = 700;
        const double preferredMinWidth = 420;
        const double preferredMinHeight = 420;
        const double screenMargin = 16;

        var workArea = SystemParameters.WorkArea;
        var availableWidth = Math.Max(320, workArea.Width - screenMargin);
        var availableHeight = Math.Max(320, workArea.Height - screenMargin);

        MinWidth = Math.Min(preferredMinWidth, availableWidth);
        MinHeight = Math.Min(preferredMinHeight, availableHeight);
        MaxWidth = availableWidth;
        MaxHeight = availableHeight;
        Width = Math.Min(preferredWidth, availableWidth);
        Height = Math.Min(preferredHeight, availableHeight);
    }

    private async void PinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm.Pin = PinBox.Password;
            await _vm.LoginAsync();
            e.Handled = true;
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        _vm.Pin = PinBox.Password;
        await _vm.LoginAsync();
    }

    private async void OnlineAccount_Click(object sender, RoutedEventArgs e)
    {
        var cloudAccountWindow = _services.GetRequiredService<CloudAccountWindow>();
        cloudAccountWindow.Owner = this;
        if (cloudAccountWindow.ShowDialog() == true && cloudAccountWindow.AuthenticatedUser != null)
        {
            try
            {
                await _vm.CompleteLoginAsync(cloudAccountWindow.AuthenticatedUser);
            }
            catch (Exception exception)
            {
                App.LogError("Apply online account settings", exception);
                LocalizedMessageBox.Show(
                    Application.Current.TryFindResource("Cloud_UnexpectedError") as string
                    ?? "Login could not be completed. Local data remains safe.",
                    Application.Current.TryFindResource("Cloud_AccountTitle") as string ?? "Online account",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}

public class LoginViewModel : ViewModelBase
{
    private readonly IAuthService _auth;
    private readonly ICloudAccountService _accounts;
    private readonly ISettingsService _settings;
    private readonly LoginView _view;
    private readonly MainWindow _mainWindow;
    private string _username = "";
    private string _pin = "";
    private string? _error;
    private bool _isLoggingIn;

    public LoginViewModel(IAuthService auth, ICloudAccountService accounts, ISettingsService settings,
        LoginView view, MainWindow mainWindow)
    {
        _auth = auth;
        _accounts = accounts;
        _settings = settings;
        _view = view;
        _mainWindow = mainWindow;
    }

    public string Username { get => _username; set => Set(ref _username, value); }
    public string Pin { get => _pin; set => Set(ref _pin, value); }
    public string? Error { get => _error; set { Set(ref _error, value); OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public async Task LoginAsync()
    {
        if (_isLoggingIn) return;
        if (string.IsNullOrWhiteSpace(Username))
        {
            Error = "Username is required";
            return;
        }
        Error = null;
        _isLoggingIn = true;
        try
        {
            var user = await _auth.LoginAsync(Username.Trim(), Pin);
            if (user == null)
            {
                Error = "Invalid username or PIN";
                return;
            }

            if (!await _accounts.CanUseCachedSessionAsync(user.Id))
            {
                var state = await _accounts.GetAccountStateAsync();
                Error = state?.IsDeviceRevoked == true
                    ? Application.Current.TryFindResource("Cloud_ErrorDeviceRevoked") as string
                      ?? "This device has been revoked by an administrator."
                    : Application.Current.TryFindResource("Cloud_SessionUserMismatch") as string
                      ?? "This local user has not completed online sign-in on this device. Use Online account once to cache secure offline access.";
                return;
            }

            await CompleteLoginAsync(user);
        }
        catch
        {
            Error = Application.Current.TryFindResource("Cloud_UnexpectedError") as string
                    ?? "Login could not be completed. Local data remains safe.";
        }
        finally
        {
            _isLoggingIn = false;
        }
    }


    public async Task CompleteLoginAsync(User user)
    {
        // Cloud/PIN authentication establishes the selected store's query
        // scope. Reload its settings now so receipts, language, currency, theme,
        // printer, and register behavior never leak from another cached branch.
        var settings = await _settings.GetStoreSettingsAsync();
        App.PublishSettings(settings);
        App.ApplyTheme(settings.Theme);
        App.ApplyLanguage(settings.Language);
        App.CurrentUser = user;
        _mainWindow.SetCurrentUser(user);
        Application.Current.MainWindow = _mainWindow;
        _mainWindow.Show();
        _view.Close();
    }
}
