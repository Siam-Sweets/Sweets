using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PosApp.Core.Entities;
using PosApp.Core.Models;

namespace PosApp.Wpf.Views;

public partial class PaymentDialog : Window
{
    private readonly ObservableCollection<PaymentEntryView> _entries = new();
    private SaleDraft? _draft;
    private bool _updatingAmount;

    public PaymentMethod SelectedMethod { get; private set; } = PaymentMethod.Cash;
    public IReadOnlyList<SalePayment> Payments => _entries
        .Select(entry => new SalePayment
        {
            Method = entry.Method,
            Amount = entry.AppliedAmount,
            Reference = entry.Method == PaymentMethod.Card ? "card" : null
        })
        .ToList();
    public decimal TenderedAmount => _entries.Sum(entry => entry.TenderedAmount);

    private decimal AppliedTotal => _entries.Sum(entry => entry.AppliedAmount);
    private decimal Remaining => Math.Max(0m, (_draft?.Total ?? 0m) - AppliedTotal);
    private string CurrencySymbol => App.StoreSettings.CurrencySymbol;

    public PaymentDialog()
    {
        InitializeComponent();
        PaymentsList.ItemsSource = _entries;
    }

    public void Configure(SaleDraft draft)
    {
        _draft = draft;
        _entries.Clear();
        SelectedMethod = PaymentMethod.Cash;
        SetAmountText("");
        AmountDueText.Text = Money(draft.Total);
        ConfigureQuickCash(draft.Total);
        UpdateMethodButtons();
        UpdateSummary();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => FocusAmount(selectAll: true);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter) return;

        if (Remaining <= 0m)
        {
            Finish();
        }
        else if (TryAddCurrentPayment(showError: true) && Remaining <= 0m)
        {
            Finish();
        }
        else
        {
            // A partial amount automatically starts a split payment. The next
            // amount can be typed immediately, matching Aronium's workflow.
            FocusAmount(selectAll: true);
        }
        e.Handled = true;
    }

    private void Method_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            !int.TryParse(button.Tag?.ToString(), out var methodValue)) return;

        SelectedMethod = (PaymentMethod)methodValue;
        UpdateMethodButtons();

        if (SelectedMethod != PaymentMethod.Cash && Remaining > 0m)
            SetAmountText(Remaining.ToString("0.00", CultureInfo.CurrentCulture));

        UpdateSummary();
        FocusAmount(selectAll: true);
    }

    private void UpdateMethodButtons()
    {
        void SetButton(Button button, PaymentMethod method)
        {
            button.Style = (Style)FindResource(
                SelectedMethod == method ? "PrimaryButton" : "OutlineButton");
        }

        SetButton(MethodCash, PaymentMethod.Cash);
        SetButton(MethodCard, PaymentMethod.Card);
        SetButton(MethodMobile, PaymentMethod.MobileWallet);
        SetButton(MethodBank, PaymentMethod.BankTransfer);
    }

    private void TenderedBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updatingAmount) UpdateSummary();
    }

    private void TenderedBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var proposed = TenderedBox.Text
            .Remove(TenderedBox.SelectionStart, TenderedBox.SelectionLength)
            .Insert(TenderedBox.SelectionStart, e.Text);
        e.Handled = !IsPotentialAmount(proposed);
    }

    private static bool IsPotentialAmount(string text)
    {
        if (string.IsNullOrEmpty(text) || text is "." or ",") return true;
        return TryParseAmount(text, out _);
    }

    private static bool TryParseAmount(string? text, out decimal amount)
    {
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out amount) ||
               decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }

    private void Pad_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag) return;

        if (tag == "back")
        {
            if (TenderedBox.SelectionLength > 0)
            {
                var start = TenderedBox.SelectionStart;
                SetAmountText(TenderedBox.Text.Remove(start, TenderedBox.SelectionLength));
                TenderedBox.CaretIndex = start;
            }
            else if (TenderedBox.Text.Length > 0)
            {
                SetAmountText(TenderedBox.Text[..^1]);
                TenderedBox.CaretIndex = TenderedBox.Text.Length;
            }
        }
        else
        {
            var proposed = TenderedBox.Text
                .Remove(TenderedBox.SelectionStart, TenderedBox.SelectionLength)
                .Insert(TenderedBox.SelectionStart, tag);
            if (IsPotentialAmount(proposed))
                SetAmountText(proposed);
        }

        UpdateSummary();
        FocusAmount(selectAll: false);
    }

    private void QuickCash_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not decimal amount) return;
        SelectedMethod = PaymentMethod.Cash;
        UpdateMethodButtons();
        SetAmountText(amount.ToString("0.00", CultureInfo.CurrentCulture));
        UpdateSummary();
        FocusAmount(selectAll: true);
    }

    private void ConfigureQuickCash(decimal total)
    {
        QuickExact.Tag = total;
        var candidates = new SortedSet<decimal>();
        foreach (var step in new[] { 50m, 100m, 500m, 1000m })
        {
            var rounded = Math.Ceiling(total / step) * step;
            if (rounded <= total) rounded += step;
            candidates.Add(rounded);
        }
        for (var amount = total + 50m; candidates.Count < 3; amount += 50m)
            candidates.Add(amount);

        var quick = candidates.Take(3).ToArray();
        ConfigureQuickButton(QuickOne, quick[0]);
        ConfigureQuickButton(QuickTwo, quick[1]);
        ConfigureQuickButton(QuickThree, quick[2]);
    }

    private static void ConfigureQuickButton(Button button, decimal amount)
    {
        button.Tag = amount;
        button.Content = amount.ToString("0.##", CultureInfo.CurrentCulture);
    }

    private void AddPayment_Click(object sender, RoutedEventArgs e)
    {
        if (TryAddCurrentPayment(showError: true))
            FocusAmount(selectAll: true);
    }

    private bool TryAddCurrentPayment(bool showError)
    {
        var remaining = Remaining;
        if (remaining <= 0m) return true;

        if (!TryParseAmount(TenderedBox.Text, out var entered) || entered <= 0m)
        {
            if (showError) ShowWarning("Pay_EnterAmount", "Enter the amount received.");
            return false;
        }

        if (SelectedMethod != PaymentMethod.Cash && entered > remaining)
        {
            if (showError) ShowWarning("Pay_ChangeCashOnly", "Only cash payments can be greater than the remaining balance.");
            return false;
        }

        var applied = Math.Min(entered, remaining);
        var tendered = SelectedMethod == PaymentMethod.Cash ? entered : applied;
        _entries.Add(new PaymentEntryView(SelectedMethod, applied, tendered, CurrencySymbol));
        SetAmountText("");
        UpdateSummary();
        return true;
    }

    private void RemovePayment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PaymentEntryView entry })
        {
            _entries.Remove(entry);
            UpdateSummary();
            FocusAmount(selectAll: true);
        }
    }

    private void Complete_Click(object sender, RoutedEventArgs e)
    {
        if (Remaining > 0m && !TryAddCurrentPayment(showError: true)) return;
        if (Remaining > 0m)
        {
            ShowWarning("Pay_RemainingWarning", "The sale still has an unpaid balance.");
            return;
        }
        Finish();
    }

    private void Finish()
    {
        if (_draft == null || Remaining > 0m || _entries.Count == 0) return;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void UpdateSummary()
    {
        var remaining = Remaining;
        RemainingText.Text = Money(remaining);
        var previewReceived = TenderedAmount;
        if (TryParseAmount(TenderedBox.Text, out var currentReceived) && currentReceived > 0m)
        {
            previewReceived += SelectedMethod == PaymentMethod.Cash
                ? currentReceived
                : Math.Min(currentReceived, remaining);
        }
        ReceivedText.Text = Money(previewReceived);

        var previewChange = _entries.Sum(entry => entry.TenderedAmount - entry.AppliedAmount);
        if (SelectedMethod == PaymentMethod.Cash &&
            TryParseAmount(TenderedBox.Text, out var currentAmount))
        {
            previewChange += Math.Max(0m, currentAmount - remaining);
        }
        ChangeText.Text = Money(previewChange);
        CompleteButton.IsEnabled = remaining <= 0m ||
                                   (TryParseAmount(TenderedBox.Text, out var entered) && entered > 0m);
    }

    private void SetAmountText(string value)
    {
        _updatingAmount = true;
        TenderedBox.Text = value;
        TenderedBox.CaretIndex = value.Length;
        _updatingAmount = false;
    }

    private void FocusAmount(bool selectAll)
    {
        TenderedBox.Focus();
        if (selectAll) TenderedBox.SelectAll();
        else TenderedBox.CaretIndex = TenderedBox.Text.Length;
    }

    private void ShowWarning(string key, string fallback)
    {
        var message = Application.Current.TryFindResource(key) as string ?? fallback;
        MessageBox.Show(this, message,
            Application.Current.TryFindResource("Pay_Title") as string ?? "Payment",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private string Money(decimal amount) => $"{CurrencySymbol} {amount:0.00}";
}

public sealed class PaymentEntryView
{
    public PaymentEntryView(PaymentMethod method, decimal appliedAmount,
        decimal tenderedAmount, string currencySymbol)
    {
        Method = method;
        AppliedAmount = appliedAmount;
        TenderedAmount = tenderedAmount;
        MethodName = Application.Current.TryFindResource(method switch
        {
            PaymentMethod.Cash => "Pay_Cash",
            PaymentMethod.Card => "Pay_Card",
            PaymentMethod.MobileWallet => "Pay_Mobile",
            PaymentMethod.BankTransfer => "Pay_Bank",
            _ => "Pay_Title"
        }) as string ?? method.ToString();
        Detail = tenderedAmount > appliedAmount
            ? $"{currencySymbol} {appliedAmount:0.00} - {Application.Current.TryFindResource("Pay_Received") as string ?? "Received"} {currencySymbol} {tenderedAmount:0.00}"
            : $"{currencySymbol} {appliedAmount:0.00}";
    }

    public PaymentMethod Method { get; }
    public decimal AppliedAmount { get; }
    public decimal TenderedAmount { get; }
    public string MethodName { get; }
    public string Detail { get; }
}
