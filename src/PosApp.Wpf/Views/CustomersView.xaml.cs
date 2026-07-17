using System.Windows;
using System.Windows.Controls;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;

namespace PosApp.Wpf.Views;

public partial class CustomersView : UserControl, IRefreshable
{
    private readonly ICustomerService _customers;
    private readonly IPurchaseService _purchases;
    private List<ContactListItem> _all = new();

    public CustomersView(ICustomerService customers, IPurchaseService purchases)
    {
        InitializeComponent();
        _customers = customers;
        _purchases = purchases;
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
            MessageBox.Show(ex.Message, "Unable to load customers and suppliers",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async Task LoadAsync()
    {
        var customersTask = _customers.SearchCustomersAsync(null);
        var suppliersTask = _purchases.SearchSuppliersAsync(null);
        await Task.WhenAll(customersTask, suppliersTask);

        _all = customersTask.Result
            .Select(ContactListItem.FromCustomer)
            .Concat(suppliersTask.Result.Select(ContactListItem.FromSupplier))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.TypeLabel, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        ApplyFilter();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (ContactsGrid == null) return;
        var term = SearchBox?.Text?.Trim() ?? string.Empty;
        ContactsGrid.ItemsSource = string.IsNullOrEmpty(term)
            ? _all
            : _all.Where(item =>
                    item.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    item.TypeLabel.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    (item.Phone?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (item.Email?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContactEditDialog(_customers, _purchases)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true) Refresh();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ContactListItem item }) return;
        var dialog = new ContactEditDialog(_customers, _purchases, item)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true) Refresh();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ContactListItem item }) return;

        if (item.IsCustomer)
        {
            var confirm = MessageBox.Show($"Delete customer '{item.Name}'?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                await _customers.DeleteCustomerAsync(item.Id);
                Refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Cannot delete customer",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        var supplierConfirm = MessageBox.Show(
            $"Remove supplier '{item.Name}'? Existing purchase history will be kept.",
            "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (supplierConfirm != MessageBoxResult.Yes) return;

        try
        {
            await _purchases.DeactivateSupplierAsync(item.Id);
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Cannot remove supplier",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void History_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ContactListItem { Customer: not null } item }) return;
        var history = await _customers.GetCustomerHistoryAsync(item.Customer.Id);
        new CustomerHistoryDialog(item.Customer, history)
        {
            Owner = Window.GetWindow(this)
        }.ShowDialog();
    }
}

public sealed class ContactListItem
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? Address { get; init; }
    public string? TaxId { get; init; }
    public string? Notes { get; init; }
    public required ContactRecordType RecordType { get; init; }
    public Customer? Customer { get; init; }
    public Supplier? Supplier { get; init; }

    public bool IsCustomer => RecordType == ContactRecordType.Customer;
    public string TypeLabel => IsCustomer
        ? Application.Current.TryFindResource("Cust_Customer") as string ?? "Customer"
        : Application.Current.TryFindResource("Cust_Supplier") as string ?? "Supplier";

    public static ContactListItem FromCustomer(Customer customer) => new()
    {
        Id = customer.Id,
        Name = customer.Name,
        Phone = customer.Phone,
        Email = customer.Email,
        Address = customer.Address,
        TaxId = customer.TaxId,
        RecordType = ContactRecordType.Customer,
        Customer = customer
    };

    public static ContactListItem FromSupplier(Supplier supplier) => new()
    {
        Id = supplier.Id,
        Name = supplier.Name,
        Phone = supplier.Phone,
        Email = supplier.Email,
        Address = supplier.Address,
        TaxId = supplier.TaxId,
        Notes = supplier.Notes,
        RecordType = ContactRecordType.Supplier,
        Supplier = supplier
    };
}

public enum ContactRecordType
{
    Customer,
    Supplier
}

public sealed class ContactEditDialog : Window
{
    private readonly ICustomerService _customers;
    private readonly IPurchaseService _purchases;
    private readonly ContactListItem? _existing;
    private readonly ComboBox _type = new();
    private readonly TextBox _name = new();
    private readonly TextBox _phone = new();
    private readonly TextBox _email = new();
    private readonly TextBox _taxId = new();
    private readonly TextBox _address = new()
    {
        AcceptsReturn = true,
        Height = 70,
        TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBox _notes = new()
    {
        AcceptsReturn = true,
        Height = 70,
        TextWrapping = TextWrapping.Wrap
    };
    private readonly FrameworkElement _notesField;
    private readonly Button _saveButton;

    public ContactEditDialog(
        ICustomerService customers,
        IPurchaseService purchases,
        ContactListItem? existing = null)
    {
        _customers = customers;
        _purchases = purchases;
        _existing = existing;

        Title = existing == null ? "Add Customer or Supplier" : $"Edit {existing.TypeLabel}";
        Width = 520;
        Height = 650;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BackgroundBrush");

        _type.Items.Add(new ComboBoxItem { Content = "Customer", Tag = ContactRecordType.Customer });
        _type.Items.Add(new ComboBoxItem { Content = "Supplier", Tag = ContactRecordType.Supplier });
        _type.SelectedIndex = existing?.RecordType == ContactRecordType.Supplier ? 1 : 0;
        _type.IsEnabled = existing == null;
        _type.SelectionChanged += (_, _) => UpdateTypeUi();

        if (existing != null)
        {
            _name.Text = existing.Name;
            _phone.Text = existing.Phone ?? string.Empty;
            _email.Text = existing.Email ?? string.Empty;
            _taxId.Text = existing.TaxId ?? string.Empty;
            _address.Text = existing.Address ?? string.Empty;
            _notes.Text = existing.Notes ?? string.Empty;
        }

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Field("Contact type", _type));
        panel.Children.Add(Field("Name", _name));
        panel.Children.Add(Field("Phone", _phone));
        panel.Children.Add(Field("Email", _email));
        panel.Children.Add(Field("Tax ID / registration", _taxId));
        panel.Children.Add(Field("Address", _address));
        _notesField = Field("Supplier notes", _notes);
        panel.Children.Add(_notesField);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancel = new Button
        {
            Content = "Cancel",
            Style = (Style)Application.Current.FindResource("OutlineButton"),
            Padding = new Thickness(20, 10, 20, 10),
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancel.Click += (_, _) =>
        {
            DialogResult = false;
            Close();
        };
        _saveButton = new Button
        {
            Style = (Style)Application.Current.FindResource("PrimaryButton"),
            Padding = new Thickness(20, 10, 20, 10)
        };
        _saveButton.Click += Save_Click;
        actions.Children.Add(cancel);
        actions.Children.Add(_saveButton);
        panel.Children.Add(actions);

        Content = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        UpdateTypeUi();
    }

    private ContactRecordType SelectedType =>
        _type.SelectedItem is ComboBoxItem { Tag: ContactRecordType type }
            ? type
            : ContactRecordType.Customer;

    private void UpdateTypeUi()
    {
        var supplier = SelectedType == ContactRecordType.Supplier;
        _notesField.Visibility = supplier ? Visibility.Visible : Visibility.Collapsed;
        _saveButton.Content = supplier ? "Save Supplier" : "Save Customer";
        if (_existing == null)
            Title = supplier ? "Add Supplier" : "Add Customer";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = _name.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Name is required.", "Contact",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            _name.Focus();
            return;
        }

        IsEnabled = false;
        try
        {
            if (SelectedType == ContactRecordType.Customer)
            {
                var source = _existing?.Customer;
                var customer = new Customer
                {
                    Id = source?.Id ?? 0,
                    Name = name,
                    Phone = Empty(_phone.Text),
                    Email = Empty(_email.Text),
                    TaxId = Empty(_taxId.Text),
                    Address = Empty(_address.Text),
                    LoyaltyPoints = source?.LoyaltyPoints ?? 0m,
                    StoreCredit = source?.StoreCredit ?? 0m,
                    LoyaltyRate = source?.LoyaltyRate ?? 0m,
                    CreatedAt = source?.CreatedAt ?? DateTime.UtcNow,
                    UpdatedAt = source?.UpdatedAt
                };
                await _customers.CreateOrUpdateCustomerAsync(customer);
            }
            else
            {
                var source = _existing?.Supplier;
                var supplier = new Supplier
                {
                    Id = source?.Id ?? 0,
                    Name = name,
                    Phone = Empty(_phone.Text),
                    Email = Empty(_email.Text),
                    TaxId = Empty(_taxId.Text),
                    Address = Empty(_address.Text),
                    Notes = Empty(_notes.Text),
                    IsActive = source?.IsActive ?? true,
                    CreatedAt = source?.CreatedAt ?? DateTime.UtcNow,
                    UpdatedAt = source?.UpdatedAt
                };
                await _purchases.CreateOrUpdateSupplierAsync(supplier);
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, $"Unable to save {SelectedType.ToString().ToLowerInvariant()}",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private static FrameworkElement Field(string label, FrameworkElement control)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextMutedBrush"),
            Margin = new Thickness(0, 0, 0, 4)
        });
        stack.Children.Add(control);
        return stack;
    }

    private static string? Empty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class CustomerHistoryDialog : Window
{
    public CustomerHistoryDialog(Customer customer, IReadOnlyList<Sale> history)
    {
        Title = $"{customer.Name} - Purchase History";
        Width = 720;
        Height = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BackgroundBrush");

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var summary = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 12),
            FontSize = 13
        };
        summary.Inlines.Add(new System.Windows.Documents.Run($"Total purchases: {history.Count}    ")
        {
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextMutedBrush")
        });
        summary.Inlines.Add(new System.Windows.Documents.Run($"Total spent: ৳ {history.Sum(sale => sale.Total):0.00}")
        {
            FontWeight = FontWeights.Bold
        });
        grid.Children.Add(summary);
        Grid.SetRow(summary, 0);

        var dataGrid = new DataGrid
        {
            ItemsSource = history.ToList(),
            AutoGenerateColumns = false,
            IsReadOnly = true
        };
        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Receipt",
            Binding = new System.Windows.Data.Binding("ReceiptNumber"),
            Width = 140
        });
        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Date",
            Binding = new System.Windows.Data.Binding("SaleDate") { StringFormat = "yyyy-MM-dd HH:mm" },
            Width = 140
        });
        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Items",
            Binding = new System.Windows.Data.Binding("Items.Count"),
            Width = 60
        });
        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Total",
            Binding = new System.Windows.Data.Binding("Total") { StringFormat = "৳ {0:0.00}" },
            Width = 100
        });
        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Status",
            Binding = new System.Windows.Data.Binding("Status"),
            Width = 100
        });
        grid.Children.Add(dataGrid);
        Grid.SetRow(dataGrid, 1);

        Content = grid;
    }
}
