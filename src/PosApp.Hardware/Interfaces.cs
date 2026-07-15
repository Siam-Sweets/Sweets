using PosApp.Core.Entities;

namespace PosApp.Hardware;

/// <summary>
/// Common interface for receipt printer implementations. Two backends are
/// shipped: an ESC/POS raw-printer backend (thermal printers) and a
/// Windows PrintDocument backend for regular A4/inkjet printers.
/// </summary>
public interface IReceiptPrinter
{
    bool IsConnected { get; }
    Task<bool> PrintAsync(Sale sale);
    Task<bool> PrintTextAsync(string text);
}

public interface ICashDrawer
{
    bool IsConnected { get; }
    Task<bool> OpenAsync();
}

/// <summary>
/// A barcode scanner that fires <c>onScan</c> when a code is read.
/// HID scanners (most USB POS scanners) appear as keyboards and need
/// no driver; this interface also supports serial scanners.
/// </summary>
public interface IBarcodeScanner
{
    bool IsConnected { get; }
    Task StartAsync(Action<string> onScan);
    Task StopAsync();
}

/// <summary>
/// Weighing scale for produce / bulk items. ReadWeightAsync returns
/// the current weight in kilograms, or null if not connected.
/// </summary>
public interface IWeighingScale
{
    bool IsConnected { get; }
    Task<decimal?> ReadWeightAsync();
    Task<bool> ZeroAsync();
}
