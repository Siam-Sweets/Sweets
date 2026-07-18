using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;

namespace PosApp.Wpf.Views;

public partial class PromotionsView : UserControl, IRefreshable
{
    private readonly IDiscountService _discounts;
    private readonly ObservableCollection<PromotionRow> _rows = new();

    public PromotionsView(IDiscountService discounts)
    {
        InitializeComponent();
        _discounts = discounts;
        PromotionGrid.ItemsSource = _rows;
    }

    public async Task RefreshAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            var items = await _discounts.GetAllAsync();
            _rows.Clear();
            foreach (var item in items) _rows.Add(new PromotionRow(item));
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Promotions", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Add_Click(object sender, RoutedEventArgs e) => await EditAsync(new Discount());

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (PromotionGrid.SelectedItem is PromotionRow row) await EditAsync(Clone(row.Discount));
    }

    private async void PromotionGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PromotionGrid.SelectedItem is PromotionRow row) await EditAsync(Clone(row.Discount));
    }

    private async Task EditAsync(Discount discount)
    {
        var dialog = new PromotionEditorDialog(discount) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _discounts.SaveAsync(dialog.Value);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to save promotion", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Active_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not PromotionRow row)
            return;

        e.Handled = true;
        var previousState = row.IsActive;
        var requestedState = !previousState;

        checkBox.IsChecked = requestedState;
        checkBox.IsEnabled = false;
        try
        {
            await _discounts.SetActiveAsync(row.Discount.Id, requestedState);
            row.Discount.IsActive = requestedState;
            PromotionGrid.Items.Refresh();
        }
        catch (Exception ex)
        {
            row.Discount.IsActive = previousState;
            checkBox.IsChecked = previousState;
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to update promotion",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            checkBox.IsEnabled = true;
        }
    }

    private async void Deactivate_Click(object sender, RoutedEventArgs e)
    {
        if (PromotionGrid.SelectedItem is not PromotionRow row || !row.IsActive) return;
        if (PosApp.Wpf.Helpers.LocalizedMessageBox.Show($"Deactivate {row.Name}?", "Promotion", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await _discounts.DeactivateAsync(row.Discount.Id);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(ex.Message, "Unable to deactivate promotion", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static Discount Clone(Discount source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Description = source.Description,
        Type = source.Type,
        Value = source.Value,
        Code = source.Code,
        ValidFrom = source.ValidFrom,
        ValidTo = source.ValidTo,
        MaxUses = source.MaxUses,
        UsedCount = source.UsedCount,
        IsActive = source.IsActive,
        CreatedAt = source.CreatedAt
    };
}

public sealed class PromotionRow
{
    public PromotionRow(Discount discount) => Discount = discount;
    public Discount Discount { get; }
    public string Name => Discount.Name;
    public string? Code => Discount.Code;
    public DiscountType Type => Discount.Type;
    public decimal Value => Discount.Value;
    public DateTime? ValidFrom => Discount.ValidFrom;
    public DateTime? ValidTo => Discount.ValidTo;
    public bool IsActive => Discount.IsActive;
    public string UsageDisplay => Discount.MaxUses.HasValue ? $"{Discount.UsedCount}/{Discount.MaxUses}" : Discount.UsedCount.ToString();
}

public sealed class PromotionEditorDialog : Window
{
    private readonly TextBox _name = new();
    private readonly TextBox _code = new();
    private readonly TextBox _description = new();
    private readonly ComboBox _type = new();
    private readonly TextBox _value = new();
    private readonly DatePicker _from = new();
    private readonly DatePicker _to = new();
    private readonly TextBox _maxUses = new();
    private readonly CheckBox _active = new();
    public Discount Value { get; }

    public PromotionEditorDialog(Discount value)
    {
        Value = value;
        Title = value.Id == 0 ? "New Promotion" : "Edit Promotion";
        Width = 610;
        Height = 650;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BackgroundBrush");

        var root = new Grid { Margin = new Thickness(20) };
        for (var i = 0; i < 7; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 2 ? new GridLength(100) : GridLength.Auto });
        root.Children.Add(Field("Name", _name, 0));

        var two = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        two.Children.Add(Field("Code (optional)", _code, 0, false));
        var typeField = Field("Type", _type, 0, false);
        Grid.SetColumn(typeField, 1);
        two.Children.Add(typeField);
        Grid.SetRow(two, 1);
        root.Children.Add(two);

        _description.AcceptsReturn = true;
        _description.TextWrapping = TextWrapping.Wrap;
        root.Children.Add(Field("Description", _description, 2));

        var valueDates = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        valueDates.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        valueDates.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        valueDates.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        valueDates.Children.Add(Field("Value", _value, 0, false));
        var fromField = Field("Valid from", _from, 0, false); Grid.SetColumn(fromField, 1); valueDates.Children.Add(fromField);
        var toField = Field("Valid to", _to, 0, false); Grid.SetColumn(toField, 2); valueDates.Children.Add(toField);
        Grid.SetRow(valueDates, 3); root.Children.Add(valueDates);

        root.Children.Add(Field("Maximum uses (blank = unlimited)", _maxUses, 4));
        _active.Content = "Active";
        _active.Margin = new Thickness(0, 4, 0, 8);
        Grid.SetRow(_active, 5); root.Children.Add(_active);
        root.Children.Add(DialogLayout.CreateButtons(6, Accept, () => Close()));
        Content = root;

        _type.Items.Add(DiscountType.Percentage);
        _type.Items.Add(DiscountType.FixedAmount);
        _name.Text = value.Name;
        _code.Text = value.Code ?? string.Empty;
        _description.Text = value.Description ?? string.Empty;
        _type.SelectedItem = value.Type;
        _value.Text = value.Value.ToString("0.##");
        _from.SelectedDate = value.ValidFrom;
        _to.SelectedDate = value.ValidTo;
        _maxUses.Text = value.MaxUses?.ToString() ?? string.Empty;
        _active.IsChecked = value.Id == 0 || value.IsActive;
    }

    private static FrameworkElement Field(string label, Control control, int row, bool margin = true)
    {
        var panel = new StackPanel { Margin = margin ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 8, 0) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextMutedBrush"), Margin = new Thickness(0, 0, 0, 4) });
        control.Padding = new Thickness(10, 7, 10, 7);
        panel.Children.Add(control);
        Grid.SetRow(panel, row);
        return panel;
    }

    private void Accept()
    {
        if (string.IsNullOrWhiteSpace(_name.Text) || !DialogLayout.TryParseDecimal(_value.Text, out var amount))
        {
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Enter a name and valid value.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        int? maxUses = null;
        if (!string.IsNullOrWhiteSpace(_maxUses.Text))
        {
            if (!int.TryParse(_maxUses.Text, out var parsed) || parsed < 1)
            {
                PosApp.Wpf.Helpers.LocalizedMessageBox.Show("Maximum uses must be blank or a positive whole number.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            maxUses = parsed;
        }
        Value.Name = _name.Text.Trim();
        Value.Code = string.IsNullOrWhiteSpace(_code.Text) ? null : _code.Text.Trim();
        Value.Description = string.IsNullOrWhiteSpace(_description.Text) ? null : _description.Text.Trim();
        Value.Type = _type.SelectedItem is DiscountType type ? type : DiscountType.Percentage;
        Value.Value = amount;
        Value.ValidFrom = _from.SelectedDate;
        Value.ValidTo = _to.SelectedDate?.Date.AddDays(1).AddTicks(-1);
        Value.MaxUses = maxUses;
        Value.IsActive = _active.IsChecked == true;
        DialogResult = true;
        Close();
    }
}
