using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Core.Utilities;

namespace PosApp.Wpf.Views;

public partial class RegisterView : UserControl, IRefreshable
{
    private readonly IRegisterService _register;
    private readonly IHardwareService _hardware;
    private CashSession? _openSession;

    public RegisterView(IRegisterService register, IHardwareService hardware)
    {
        InitializeComponent();
        _register = register;
        _hardware = hardware;
    }

    public async Task RefreshAsync()
    {
        IsEnabled = false;
        try { await LoadAsync(); }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to load register", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsEnabled = true; }
    }

    private async Task LoadAsync()
    {
        _openSession = await _register.GetOpenSessionAsync();
        var recent = await _register.GetRecentSessionsAsync();
        SessionGrid.ItemsSource = recent.Select(session => new RegisterSessionRow(session)).ToList();

        var isOpen = _openSession != null;
        OpenButton.IsEnabled = !isOpen;
        CashInButton.IsEnabled = isOpen;
        CashOutButton.IsEnabled = isOpen;
        ReportButton.IsEnabled = isOpen;
        CloseButton.IsEnabled = isOpen;
        CloseButton.Visibility = App.CurrentUser is { Role: >= UserRole.Manager }
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!isOpen)
        {
            StatusText.Text = "No open session";
            StatusBorder.Background = (System.Windows.Media.Brush)FindResource("SurfaceMutedBrush");
            OpeningText.Text = CashSalesText.Text = MovementsText.Text = ExpectedText.Text = "—";
            MovementGrid.ItemsSource = null;
            return;
        }

        StatusText.Text = $"Open since {DateTimeUtilities.ToLocal(_openSession!.OpenedAt):dd MMM, HH:mm}";
        StatusBorder.Background = (System.Windows.Media.Brush)FindResource("SuccessSurfaceBrush");
        var summary = await _register.GetSummaryAsync(_openSession.Id);
        var movements = await _register.GetMovementsAsync(_openSession.Id);
        MovementGrid.ItemsSource = movements.Select(movement => new CashMovementRow(movement)).ToList();
        OpeningText.Text = Money(summary.OpeningFloat);
        CashSalesText.Text = Money(summary.CashSales);
        MovementsText.Text = $"+{Money(summary.CashIn)} / -{Money(summary.CashOut)}";
        ExpectedText.Text = Money(summary.ExpectedCash);
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (App.CurrentUser == null) return;
        var dialog = new CashEntryDialog("Open Register", "Opening cash", requireReason: false)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _register.OpenSessionAsync(dialog.Amount, App.CurrentUser.Id, dialog.Reason);
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to open register", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CashIn_Click(object sender, RoutedEventArgs e) => AddMovement(CashMovementType.CashIn);
    private void CashOut_Click(object sender, RoutedEventArgs e) => AddMovement(CashMovementType.CashOut);

    private async void AddMovement(CashMovementType type)
    {
        if (App.CurrentUser == null) return;
        var dialog = new CashEntryDialog(
            type == CashMovementType.CashIn ? "Cash In" : "Cash Out",
            "Amount",
            requireReason: true)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _register.AddMovementAsync(type, dialog.Amount, dialog.Reason, App.CurrentUser.Id);
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to record movement", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Report_Click(object sender, RoutedEventArgs e)
    {
        if (_openSession == null) return;
        await ShowReportAsync(_openSession, "X REPORT");
    }

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_openSession == null || App.CurrentUser == null) return;
        if (App.CurrentUser.Role < UserRole.Manager)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("A manager or administrator must close the register.",
                "Register", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var current = await _register.GetSummaryAsync(_openSession.Id);
        var dialog = new CashEntryDialog("Close Register", "Counted cash", requireReason: false)
        {
            Owner = Window.GetWindow(this),
            Amount = current.ExpectedCash
        };
        if (dialog.ShowDialog() != true) return;
        if (PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Close this register session and produce the final Z report?",
                "Close Register", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        try
        {
            var closedSummary = await _register.CloseSessionAsync(
                _openSession.Id, dialog.Amount, App.CurrentUser.Id, dialog.Reason);
            var report = BuildReport(closedSummary, "Z REPORT");
            new RegisterReportDialog(report, _hardware) { Owner = Window.GetWindow(this) }.ShowDialog();
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to close register", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SessionGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SessionGrid.SelectedItem is not RegisterSessionRow row) return;
        await ShowReportAsync(row.Session, row.Session.IsOpen ? "X REPORT" : "Z REPORT");
    }

    private async Task ShowReportAsync(CashSession session, string title)
    {
        try
        {
            var summary = await _register.GetSummaryAsync(session.Id);
            var report = BuildReport(summary, title);
            new RegisterReportDialog(report, _hardware) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to build register report", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string BuildReport(RegisterSummary summary, string title)
    {
        var builder = new StringBuilder();
        builder.AppendLine(App.StoreSettings.StoreName);
        builder.AppendLine(title);
        builder.AppendLine(new string('=', 38));
        builder.AppendLine($"Session:       {summary.SessionId}");
        builder.AppendLine($"Opened:        {DateTimeUtilities.ToLocal(summary.OpenedAt):dd MMM yyyy HH:mm}");
        builder.AppendLine($"Through:       {DateTimeUtilities.ToLocal(summary.ClosedAt ?? DateTime.UtcNow):dd MMM yyyy HH:mm}");
        builder.AppendLine(new string('-', 38));
        builder.AppendLine($"Transactions:  {summary.TransactionCount}");
        builder.AppendLine($"Gross sales:   {Money(summary.GrossSales)}");
        builder.AppendLine();
        builder.AppendLine("PAYMENTS");
        foreach (var payment in summary.ByPaymentMethod.OrderBy(item => item.Key))
            builder.AppendLine($"{payment.Key,-18} {Money(payment.Value)}");
        builder.AppendLine(new string('-', 38));
        builder.AppendLine($"Opening cash:  {Money(summary.OpeningFloat)}");
        builder.AppendLine($"Cash sales:    {Money(summary.CashSales)}");
        builder.AppendLine($"Cash in:       {Money(summary.CashIn)}");
        builder.AppendLine($"Cash out:      {Money(summary.CashOut)}");
        builder.AppendLine($"Expected cash: {Money(summary.ExpectedCash)}");
        if (summary.CountedCash.HasValue)
        {
            builder.AppendLine($"Counted cash:  {Money(summary.CountedCash.Value)}");
            builder.AppendLine($"Variance:      {Money(summary.Variance.GetValueOrDefault())}");
        }
        builder.AppendLine(new string('=', 38));
        builder.AppendLine($"Printed {DateTime.Now:dd MMM yyyy HH:mm}");
        return builder.ToString();
    }

    private static string Money(decimal value) => FormattingUtilities.Money(value, App.StoreSettings);
}

public sealed class RegisterSessionRow
{
    public RegisterSessionRow(CashSession session)
    {
        Session = session;
        OpenedLocal = DateTimeUtilities.ToLocal(session.OpenedAt);
        ClosedLocal = session.ClosedAt.HasValue ? DateTimeUtilities.ToLocal(session.ClosedAt.Value) : null;
        Status = session.IsOpen ? "Open" : "Closed";
        Expected = session.ExpectedCash;
        Variance = session.Variance;
    }

    public CashSession Session { get; }
    public DateTime OpenedLocal { get; }
    public DateTime? ClosedLocal { get; }
    public string Status { get; }
    public decimal? Expected { get; }
    public decimal? Variance { get; }
}

public sealed class CashMovementRow
{
    public CashMovementRow(CashMovement movement)
    {
        LocalTime = DateTimeUtilities.ToLocal(movement.CreatedAt);
        Type = movement.Type == CashMovementType.CashIn ? "Cash In" : "Cash Out";
        Description = movement.Description;
        SignedAmount = movement.Type == CashMovementType.CashIn ? movement.Amount : -movement.Amount;
    }

    public DateTime LocalTime { get; }
    public string Type { get; }
    public string Description { get; }
    public decimal SignedAmount { get; }
}

public sealed class CashEntryDialog : Window
{
    private readonly TextBox _amountBox = new();
    private readonly TextBox _reasonBox = new();
    private readonly bool _requireReason;

    public CashEntryDialog(string title, string amountLabel, bool requireReason)
    {
        Title = title;
        Width = 440;
        Height = 310;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _requireReason = requireReason;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Label(amountLabel));
        panel.Children.Add(_amountBox);
        panel.Children.Add(Label(requireReason ? "Reason (required)" : "Note (optional)", new Thickness(0, 14, 0, 4)));
        panel.Children.Add(_reasonBox);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("OutlineButton"), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = new Button { Content = "Confirm", Style = (Style)FindResource("PrimaryButton") };
        save.Click += Confirm_Click;
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        panel.Children.Add(actions);
        Content = panel;
        Loaded += (_, _) => { _amountBox.Focus(); _amountBox.SelectAll(); };
    }

    public decimal Amount
    {
        get => FormattingUtilities.TryParseDecimal(_amountBox.Text, out var value) ? value : 0m;
        set => _amountBox.Text = value.ToString("0.00");
    }

    public string Reason => _reasonBox.Text.Trim();

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!FormattingUtilities.TryParseDecimal(_amountBox.Text, out var amount) || amount < 0m ||
            (_requireReason && amount == 0m))
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(_requireReason
                    ? "Enter an amount greater than zero."
                    : "Enter a valid non-negative amount.",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_requireReason && string.IsNullOrWhiteSpace(_reasonBox.Text))
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Enter a reason.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
        Close();
    }

    private static TextBlock Label(string text, Thickness? margin = null) => new()
    {
        Text = text,
        FontSize = 12,
        Margin = margin ?? new Thickness(0, 0, 0, 4)
    };
}

public sealed class RegisterReportDialog : Window
{
    public RegisterReportDialog(string report, IHardwareService hardware)
    {
        Title = report.Contains("Z REPORT", StringComparison.Ordinal) ? "Z Report" : "X Report";
        Width = 600;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new TextBox
        {
            Text = report,
            IsReadOnly = true,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 14,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        root.Children.Add(text);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var print = new Button { Content = "Print", Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        print.Click += async (_, _) =>
        {
            var ok = await hardware.PrintTextAsync(report);
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ok ? "Report sent to the printer." : "Printing failed. Check printer settings.",
                Title, MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        };
        var close = new Button { Content = "Close", Style = (Style)FindResource("OutlineButton") };
        close.Click += (_, _) => Close();
        actions.Children.Add(print);
        actions.Children.Add(close);
        Grid.SetRow(actions, 1);
        root.Children.Add(actions);
        Content = root;
    }
}
