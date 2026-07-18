using System.Text;
using System.Windows;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Utilities;

namespace PosApp.Wpf.Helpers;

/// <summary>
/// Shared text-report formatting and printer feedback for management pages.
/// Reports intentionally use a compact width so they remain readable on both
/// receipt printers and regular Windows printers.
/// </summary>
internal static class PageReportPrinter
{
    public static int Width => App.StoreSettings.ReceiptWidth switch
    {
        <= 60 => 32,
        <= 90 => 48,
        _ => 64
    };

    public static string Line(char character = '-') => new(character, Width);

    public static void AppendHeader(StringBuilder builder, string title, string? period = null)
    {
        AppendWrapped(builder, App.StoreSettings.StoreName);
        AppendWrapped(builder, title);
        if (!string.IsNullOrWhiteSpace(period)) AppendWrapped(builder, period);
        builder.AppendLine(Line('='));
    }

    public static void AppendSection(StringBuilder builder, string title)
    {
        if (builder.Length > 0 && !builder.ToString().EndsWith("\n\n", StringComparison.Ordinal))
            builder.AppendLine();
        AppendWrapped(builder, title.ToUpperInvariant());
        builder.AppendLine(Line());
    }

    public static void AppendMetric(StringBuilder builder, string label, string value)
    {
        var cleanLabel = Clean(label);
        var cleanValue = Clean(value);
        var available = Width - cleanValue.Length - 1;
        if (available >= 8)
        {
            builder.Append(cleanLabel.Length > available ? Fit(cleanLabel, available) : cleanLabel.PadRight(available));
            builder.Append(' ');
            builder.AppendLine(cleanValue);
            return;
        }

        AppendWrapped(builder, cleanLabel);
        AppendWrapped(builder, $"Value: {cleanValue}");
    }

    public static void AppendEntry(StringBuilder builder, string primary, params string?[] details)
    {
        AppendWrapped(builder, primary);
        foreach (var detail in details)
        {
            if (string.IsNullOrWhiteSpace(detail)) continue;
            AppendWrapped(builder, $"- {detail}");
        }
    }

    public static void AppendWrapped(StringBuilder builder, string? value)
    {
        var text = Clean(value);
        if (text.Length == 0)
        {
            builder.AppendLine();
            return;
        }

        var remaining = text;
        while (remaining.Length > Width)
        {
            var split = remaining.LastIndexOf(' ', Width);
            if (split < Math.Max(8, Width / 3)) split = Width;
            builder.AppendLine(remaining[..split].TrimEnd());
            remaining = remaining[split..].TrimStart();
        }
        builder.AppendLine(remaining);
    }

    public static string Fit(string? value, int width)
    {
        if (width <= 0) return string.Empty;
        var text = Clean(value);
        if (text.Length <= width) return text.PadRight(width);
        return width == 1 ? "…" : text[..(width - 1)] + "…";
    }

    public static string Money(decimal value) => FormattingUtilities.Money(value, App.StoreSettings);

    public static string PaymentMethodName(PaymentMethod method) => method switch
    {
        PaymentMethod.MobileWallet => "Mobile wallet",
        PaymentMethod.BankTransfer => "Bank transfer",
        PaymentMethod.StoreCredit => "Store credit",
        _ => method.ToString()
    };

    public static async Task PrintAsync(IHardwareService hardware, string report, string logContext)
    {
        try
        {
            var ok = await hardware.PrintTextAsync(report);
            LocalizedMessageBox.Show(
                ok ? "Report sent to the printer." : "Printing failed. Check printer settings.",
                Resource("Common_Print", "Print"),
                MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            App.LogError($"Print {logContext}", ex);
            LocalizedMessageBox.Show(
                ex.GetBaseException().Message,
                "Print failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static string Resource(string key, string fallback)
        => Application.Current?.TryFindResource(key) as string ?? fallback;

    private static string Clean(string? value)
        => (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
}
