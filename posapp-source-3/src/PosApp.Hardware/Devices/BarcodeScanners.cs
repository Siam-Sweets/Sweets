using System.IO.Ports;
using System.Text;
using System.Windows.Input;
using PosApp.Core.Interfaces;

namespace PosApp.Hardware.Devices;

/// <summary>
/// HID barcode scanner driver. Most USB POS scanners (Honeywell, Zebra,
/// Symbol, generic) appear as HID keyboards and "type" the scanned code
/// followed by Enter. We hook the application-level keyboard events and
/// collect keystrokes into a buffer; when Enter arrives, we fire onScan
/// with the accumulated code.
/// </summary>
public class HidBarcodeScanner : IBarcodeScanner, IDisposable
{
    private readonly Action<string>? _externalOnScan;
    private Action<string>? _onScan;
    private readonly StringBuilder _buffer = new();
    private DateTime _lastKey = DateTime.MinValue;
    private const int InterKeyTimeoutMs = 80; // scanners type fast

    public HidBarcodeScanner() { }

    /// <summary>True after <see cref="StartAsync"/> until <see cref="StopAsync"/>.</summary>
    public bool IsConnected { get; private set; }

    public Task StartAsync(Action<string> onScan)
    {
        _onScan = onScan;
        IsConnected = true;
        // Subscribe at the WPF InputManager level so we get keys regardless of focus
        InputManager.Current.PreProcessInput += OnPreProcessInput;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        IsConnected = false;
        InputManager.Current.PreProcessInput -= OnPreProcessInput;
        _buffer.Clear();
        return Task.CompletedTask;
    }

    private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is KeyEventArgs keyArgs && keyArgs.RoutedEvent == Keyboard.KeyDownEvent)
        {
            var key = keyArgs.Key;
            // If buffer is empty and last key was long ago, this is likely a human
            if (_buffer.Length == 0 && (DateTime.UtcNow - _lastKey).TotalMilliseconds > 1500)
            {
                // Mark the start of a possible scan
            }

            if (key == Key.Enter)
            {
                if (_buffer.Length > 0)
                {
                    var code = _buffer.ToString();
                    _buffer.Clear();
                    _onScan?.Invoke(code);
                    keyArgs.Handled = true;
                }
            }
            else if (key == Key.Escape)
            {
                _buffer.Clear();
            }
            else
            {
                var ch = KeyToChar(key, Keyboard.Modifiers);
                if (ch.HasValue)
                {
                    _buffer.Append(ch.Value);
                    _lastKey = DateTime.UtcNow;
                }
            }
        }
    }

    private static char? KeyToChar(Key key, ModifierKeys mods)
    {
        if (key >= Key.D0 && key <= Key.D9)
            return (mods & ModifierKeys.Shift) == 0
                ? (char)('0' + (key - Key.D0))
                : ShiftDigit(key);
        if (key >= Key.NumPad0 && key <= Key.NumPad9)
            return (char)('0' + (key - Key.NumPad0));
        if (key >= Key.A && key <= Key.Z)
            return (mods & ModifierKeys.Shift) == 0
                ? char.ToLower((char)('A' + (key - Key.A)))
                : (char)('A' + (key - Key.A));
        return key switch
        {
            Key.OemMinus => '-',
            Key.OemPlus => '+',
            Key.OemPeriod => '.',
            Key.OemComma => ',',
            Key.OemQuestion => '/',
            Key.Space => ' ',
            _ => null
        };
    }

    private static char ShiftDigit(Key key) => key switch
    {
        Key.D0 => ')',
        Key.D1 => '!',
        Key.D2 => '@',
        Key.D3 => '#',
        Key.D4 => '$',
        Key.D5 => '%',
        Key.D6 => '^',
        Key.D7 => '&',
        Key.D8 => '*',
        Key.D9 => '(',
        _ => '?'
    };

    public void Dispose()
    {
        if (IsConnected) StopAsync().GetAwaiter().GetResult();
    }
}

/// <summary>
/// Serial-port barcode scanner (rare). Reads lines terminated by CR
/// from the configured COM port.
/// </summary>
public class SerialBarcodeScanner : IBarcodeScanner, IDisposable
{
    private readonly string _portName;
    private SerialPort? _port;
    private Action<string>? _onScan;

    public SerialBarcodeScanner(string portName) => _portName = portName;

    public bool IsConnected => _port?.IsOpen == true;

    public Task StartAsync(Action<string> onScan)
    {
        _onScan = onScan;
        _port = new SerialPort(_portName, 9600, Parity.None, 8, StopBits.One) { NewLine = "\r" };
        _port.DataReceived += (_, __) =>
        {
            try
            {
                var line = _port.ReadLine().Trim();
                if (!string.IsNullOrEmpty(line)) _onScan?.Invoke(line);
            }
            catch { /* ignore */ }
        };
        _port.Open();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (_port?.IsOpen == true) _port.Close();
        _port?.Dispose();
        _port = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _port?.Dispose();
    }
}

/// <summary>
/// Null scanner - never fires. Used when no scanner is configured so
/// the POS UI can still be operated via on-screen search.
/// </summary>
public class NullBarcodeScanner : IBarcodeScanner
{
    public bool IsConnected => false;
    public Task StartAsync(Action<string> onScan) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
}
