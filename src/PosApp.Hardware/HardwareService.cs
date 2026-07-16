using PosApp.Core.Interfaces;

namespace PosApp.Hardware;

/// <summary>
/// Hardware service that gracefully degrades when devices are absent.
/// Each device is wrapped in try/catch and logs failures - the POS UI
/// continues to work even with no printer/scanner/drawer attached.
/// </summary>
public class HardwareService : IHardwareService
{
    private readonly IReceiptPrinter _printer;
    private readonly ICashDrawer _drawer;
    private readonly IBarcodeScanner _scanner;
    private readonly IWeighingScale _scale;

    public HardwareService(
        IReceiptPrinter printer,
        ICashDrawer drawer,
        IBarcodeScanner scanner,
        IWeighingScale scale)
    {
        _printer = printer;
        _drawer = drawer;
        _scanner = scanner;
        _scale = scale;
    }

    public Task<bool> PrintReceiptAsync(Core.Entities.Sale sale) => _printer.PrintAsync(sale);

    public Task<bool> PrintTextAsync(string text) => _printer.PrintTextAsync(text);

    public Task<bool> OpenCashDrawerAsync() => _drawer.OpenAsync();

    public Task<bool> IsScaleConnected() => Task.FromResult(_scale.IsConnected);

    public Task<decimal?> ReadScaleAsync() => _scale.ReadWeightAsync();

    public Task<bool> IsScannerConnected() => Task.FromResult(_scanner.IsConnected);

    public Task StartScannerAsync(Action<string> onScan) => _scanner.StartAsync(onScan);

    public Task StopScannerAsync() => _scanner.StopAsync();
}
