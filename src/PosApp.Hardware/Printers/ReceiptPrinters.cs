using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Text;
using PosApp.Core.Entities;
using PosApp.Core.Models;
using PosApp.Core.Interfaces;

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

    public bool IsConnected => !string.IsNullOrEmpty(GetPrinterName());

    private string GetPrinterName()
    {
        var s = _settings.GetStoreSettingsAsync().GetAwaiter().GetResult();
        if (!string.IsNullOrEmpty(s.ReceiptPrinterName)) return s.ReceiptPrinterName;
        // Fall back to the OS default printer.
        return PrinterSettings.InstalledPrinters.Count > 0
            ? new PrinterSettings().PrinterName
            : string.Empty;
    }

    public Task<bool> PrintAsync(Sale sale)
    {
        var store = _settings.GetStoreSettingsAsync().GetAwaiter().GetResult();
        var doc = BuildReceipt(sale, store);
        return PrintTextAsync(doc);
    }

    public Task<bool> PrintTextAsync(string text)
    {
        return Task.Run(() =>
        {
            var printerName = GetPrinterName();
            if (string.IsNullOrEmpty(printerName)) return false;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                return RawPrinterHelper.SendRawToPrinter(printerName, bytes, "POS Receipt");
            }
            catch
            {
                return false;
            }
        });
    }

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
        sb.AppendLine($"Date: {sale.SaleDate:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Cashier: {sale.User?.FullName ?? sale.UserId.ToString()}");
        sb.AppendLine(new string('-', 32));

        foreach (var item in sale.Items)
        {
            var qty = item.Quantity.ToString("0.###");
            var line = $"{qty} x {item.ProductName}";
            sb.AppendLine(line);
            sb.Append(EscPosConst.AlignRight);
            sb.AppendLine($"{item.LineTotal + item.LineTax:0.00}");
            sb.Append(EscPosConst.AlignLeft);
        }

        sb.AppendLine(new string('-', 32));
        sb.Append(EscPosConst.AlignRight);
        sb.AppendLine($"Subtotal:    {sale.Subtotal:0.00}");
        if (sale.DiscountTotal > 0) sb.AppendLine($"Discount:    -{sale.DiscountTotal:0.00}");
        if (sale.TaxTotal > 0) sb.AppendLine($"Tax:         {sale.TaxTotal:0.00}");
        sb.Append(EscPosConst.DoubleOn);
        sb.AppendLine($"TOTAL:       {sale.Total:0.00}");
        sb.Append(EscPosConst.DoubleOff);
        sb.AppendLine();

        var payments = sale.Payments.ToList();
        foreach (var pay in payments)
        {
            var isSingleCashPayment = payments.Count == 1 && pay.Method == PaymentMethod.Cash;
            var displayedAmount = isSingleCashPayment && sale.AmountPaid > 0m
                ? sale.AmountPaid
                : pay.Amount;
            var label = isSingleCashPayment ? "Cash tendered" : pay.Method.ToString();
            sb.AppendLine($"{label}: {displayedAmount:0.00}");
        }
        if (sale.Change > 0) sb.AppendLine($"Change: {sale.Change:0.00}");

        sb.Append(EscPosConst.AlignCenter);
        sb.AppendLine();
        sb.AppendLine(store.FooterNote);
        sb.AppendLine();
        sb.Append(EscPosConst.Cut);
        return sb.ToString();
    }
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

    public Task<bool> PrintAsync(Sale sale)
    {
        var store = _settings.GetStoreSettingsAsync().GetAwaiter().GetResult();
        return Task.Run(() =>
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

    public Task<bool> PrintTextAsync(string text)
    {
        var store = _settings.GetStoreSettingsAsync().GetAwaiter().GetResult();
        return Task.Run(() =>
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
            $"Date: {sale.SaleDate:yyyy-MM-dd HH:mm}",
            new string('-', 40)
        };
        foreach (var item in sale.Items)
        {
            lines.Add($"{item.Quantity:0.###} x {item.ProductName}");
            lines.Add($"      {(item.LineTotal + item.LineTax):0.00}");
        }
        lines.Add(new string('-', 40));
        lines.Add($"Subtotal:    {sale.Subtotal:0.00}");
        if (sale.DiscountTotal > 0) lines.Add($"Discount:    -{sale.DiscountTotal:0.00}");
        if (sale.TaxTotal > 0) lines.Add($"Tax:         {sale.TaxTotal:0.00}");
        lines.Add($"TOTAL:       {sale.Total:0.00}");
        var payments = sale.Payments.ToList();
        foreach (var pay in payments)
        {
            var isSingleCashPayment = payments.Count == 1 && pay.Method == PaymentMethod.Cash;
            var displayedAmount = isSingleCashPayment && sale.AmountPaid > 0m
                ? sale.AmountPaid
                : pay.Amount;
            var label = isSingleCashPayment ? "Cash tendered" : pay.Method.ToString();
            lines.Add($"{label}: {displayedAmount:0.00}");
        }
        if (sale.Change > 0) lines.Add($"Change: {sale.Change:0.00}");
        lines.Add("");
        lines.Add(store.FooterNote ?? "");
        return lines;
    }
}
