using System.Windows;
using PosApp.Core.Entities;

namespace PosApp.Wpf.Views;

public partial class StoreEditDialog : Window
{
    private readonly Store? _source;
    public Store? Result { get; private set; }

    public StoreEditDialog(Store? source)
    {
        InitializeComponent();
        _source = source;
        if (source == null) return;
        CodeBox.Text = source.Code;
        NameBox.Text = source.Name;
        AddressBox.Text = source.Address;
        PhoneBox.Text = source.Phone;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = new Store
        {
            Id = _source?.Id ?? 0,
            SyncId = _source?.SyncId ?? Guid.NewGuid().ToString("N"),
            Code = CodeBox.Text,
            Name = NameBox.Text,
            Address = AddressBox.Text,
            Phone = PhoneBox.Text,
            IsActive = _source?.IsActive ?? true,
            CreatedAt = _source?.CreatedAt ?? DateTime.UtcNow,
            UpdatedAt = _source?.UpdatedAt
        };
        DialogResult = true;
    }
}
