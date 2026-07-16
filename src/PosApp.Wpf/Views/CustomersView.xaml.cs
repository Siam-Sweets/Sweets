using System.Windows;
using System.Windows.Controls;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;

namespace PosApp.Wpf.Views;

public partial class CustomersView : UserControl, IRefreshable
{
    private readonly ICustomerService _customers;
    private List<Customer> _all = new();

    public CustomersView(ICustomerService customers)
    {
        InitializeComponent();
        _customers = customers;
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
            MessageBox.Show(ex.Message, "Unable to load customers", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async Task LoadAsync()
    {
        _all = (await _customers.SearchCustomersAsync(null)).ToList();
        CustomersGrid.ItemsSource = _all;
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (CustomersGrid == null) return;
        var term = SearchBox.Text?.Trim() ?? "";
        var filtered = _all.Where(c =>
            string.IsNullOrEmpty(term) ||
            c.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            (c.Phone?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (c.Email?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        CustomersGrid.ItemsSource = filtered;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CustomerEditDialog(_customers) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) Refresh();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Customer c)
        {
            var dlg = new CustomerEditDialog(_customers, c) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true) Refresh();
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Customer c)
        {
            var confirm = MessageBox.Show($"Delete customer '{c.Name}'?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            try
            {
                await _customers.DeleteCustomerAsync(c.Id);
                Refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Cannot delete", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private async void History_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Customer c)
        {
            var history = await _customers.GetCustomerHistoryAsync(c.Id);
            var dlg = new CustomerHistoryDialog(c, history) { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
        }
    }
}

public class CustomerEditDialog : Window
{
    private readonly ICustomerService _svc;
    private readonly Customer _c;
    private readonly bool _isNew;

    public CustomerEditDialog(ICustomerService svc, Customer? existing = null)
    {
        _svc = svc;
        _isNew = existing == null;
        _c = existing ?? new Customer();

        Title = _isNew ? "Add Customer" : "Edit Customer";
        Width = 480; Height = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BackgroundBrush");

        var panel = new StackPanel { Margin = new Thickness(24) };

        var nameBox = new TextBox { Text = _c.Name };
        var phoneBox = new TextBox { Text = _c.Phone ?? "" };
        var emailBox = new TextBox { Text = _c.Email ?? "" };
        var addressBox = new TextBox { Text = _c.Address ?? "", AcceptsReturn = true, Height = 70, TextWrapping = TextWrapping.Wrap };

        panel.Children.Add(MakeRow("Name", nameBox));
        panel.Children.Add(MakeRow("Phone", phoneBox));
        panel.Children.Add(MakeRow("Email", emailBox));
        panel.Children.Add(MakeRow("Address", addressBox));

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
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
            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                MessageBox.Show("Name is required", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _c.Name = nameBox.Text;
            _c.Phone = phoneBox.Text;
            _c.Email = emailBox.Text;
            _c.Address = addressBox.Text;
            try
            {
                await _svc.CreateOrUpdateCustomerAsync(_c);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Unable to save customer", MessageBoxButton.OK, MessageBoxImage.Error);
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
        ctrl.Margin = new Thickness(0, 0, 0, 12);
        return new System.Windows.Controls.Border { Child = stack };
    }
}

public class CustomerHistoryDialog : Window
{
    public CustomerHistoryDialog(Customer c, System.Collections.Generic.IReadOnlyList<Sale> history)
    {
        Title = $"{c.Name} - Purchase History";
        Width = 720; Height = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BackgroundBrush");

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var summary = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 12),
            FontSize = 13
        };
        summary.Inlines.Add(new System.Windows.Documents.Run($"Total purchases: {history.Count}    ")
            { Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextMutedBrush") });
        summary.Inlines.Add(new System.Windows.Documents.Run($"Total spent: ৳ {history.Sum(s => s.Total):0.00}")
            { FontWeight = FontWeights.Bold });
        grid.Children.Add(summary); Grid.SetRow(summary, 0);

        var dg = new DataGrid
        {
            ItemsSource = history.ToList(),
            AutoGenerateColumns = false,
            IsReadOnly = true
        };
        dg.Columns.Add(new DataGridTextColumn { Header = "Receipt", Binding = new System.Windows.Data.Binding("ReceiptNumber"), Width = 140 });
        dg.Columns.Add(new DataGridTextColumn { Header = "Date", Binding = new System.Windows.Data.Binding("SaleDate") { StringFormat = "yyyy-MM-dd HH:mm" }, Width = 140 });
        dg.Columns.Add(new DataGridTextColumn { Header = "Items", Binding = new System.Windows.Data.Binding("Items.Count"), Width = 60 });
        dg.Columns.Add(new DataGridTextColumn { Header = "Total", Binding = new System.Windows.Data.Binding("Total") { StringFormat = "৳ {0:0.00}" }, Width = 100 });
        dg.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new System.Windows.Data.Binding("Status"), Width = 100 });
        grid.Children.Add(dg); Grid.SetRow(dg, 1);

        Content = grid;
    }
}
