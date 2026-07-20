using System.Windows;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Services;
using PosApp.Wpf.Helpers;

namespace PosApp.Wpf.Views;

public partial class CloudAccountWindow : Window
{
    private readonly ICloudAccountService _accounts;
    private bool _busy;

    public CloudAccountWindow(ICloudAccountService accounts)
    {
        InitializeComponent();
        _accounts = accounts;
        Loaded += CloudAccountWindow_Loaded;
    }

    public User? AuthenticatedUser { get; private set; }
    public CloudAuthenticationResult? AuthenticationResult { get; private set; }
    public bool CreatedOrganization { get; private set; }

    private async void CloudAccountWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoginDeviceName.Text = Environment.MachineName;
        CreateDeviceName.Text = Environment.MachineName;
        try
        {
            var state = await _accounts.GetAccountStateAsync();
            if (state != null)
            {
                LoginApiUrl.Text = state.ApiBaseUrl;
                CreateApiUrl.Text = state.ApiBaseUrl;
            }
        }
        catch
        {
            // The form remains usable; validation will run when submitted.
        }
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await RunAsync(async () =>
        {
            var result = await _accounts.LoginAsync(new CloudLoginRequest
            {
                ApiBaseUrl = LoginApiUrl.Text,
                UsernameOrEmail = LoginIdentifier.Text,
                Password = LoginPassword.Password,
                OfflinePin = LoginOfflinePin.Password,
                DeviceName = LoginDeviceName.Text
            });
            AuthenticationResult = result;
            CreatedOrganization = false;
            AuthenticatedUser = result.LocalUser;
        });
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (CreatePassword.Password != CreatePasswordConfirm.Password)
        {
            StatusText.Text = Text("Cloud_PasswordMismatch", "The passwords do not match.");
            return;
        }
        await RunAsync(async () =>
        {
            var result = await _accounts.CreateOrganizationAsync(new CloudOrganizationRequest
            {
                ApiBaseUrl = CreateApiUrl.Text,
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
        });
    }

    private async Task RunAsync(Func<Task> operation)
    {
        _busy = true;
        AccountTabs.IsEnabled = false;
        BusyBar.Visibility = Visibility.Visible;
        StatusText.Text = Text("Cloud_Connecting", "Connecting securely...");
        try
        {
            await operation();
            if (AuthenticatedUser == null)
                throw new InvalidOperationException("The online account did not provide a local user profile.");
            DialogResult = true;
        }
        catch (CloudApiException exception)
        {
            StatusText.Text = string.IsNullOrWhiteSpace(exception.RequestId)
                ? FriendlyError(exception.Code, exception.Message)
                : $"{FriendlyError(exception.Code, exception.Message)} • {Text("Cloud_RequestId", "Request ID")}: {exception.RequestId}";
        }
        catch (ArgumentException exception)
        {
            StatusText.Text = RuntimeUiText.Translate(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            StatusText.Text = RuntimeUiText.Translate(exception.Message);
        }
        catch
        {
            StatusText.Text = Text("Cloud_UnexpectedError", "The online account request could not be completed.");
        }
        finally
        {
            _busy = false;
            AccountTabs.IsEnabled = true;
            BusyBar.Visibility = Visibility.Collapsed;
        }
    }

    private string FriendlyError(string code, string fallback)
    {
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

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
