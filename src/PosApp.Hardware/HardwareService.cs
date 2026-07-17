using PosApp.Core.Interfaces;

namespace PosApp.Hardware;

/// <summary>
/// Hardware service that gracefully degrades when devices are absent.
/// Each device is wrapped in try/catch and logs failures - the POS UI
/// continues to work even with no printer or scanner attached.
/// </summary>
public class HardwareService : IHardwareService
{
    private readonly IReceiptPrinter _printer;
    private readonly IBarcodeScanner _scanner;

    public HardwareService(
        IReceiptPrinter printer,
        IBarcodeScanner scanner)
    {
        _printer = printer;
        _scanner = scanner;
    }

    public Task<bool> PrintReceiptAsync(Core.Entities.Sale sale) => _printer.PrintAsync(sale);

    public Task<bool> PrintTextAsync(string text) => _printer.PrintTextAsync(text);


    public Task<bool> IsScannerConnected() => Task.FromResult(_scanner.IsConnected);

    public Task StartScannerAsync(Action<string> onScan) => _scanner.StartAsync(onScan);

    public Task StopScannerAsync() => _scanner.StopAsync();
}
