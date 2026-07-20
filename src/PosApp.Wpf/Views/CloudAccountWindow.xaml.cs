using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Services;
using PosApp.Wpf.Helpers;

namespace PosApp.Wpf.Views;

public partial class CloudAccountWindow : Window
{
    private static readonly Regex UsernamePattern = new(
        "^[a-z0-9][a-z0-9._-]{2,59}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex EmailPattern = new(
        "^[^\\s@]+@[^\\s@]+\\.[^\\s@]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex OfflinePinPattern = new(
        "^[0-9]{4,12}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ICloudAccountService _accounts;
    private bool _busy;

    public CloudAccountWindow(ICloudAccountService accounts)
    {
        InitializeComponent();
        ConstrainToWorkingArea();
        _accounts = accounts;
        Loaded += CloudAccountWindow_Loaded;
    }

    public User? AuthenticatedUser { get; private set; }
    public CloudAuthenticationResult? AuthenticationResult { get; private set; }
    public bool CreatedOrganization { get; private set; }

    private void ConstrainToWorkingArea()
    {
        const double preferredWidth = 720;
        const double preferredHeight = 700;
        const double preferredMinWidth = 560;
        const double preferredMinHeight = 500;
        const double screenMargin = 16;

        var workArea = SystemParameters.WorkArea;
        var availableWidth = Math.Max(360, workArea.Width - screenMargin);
        var availableHeight = Math.Max(360, workArea.Height - screenMargin);

        MinWidth = Math.Min(preferredMinWidth, availableWidth);
        MinHeight = Math.Min(preferredMinHeight, availableHeight);
        MaxWidth = availableWidth;
        MaxHeight = availableHeight;
        Width = Math.Min(preferredWidth, availableWidth);
        Height = Math.Min(preferredHeight, availableHeight);
    }

    private void CloudAccountWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoginDeviceName.Text = Environment.MachineName;
        CreateDeviceName.Text = Environment.MachineName;
        if (!CloudDeploymentSettings.IsConfigured)
        {
            AccountTabs.IsEnabled = false;
            SetStatus(Text("Cloud_DeploymentEndpointMissing",
                CloudDeploymentSettings.ConfigurationError ??
                "Online synchronization is not configured for this PosApp build."), StatusKind.Error);
        }
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !ValidateLoginForm()) return;

        await RunAsync(async () =>
        {
            var result = await _accounts.LoginAsync(new CloudLoginRequest
            {
                ApiBaseUrl = CloudDeploymentSettings.RequireApiBaseUrl(),
                UsernameOrEmail = LoginIdentifier.Text,
                Password = LoginPassword.Password,
                OfflinePin = LoginOfflinePin.Password,
                DeviceName = LoginDeviceName.Text
            });
            AuthenticationResult = result;
            CreatedOrganization = false;
            AuthenticatedUser = result.LocalUser;
        }, "Online account sign in");
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !ValidateCreateForm()) return;

        await RunAsync(async () =>
        {
            var result = await _accounts.CreateOrganizationAsync(new CloudOrganizationRequest
            {
                ApiBaseUrl = CloudDeploymentSettings.RequireApiBaseUrl(),
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
        }, "Create online organization");
    }

    private bool ValidateLoginForm()
    {
        if (string.IsNullOrWhiteSpace(LoginIdentifier.Text))
            return ValidationFailure(Text("Cloud_ValidationUsernameOrEmail",
                "Username or email is required."), LoginIdentifier);

        if (!IsValidPassword(LoginPassword.Password))
            return ValidationFailure(PasswordRequirementText(), LoginPassword);

        if (!OfflinePinPattern.IsMatch(LoginOfflinePin.Password))
            return ValidationFailure(Text("Cloud_ValidationOfflinePin",
                "The offline PIN must contain 4 to 12 digits."), LoginOfflinePin);

        return true;
    }

    private bool ValidateCreateForm()
    {
        if (string.IsNullOrWhiteSpace(CreateOrganizationName.Text) ||
            CreateOrganizationName.Text.Trim().Length > 160)
            return ValidationFailure(Text("Cloud_ValidationOrganizationName",
                "Organization name is required and must be 160 characters or fewer."),
                CreateOrganizationName);

        if (string.IsNullOrWhiteSpace(CreateStoreName.Text) || CreateStoreName.Text.Trim().Length > 160)
            return ValidationFailure(Text("Cloud_ValidationStoreName",
                "Store name is required and must be 160 characters or fewer."), CreateStoreName);

        if (string.IsNullOrWhiteSpace(CreateFullName.Text) || CreateFullName.Text.Trim().Length > 100)
            return ValidationFailure(Text("Cloud_ValidationFullName",
                "Full name is required and must be 100 characters or fewer."), CreateFullName);

        if (!UsernamePattern.IsMatch(CreateUsername.Text.Trim()))
            return ValidationFailure(Text("Cloud_ValidationUsername",
                "Username must be 3 to 60 letters, numbers, dots, underscores, or hyphens."),
                CreateUsername);

        if (!EmailPattern.IsMatch(CreateEmail.Text.Trim()) || CreateEmail.Text.Trim().Length > 255)
            return ValidationFailure(Text("Cloud_ValidationEmail",
                "Enter a valid email address."), CreateEmail);

        if (!IsValidPassword(CreatePassword.Password))
            return ValidationFailure(PasswordRequirementText(), CreatePassword);

        if (CreatePassword.Password != CreatePasswordConfirm.Password)
            return ValidationFailure(Text("Cloud_PasswordMismatch",
                "The passwords do not match."), CreatePasswordConfirm);

        if (!OfflinePinPattern.IsMatch(CreateOfflinePin.Password))
            return ValidationFailure(Text("Cloud_ValidationOfflinePin",
                "The offline PIN must contain 4 to 12 digits."), CreateOfflinePin);

        if (CreateDeviceName.Text.Trim().Length > 160)
            return ValidationFailure(Text("Cloud_ValidationDeviceName",
                "Device name must be 160 characters or fewer."), CreateDeviceName);

        return true;
    }

    private static bool IsValidPassword(string password)
        => password.Length is >= 10 and <= 128 &&
           password.Any(char.IsLetter) &&
           password.Any(char.IsDigit);

    private string PasswordRequirementText()
        => Text("Cloud_ValidationPasswordLength",
            "The online password must contain 10 to 128 characters and include at least one letter and one number.");

    private bool ValidationFailure(string message, Control target)
    {
        SetStatus(message, StatusKind.Error);
        LocalizedMessageBox.Show(this, message,
            Text("Cloud_ValidationTitle", "Check account details"),
            MessageBoxButton.OK, MessageBoxImage.Warning);
        target.Focus();
        return false;
    }

    private async Task RunAsync(Func<Task> operation, string logContext)
    {
        _busy = true;
        AccountTabs.IsEnabled = false;
        BusyBar.Visibility = Visibility.Visible;
        SetStatus(Text("Cloud_Connecting", "Connecting securely..."), StatusKind.Busy);

        // Let WPF paint the busy state before validation, DNS, TLS, or database
        // work begins. This prevents a network request from appearing as a dead click.
        await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);

        try
        {
            await operation();
            if (AuthenticatedUser == null)
                throw new InvalidOperationException("The online account did not provide a local user profile.");
            DialogResult = true;
        }
        catch (CloudApiException exception)
        {
            var message = string.IsNullOrWhiteSpace(exception.RequestId)
                ? FriendlyError(exception.Code, exception.Message)
                : $"{FriendlyError(exception.Code, exception.Message)} • {Text("Cloud_RequestId", "Request ID")}: {exception.RequestId}";
            ShowOperationFailure(message);
        }
        catch (ArgumentException exception)
        {
            ShowOperationFailure(RuntimeUiText.Translate(exception.Message), MessageBoxImage.Warning);
        }
        catch (InvalidOperationException exception)
        {
            ShowOperationFailure(RuntimeUiText.Translate(exception.Message), MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            App.LogError(logContext, exception);
            ShowOperationFailure(Text("Cloud_UnexpectedError",
                "The online account request could not be completed."));
        }
        finally
        {
            _busy = false;
            AccountTabs.IsEnabled = CloudDeploymentSettings.IsConfigured;
            BusyBar.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowOperationFailure(string message, MessageBoxImage icon = MessageBoxImage.Error)
    {
        SetStatus(message, StatusKind.Error);
        LocalizedMessageBox.Show(this, message,
            Text("Cloud_AccountTitle", "Online PosApp account"),
            MessageBoxButton.OK, icon);
    }

    private void SetStatus(string message, StatusKind kind)
    {
        StatusText.Text = message;
        var backgroundKey = kind switch
        {
            StatusKind.Error => "DangerSurfaceBrush",
            StatusKind.Busy => "InfoSurfaceBrush",
            _ => "CardBrush"
        };
        var foregroundKey = kind switch
        {
            StatusKind.Error => "DangerTextBrush",
            StatusKind.Busy => "InfoTextBrush",
            _ => "TextMutedBrush"
        };

        if (TryFindResource(backgroundKey) is Brush background)
            StatusBorder.Background = background;
        if (TryFindResource(foregroundKey) is Brush foreground)
            StatusText.Foreground = foreground;
    }

    private string FriendlyError(string code, string fallback)
    {
        // VALIDATION_ERROR is used by several API surfaces. Preserve the
        // endpoint's field-specific message here instead of replacing it with
        // the synchronization-oriented generic resource.
        if (string.Equals(code, "VALIDATION_ERROR", StringComparison.Ordinal))
            return fallback;

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

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) Close();
    }

    private enum StatusKind
    {
        Normal,
        Busy,
        Error
    }
}
