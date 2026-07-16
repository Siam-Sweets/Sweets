using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PosApp.Core.Entities;
using PosApp.Core.Models;

namespace PosApp.Wpf.Views;

public partial class PaymentDialog : Window
{
    private SaleDraft? _draft;
    public PaymentMethod SelectedMethod { get; private set; } = PaymentMethod.Cash;
    public List<SalePayment> Payments { get; } = new();
    public decimal TenderedAmount => _tendered;
    private decimal _tendered = 0m;

    public PaymentDialog()
    {
        InitializeComponent();
    }

    public void Configure(SaleDraft draft)
    {
        _draft = draft;
        AmountDueText.Text = $"৳ {draft.Total:0.00}";
        TenderedBox.Text = "";
        _tendered = 0m;
        ChangeText.Text = "৳ 0.00";
        SelectedMethod = PaymentMethod.Cash;
        UpdateMethodButtons();
        Payments.Clear();
    }

    private void Method_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out var m))
        {
            SelectedMethod = (PaymentMethod)m;
            UpdateMethodButtons();
            // For non-cash, default tendered to the amount due (exact)
            if (SelectedMethod != PaymentMethod.Cash)
            {
                _tendered = _draft?.Total ?? 0m;
                TenderedBox.Text = _tendered.ToString("0.00");
            }
            UpdateChange();
        }
    }

    private void UpdateMethodButtons()
    {
        void SetBtn(Button b, PaymentMethod m)
        {
            b.Style = (Style)FindResource(SelectedMethod == m ? "PrimaryButton" : "OutlineButton");
        }
        SetBtn(MethodCash, PaymentMethod.Cash);
        SetBtn(MethodCard, PaymentMethod.Card);
        SetBtn(MethodMobile, PaymentMethod.MobileWallet);
        SetBtn(MethodBank, PaymentMethod.BankTransfer);
    }

    private void TenderedBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (decimal.TryParse(TenderedBox.Text, out var v)) _tendered = v;
        else _tendered = 0m;
        UpdateChange();
    }

    private void TenderedBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var text = TenderedBox.Text + e.Text;
        e.Handled = !decimal.TryParse(text, out _);
    }

    private void UpdateChange()
    {
        var due = _draft?.Total ?? 0m;
        var change = Math.Max(0m, _tendered - due);
        ChangeText.Text = $"৳ {change:0.00}";
    }

    private void Pad_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            var text = TenderedBox.Text;
            if (tag == "back")
            {
                if (text.Length > 0) TenderedBox.Text = text[..^1];
            }
            else
            {
                TenderedBox.Text = text + tag;
            }
        }
    }

    private void QuickCash_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            if (tag == "exact")
            {
                var due = _draft?.Total ?? 0m;
                TenderedBox.Text = due.ToString("0.00");
            }
            else if (decimal.TryParse(tag, out var amt))
            {
                TenderedBox.Text = amt.ToString("0.00");
            }
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Complete_Click(object sender, RoutedEventArgs e)
    {
        if (_draft == null) return;
        var due = _draft.Total;
        if (_tendered < due && SelectedMethod == PaymentMethod.Cash)
        {
            MessageBox.Show(System.Windows.Application.Current.TryFindResource("Pay_Insufficient") as string ?? "Insufficient",
                "Payment", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Payments.Add(new SalePayment
        {
            Method = SelectedMethod,
            // The payment applied to the sale is the amount due. Cash tendered
            // above that amount is recorded separately so it becomes change,
            // not additional sales revenue.
            Amount = due,
            Reference = SelectedMethod == PaymentMethod.Card ? "card" : null
        });

        DialogResult = true;
        Close();
    }
}
