using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Wpf.Helpers;

namespace PosApp.Wpf.Views;

public partial class LoginView : Window
{
    private LoginViewModel _vm = null!;

    public LoginView(IAuthService auth, MainWindow mainWindow)
    {
        InitializeComponent();
        _vm = new LoginViewModel(auth, this, mainWindow);
        DataContext = _vm;
        UsernameBox.TextChanged += (_, _) => _vm.Username = UsernameBox.Text;
    }

    private void PinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm.Pin = PinBox.Password;
            _vm.LoginCommand.Execute(null);
        }
    }

    private void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        _vm.Pin = PinBox.Password;
        _vm.LoginCommand.Execute(null);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}

public class LoginViewModel : ViewModelBase
{
    private readonly IAuthService _auth;
    private readonly LoginView _view;
    private readonly MainWindow _mainWindow;
    private string _username = "";
    private string _pin = "";
    private string? _error;

    public LoginViewModel(IAuthService auth, LoginView view, MainWindow mainWindow)
    {
        _auth = auth;
        _view = view;
        _mainWindow = mainWindow;
        LoginCommand = new RelayCommand(async () => await LoginAsync());
    }

    public string Username { get => _username; set => Set(ref _username, value); }
    public string Pin { get => _pin; set => Set(ref _pin, value); }
    public string? Error { get => _error; set { Set(ref _error, value); OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public ICommand LoginCommand { get; }

    private async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(Username))
        {
            Error = "Username is required";
            return;
        }
        Error = null;

        var user = await _auth.LoginAsync(Username, Pin);
        if (user == null)
        {
            Error = "Invalid username or PIN";
            return;
        }

        App.CurrentUser = user;
        _mainWindow.SetCurrentUser(user);
        _mainWindow.Show();
        _view.Close();
    }
}
