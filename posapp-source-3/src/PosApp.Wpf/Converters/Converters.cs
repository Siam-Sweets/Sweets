using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PosApp.Wpf.Converters;

/// <summary>Converts a decimal money value to a formatted currency string.</summary>
public class MoneyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var d = System.Convert.ToDecimal(value);
        var sym = parameter as string ?? "৳";
        return $"{sym} {d:0.00}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class DecimalToMoney : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length == 0) return "0.00";
        if (!decimal.TryParse(values[0]?.ToString(), out var d)) d = 0m;
        var sym = values.Length > 1 ? values[1]?.ToString() ?? "৳" : "৳";
        return $"{sym} {d:0.00}";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is bool b && b) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is System.Windows.Visibility v && v == System.Windows.Visibility.Visible;
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => !(value is bool b && b);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => !(value is bool b && b);
}

public class StockColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is decimal d)
        {
            if (d <= 0) return new SolidColorBrush(Colors.OrangeRed);
            if (d <= 5) return new SolidColorBrush(Colors.DarkOrange);
            return new SolidColorBrush(Colors.ForestGreen);
        }
        return new SolidColorBrush(Colors.Gray);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class SaleStatusToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is PosApp.Core.Entities.SaleStatus s)
        {
            return s switch
            {
                PosApp.Core.Entities.SaleStatus.Completed => "Completed",
                PosApp.Core.Entities.SaleStatus.Suspended => "Suspended",
                PosApp.Core.Entities.SaleStatus.Voided => "Voided",
                PosApp.Core.Entities.SaleStatus.Refunded => "Refunded",
                _ => s.ToString()
            };
        }
        return value?.ToString() ?? "";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class PaymentMethodToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is PosApp.Core.Entities.PaymentMethod m)
        {
            return m switch
            {
                PosApp.Core.Entities.PaymentMethod.Cash => "Cash",
                PosApp.Core.Entities.PaymentMethod.Card => "Card",
                PosApp.Core.Entities.PaymentMethod.MobileWallet => "Mobile Wallet",
                PosApp.Core.Entities.PaymentMethod.BankTransfer => "Bank Transfer",
                PosApp.Core.Entities.PaymentMethod.StoreCredit => "Store Credit",
                PosApp.Core.Entities.PaymentMethod.Coupon => "Coupon",
                _ => m.ToString()
            };
        }
        return value?.ToString() ?? "";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value == null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class UserRoleToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is PosApp.Core.Entities.UserRole r)
        {
            return r switch
            {
                PosApp.Core.Entities.UserRole.Cashier => "Cashier",
                PosApp.Core.Entities.UserRole.Manager => "Manager",
                PosApp.Core.Entities.UserRole.Admin => "Admin",
                _ => r.ToString()
            };
        }
        return value?.ToString() ?? "";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
