using System.Globalization;
using PosApp.Core.Models;

namespace PosApp.Core.Utilities;

public static class FormattingUtilities
{
    public static string Money(decimal value, StoreSettings settings, bool includeSymbol = true)
    {
        var decimals = Math.Clamp(settings.CurrencyDecimals, 0, 4);
        var formatted = value.ToString($"N{decimals}", CultureInfo.CurrentCulture);
        return includeSymbol && !string.IsNullOrWhiteSpace(settings.CurrencySymbol)
            ? $"{settings.CurrencySymbol} {formatted}"
            : formatted;
    }

    public static string CsvDecimal(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    public static bool TryParseDecimal(string? text, out decimal value)
        => decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value) ||
           decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    public static string CsvField(string? value)
    {
        value ??= string.Empty;
        // Spreadsheet applications can execute cells beginning with these characters as formulas.
        // Prefix text values so exported local data is safe to open in Excel or similar software.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t')
            value = "'" + value;
        return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    public static string UnprotectCsvField(string value)
    {
        if (value.Length > 1 && value[0] == '\'' && value[1] is '=' or '+' or '-' or '@' or '\t')
            return value[1..];
        return value;
    }
}
