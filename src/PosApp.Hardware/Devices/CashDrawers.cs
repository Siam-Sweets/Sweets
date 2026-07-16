using System.IO.Ports;
using PosApp.Core.Interfaces;

namespace PosApp.Hardware.Devices;

/// <summary>
/// Cash drawer driver. Most POS cash drawers are connected either:
/// 1. To the receipt printer's DK port (kick-out triggered by ESC/POS command)
/// 2. Directly to a COM port (open via DTR/RTS pulse)
/// This implementation handles case 2. Case 1 is handled by sending the
/// drawer-kick ESC/POS command through the printer.
/// </summary>
public class SerialCashDrawer : ICashDrawer
{
    private readonly ISettingsService _settings;
    public SerialCashDrawer(ISettingsService settings) => _settings = settings;

    public bool IsConnected
    {
        get
        {
            var port = _settings.GetStoreSettingsAsync().GetAwaiter().GetResult().CashDrawerPort;
            return !string.IsNullOrEmpty(port);
        }
    }

    public Task<bool> OpenAsync()
    {
        return Task.Run(() =>
        {
            var portName = _settings.GetStoreSettingsAsync().GetAwaiter().GetResult().CashDrawerPort;
            if (string.IsNullOrEmpty(portName)) return false;
            try
            {
                using var port = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One);
                port.Open();
                // Pulse DTR to trigger the drawer solenoid
                port.DtrEnable = true;
                Thread.Sleep(100);
                port.DtrEnable = false;
                port.Close();
                return true;
            }
            catch { return false; }
        });
    }
}

/// <summary>
/// Drawer that fires via the receipt printer's DK port using ESC/POS
/// command (ESC p m t1 t2). Use this when the drawer is wired to the
/// printer rather than a serial port.
/// </summary>
public class PrinterCashDrawer : ICashDrawer
{
    private readonly IReceiptPrinter _printer;
    public PrinterCashDrawer(IReceiptPrinter printer) => _printer = printer;

    public bool IsConnected => _printer.IsConnected;

    public Task<bool> OpenAsync()
    {
        // ESC p m t1 t2 - pulse pin 2 for 200ms then 200ms off
        return _printer.PrintTextAsync("\x1B\x70\x00\xC8\xC8");
    }
}

/// <summary>
/// No-op drawer used on systems without a drawer attached. Always
/// returns true so POS flow doesn't block.
/// </summary>
public class NullCashDrawer : ICashDrawer
{
    public bool IsConnected => false;
    public Task<bool> OpenAsync() => Task.FromResult(true);
}
