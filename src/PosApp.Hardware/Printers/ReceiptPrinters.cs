using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Text;
using PosApp.Core.Entities;
using PosApp.Core.Models;
using PosApp.Core.Interfaces;
using PosApp.Core.Utilities;

namespace PosApp.Hardware.Printers;

/// <summary>
/// ESC/POS thermal receipt printer driver. Sends raw bytes to the
/// Windows spooler via Winspool's WritePrinter API. Works with most
/// 58mm and 80mm thermal printers (EPSON TM series, Xprinter, etc.).
/// </summary>
public class EscPosPrinter : IReceiptPrinter
{
    private readonly ISettingsService _settings;
    public EscPosPrinter(ISettingsService settings) => _settings = settings;

    public bool IsConnected => PrinterSettings.InstalledPrinters.Count > 0;

    public async Task<bool> PrintAsync(Sale sale)
    {
        var store = await _settings.GetStoreSettingsAsync();
        return await SendAsync(BuildReceipt(sale, store), store.ReceiptPrinterName, "POS Receipt");
    }

    public async Task<bool> PrintTextAsync(string text)
    {
        var store = await _settings.GetStoreSettingsAsync();
        return await SendAsync(text, store.ReceiptPrinterName, "POS Text");
    }

    private static Task<bool> SendAsync(string text, string? configuredPrinter, string documentName)
        => Task.Run(() =>
        {
            var printerName = string.IsNullOrWhiteSpace(configuredPrinter)
                ? (PrinterSettings.InstalledPrinters.Count > 0 ? new PrinterSettings().PrinterName : string.Empty)
                : configuredPrinter;
            if (string.IsNullOrWhiteSpace(printerName)) return false;
            try { return RawPrinterHelper.SendRawToPrinter(printerName, Encoding.UTF8.GetBytes(text), documentName); }
            catch { return false; }
        });

    private static string BuildReceipt(Sale sale, StoreSettings store)
    {
        var sb = new StringBuilder();
        sb.Append(EscPosConst.Initialize);
        sb.Append(EscPosConst.AlignCenter);
        sb.Append(EscPosConst.DoubleOn);
        sb.AppendLine(store.StoreName);
        sb.Append(EscPosConst.DoubleOff);
        if (!string.IsNullOrEmpty(store.Address)) sb.AppendLine(store.Address);
        if (!string.IsNullOrEmpty(store.Phone)) sb.AppendLine($"Tel: {store.Phone}");
        if (!string.IsNullOrEmpty(store.TaxId)) sb.AppendLine($"Tax ID: {store.TaxId}");
        sb.AppendLine(new string('-', 32));
        sb.Append(EscPosConst.AlignLeft);
        sb.AppendLine($"Receipt: {sale.ReceiptNumber}");
        sb.AppendLine($"Date: {DateTimeUtilities.ToLocal(sale.SaleDate):yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Cashier: {sale.User?.FullName ?? sale.UserId.ToString()}");
        sb.AppendLine(new string('-', 32));

        foreach (var item in sale.Items)
        {
            var line = $"{item.QuantityDisplay} x {item.ProductName}";
            sb.AppendLine(line);
            sb.Append(EscPosConst.AlignRight);
            sb.AppendLine(FormattingUtilities.Money(item.LineTotal + item.LineTax, store));
            sb.Append(EscPosConst.AlignLeft);
        }

        sb.AppendLine(new string('-', 32));
        sb.Append(EscPosConst.AlignRight);
        sb.AppendLine($"Subtotal:    {FormattingUtilities.Money(sale.Subtotal, store)}");
        if (sale.DiscountTotal != 0m) sb.AppendLine($"Discount:    {FormattingUtilities.Money(-sale.DiscountTotal, store)}");
        if (sale.TaxTotal != 0m) sb.AppendLine($"Tax:         {FormattingUtilities.Money(sale.TaxTotal, store)}");
        sb.Append(EscPosConst.DoubleOn);
        sb.AppendLine($"TOTAL:       {FormattingUtilities.Money(sale.Total, store)}");
        sb.Append(EscPosConst.DoubleOff);
        sb.AppendLine();

        foreach (var payment in ReceiptPaymentSummary.Build(sale))
            sb.AppendLine($"{payment.Label}: {FormattingUtilities.Money(payment.Amount, store)}");
        if (sale.Change > 0) sb.AppendLine($"Change: {FormattingUtilities.Money(sale.Change, store)}");

        sb.Append(EscPosConst.AlignCenter);
        sb.AppendLine();
        sb.AppendLine(store.FooterNote);
        sb.AppendLine();
        sb.Append(EscPosConst.Cut);
        return sb.ToString();
    }
}

internal sealed class ReceiptPaymentLine
{
    public ReceiptPaymentLine(string label, decimal amount)
    {
        Label = label;
        Amount = amount;
    }

    public string Label { get; }
    public decimal Amount { get; }
}

internal static class ReceiptPaymentSummary
{
    public static IReadOnlyList<ReceiptPaymentLine> Build(Sale sale)
    {
        var payments = sale.Payments.ToList();
        var lines = new List<ReceiptPaymentLine>();
        if (payments.Count == 0) return lines;

        var cashApplied = payments
            .Where(payment => payment.Method == PaymentMethod.Cash)
            .Sum(payment => payment.Amount);
        var nonCashApplied = payments
            .Where(payment => payment.Method != PaymentMethod.Cash)
            .Sum(payment => payment.Amount);
        var grossReceived = sale.AmountPaid > 0m
            ? sale.AmountPaid
            : cashApplied + nonCashApplied;
        var cashTendered = Math.Max(cashApplied, grossReceived - nonCashApplied);
        var cashWritten = false;

        foreach (var payment in payments)
        {
            if (payment.Method == PaymentMethod.Cash)
            {
                if (cashWritten) continue;
                lines.Add(new ReceiptPaymentLine("Cash tendered", cashTendered));
                cashWritten = true;
                continue;
            }

            lines.Add(new ReceiptPaymentLine(MethodName(payment.Method), payment.Amount));
        }

        return lines;
    }

    private static string MethodName(PaymentMethod method) => method switch
    {
        PaymentMethod.Card => "Card",
        PaymentMethod.MobileWallet => "Mobile wallet",
        PaymentMethod.BankTransfer => "Bank transfer",
        PaymentMethod.StoreCredit => "Store credit",
        PaymentMethod.Coupon => "Coupon",
        PaymentMethod.Other => "Other",
        _ => method.ToString()
    };
}

internal static class EscPosConst
{
    public const string Initialize = "\x1B\x40";
    public const string AlignLeft = "\x1B\x61\x00";
    public const string AlignCenter = "\x1B\x61\x01";
    public const string AlignRight = "\x1B\x61\x02";
    public const string DoubleOn = "\x1D\x21\x11";
    public const string DoubleOff = "\x1D\x21\x00";
    public const string Cut = "\x1D\x56\x00";
}

internal static class RawPrinterHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private class DOCINFOA
    {
        [MarshalAs(UnmanagedType.LPStr)] public string? pDocName;
        [MarshalAs(UnmanagedType.LPStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPStr)] public string? pDataType;
    }

    [DllImport("winspool.Drv", EntryPoint = "OpenPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern bool OpenPrinter([MarshalAs(UnmanagedType.LPStr)] string szPrinter, out IntPtr hPrinter, IntPtr pd);

    [DllImport("winspool.Drv", EntryPoint = "ClosePrinter", SetLastError = true, ExactSpelling = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "StartDocPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In, MarshalAs(UnmanagedType.LPStruct)] DOCINFOA di);

    [DllImport("winspool.Drv", EntryPoint = "EndDocPrinter", SetLastError = true, ExactSpelling = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "StartPagePrinter", SetLastError = true, ExactSpelling = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "EndPagePrinter", SetLastError = true, ExactSpelling = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "WritePrinter", SetLastError = true, ExactSpelling = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    public static bool SendRawToPrinter(string szPrinterName, byte[] data, string docName)
    {
        IntPtr hPrinter;
        var di = new DOCINFOA { pDocName = docName, pDataType = "RAW" };
        if (!OpenPrinter(szPrinterName.Normalize(), out hPrinter, IntPtr.Zero)) return false;
        try
        {
            if (!StartDocPrinter(hPrinter, 1, di)) return false;
            try
            {
                if (!StartPagePrinter(hPrinter)) return false;
                var pUnmanagedBytes = Marshal.AllocCoTaskMem(data.Length);
                try
                {
                    Marshal.Copy(data, 0, pUnmanagedBytes, data.Length);
                    bool success = WritePrinter(hPrinter, pUnmanagedBytes, data.Length, out _);
                    EndPagePrinter(hPrinter);
                    return success;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pUnmanagedBytes);
                }
            }
            finally { EndDocPrinter(hPrinter); }
        }
        finally { ClosePrinter(hPrinter); }
    }
}

/// <summary>
/// Fallback printer that renders the receipt via System.Drawing.Printing
/// PrintDocument. Works on any Windows printer (laser, inkjet, PDF).
/// </summary>
public class WindowsPrinter : IReceiptPrinter
{
    private readonly ISettingsService _settings;
    public WindowsPrinter(ISettingsService settings) => _settings = settings;

    public bool IsConnected => PrinterSettings.InstalledPrinters.Count > 0;

    public async Task<bool> PrintAsync(Sale sale)
    {
        var store = await _settings.GetStoreSettingsAsync();
        return await Task.Run(() =>
        {
            try
            {
                var pd = new PrintDocument
                {
                    PrinterSettings = { PrinterName = string.IsNullOrEmpty(store.ReceiptPrinterName)
                        ? new PrinterSettings().PrinterName
                        : store.ReceiptPrinterName }
                };
                var lines = BuildPlainLines(sale, store);
                pd.PrintPage += (_, e) =>
                {
                    var font = new Font("Consolas", 8);
                    float y = 20;
                    foreach (var line in lines)
                    {
                        e.Graphics!.DrawString(line, font, Brushes.Black, 20, y);
                        y += font.GetHeight(e.Graphics) + 2;
                    }
                    e.HasMorePages = false;
                };
                pd.Print();
                return true;
            }
            catch { return false; }
        });
    }

    public async Task<bool> PrintTextAsync(string text)
    {
        var store = await _settings.GetStoreSettingsAsync();
        return await Task.Run(() =>
        {
            try
            {
                var pd = new PrintDocument
                {
                    PrinterSettings = { PrinterName = string.IsNullOrEmpty(store.ReceiptPrinterName)
                        ? new PrinterSettings().PrinterName
                        : store.ReceiptPrinterName }
                };
                var lines = text.Split('\n');
                pd.PrintPage += (_, e) =>
                {
                    var font = new Font("Consolas", 9);
                    float y = 20;
                    foreach (var line in lines)
                    {
                        e.Graphics!.DrawString(line, font, Brushes.Black, 20, y);
                        y += font.GetHeight(e.Graphics) + 2;
                    }
                    e.HasMorePages = false;
                };
                pd.Print();
                return true;
            }
            catch { return false; }
        });
    }

    private static List<string> BuildPlainLines(Sale sale, StoreSettings store)
    {
        var lines = new List<string>
        {
            store.StoreName,
            store.Address ?? "",
            string.IsNullOrEmpty(store.Phone) ? "" : $"Tel: {store.Phone}",
            new string('-', 40),
            $"Receipt: {sale.ReceiptNumber}",
            $"Date: {DateTimeUtilities.ToLocal(sale.SaleDate):yyyy-MM-dd HH:mm}",
            new string('-', 40)
        };
        foreach (var item in sale.Items)
        {
            lines.Add($"{item.QuantityDisplay} x {item.ProductName}");
            lines.Add($"      {FormattingUtilities.Money(item.LineTotal + item.LineTax, store)}");
        }
        lines.Add(new string('-', 40));
        lines.Add($"Subtotal:    {FormattingUtilities.Money(sale.Subtotal, store)}");
        if (sale.DiscountTotal != 0m) lines.Add($"Discount:    {FormattingUtilities.Money(-sale.DiscountTotal, store)}");
        if (sale.TaxTotal != 0m) lines.Add($"Tax:         {FormattingUtilities.Money(sale.TaxTotal, store)}");
        lines.Add($"TOTAL:       {FormattingUtilities.Money(sale.Total, store)}");
        foreach (var payment in ReceiptPaymentSummary.Build(sale))
            lines.Add($"{payment.Label}: {FormattingUtilities.Money(payment.Amount, store)}");
        if (sale.Change > 0) lines.Add($"Change: {FormattingUtilities.Money(sale.Change, store)}");
        lines.Add("");
        lines.Add(store.FooterNote ?? "");
        return lines;
    }
}
