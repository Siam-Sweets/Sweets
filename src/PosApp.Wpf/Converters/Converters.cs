using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PosApp.Core.Entities;
using PosApp.Core.Utilities;

namespace PosApp.Wpf.Converters;

internal static class ConverterText
{
    public static string Get(string key, string fallback)
        => Application.Current?.TryFindResource(key) as string ?? fallback;
}

/// <summary>Converts a decimal money value to a formatted currency string.</summary>
public class MoneyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var d = value == null ? 0m : System.Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        if (parameter is string symbol && !string.IsNullOrWhiteSpace(symbol))
        {
            var decimals = Math.Clamp(PosApp.Wpf.App.StoreSettings.CurrencyDecimals, 0, 4);
            return $"{symbol} {d.ToString($"N{decimals}", culture)}";
        }
        return FormattingUtilities.Money(d, PosApp.Wpf.App.StoreSettings);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public class DecimalToMoney : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length == 0)
            return FormattingUtilities.Money(0m, PosApp.Wpf.App.StoreSettings);
        if (!decimal.TryParse(values[0]?.ToString(), NumberStyles.Number, culture, out var d) &&
            !decimal.TryParse(values[0]?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out d))
            d = 0m;
        var settings = PosApp.Wpf.App.StoreSettings;
        if (values.Length > 1 && values[1] is string symbol && !string.IsNullOrWhiteSpace(symbol))
        {
            var decimals = Math.Clamp(settings.CurrencyDecimals, 0, 4);
            return $"{symbol} {d.ToString($"N{decimals}", culture)}";
        }
        return FormattingUtilities.Money(d, settings);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
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
        => Binding.DoNothing;
}

public class SaleStatusToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is PosApp.Core.Entities.SaleStatus s)
        {
            return s switch
            {
                PosApp.Core.Entities.SaleStatus.Completed => ConverterText.Get("Sales_StatusCompleted", "Completed"),
                PosApp.Core.Entities.SaleStatus.Suspended => ConverterText.Get("Sales_StatusSuspended", "Suspended"),
                PosApp.Core.Entities.SaleStatus.Voided => ConverterText.Get("Sales_StatusVoided", "Voided"),
                PosApp.Core.Entities.SaleStatus.Refunded => ConverterText.Get("Sales_StatusRefunded", "Refunded"),
                _ => s.ToString()
            };
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public class PaymentMethodToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is PosApp.Core.Entities.PaymentMethod m)
        {
            return m switch
            {
                PosApp.Core.Entities.PaymentMethod.Cash => ConverterText.Get("Pay_Cash", "Cash"),
                PosApp.Core.Entities.PaymentMethod.Card => ConverterText.Get("Pay_Card", "Card"),
                PosApp.Core.Entities.PaymentMethod.MobileWallet => ConverterText.Get("Pay_Mobile", "Mobile Wallet"),
                PosApp.Core.Entities.PaymentMethod.BankTransfer => ConverterText.Get("Pay_Bank", "Bank Transfer"),
                PosApp.Core.Entities.PaymentMethod.StoreCredit => ConverterText.Get("Pay_StoreCredit", "Store Credit"),
                PosApp.Core.Entities.PaymentMethod.Coupon => ConverterText.Get("Pay_Coupon", "Coupon"),
                _ => m.ToString()
            };
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value == null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public class UserRoleToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is PosApp.Core.Entities.UserRole r)
        {
            return r switch
            {
                PosApp.Core.Entities.UserRole.Cashier => ConverterText.Get("Usr_Cashier", "Cashier"),
                PosApp.Core.Entities.UserRole.Manager => ConverterText.Get("Usr_Manager", "Manager"),
                PosApp.Core.Entities.UserRole.Admin => ConverterText.Get("Usr_Admin", "Admin"),
                _ => r.ToString()
            };
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public class ActiveActionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool active && active
            ? ConverterText.Get("Common_Deactivate", "Deactivate")
            : ConverterText.Get("Common_Restore", "Restore");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public class ProductSaleModeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is ProductSaleMode mode
            ? mode switch
            {
                ProductSaleMode.Weight => ConverterText.Get("Prod_SoldByWeight", "By weight"),
                ProductSaleMode.Volume => ConverterText.Get("Prod_SoldByVolume", "By volume"),
                ProductSaleMode.Length => ConverterText.Get("Prod_SoldByLength", "By length"),
                _ => ConverterText.Get("Prod_SoldPerItem", "Per item")
            }
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public class UtcToLocalConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime dateTime) return value;
        var local = DateTimeUtilities.ToLocal(dateTime);
        var format = parameter as string;
        return string.IsNullOrWhiteSpace(format) ? local : local.ToString(format, culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
