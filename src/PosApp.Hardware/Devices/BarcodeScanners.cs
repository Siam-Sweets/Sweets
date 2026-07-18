using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
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
    private Action<string>? _onScan;
    private readonly StringBuilder _buffer = new();
    private DateTime _lastKey = DateTime.MinValue;
    private const int InterKeyTimeoutMs = 80; // scanners type fast

    /// <summary>True after <see cref="StartAsync"/> until <see cref="StopAsync"/>.</summary>
    public bool IsConnected { get; private set; }

    public Task StartAsync(Action<string> onScan)
    {
        ArgumentNullException.ThrowIfNull(onScan);
        _onScan = onScan;
        if (IsConnected) return Task.CompletedTask;

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
        _onScan = null;
        _lastKey = DateTime.MinValue;
        return Task.CompletedTask;
    }

    private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is KeyEventArgs keyArgs && keyArgs.RoutedEvent == Keyboard.KeyDownEvent)
        {
            var key = keyArgs.Key;
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
                    var now = DateTime.UtcNow;
                    if (_buffer.Length > 0 &&
                        (now - _lastKey).TotalMilliseconds > InterKeyTimeoutMs)
                        _buffer.Clear();

                    _buffer.Append(ch.Value);
                    _lastKey = now;
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
        if (!IsConnected) return;
        InputManager.Current.PreProcessInput -= OnPreProcessInput;
        IsConnected = false;
        _buffer.Clear();
        _onScan = null;
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
    private SynchronizationContext? _callbackContext;

    public SerialBarcodeScanner(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
            throw new ArgumentException("A serial port name is required.", nameof(portName));
        _portName = portName.Trim();
    }

    public bool IsConnected => _port?.IsOpen == true;

    public Task StartAsync(Action<string> onScan)
    {
        ArgumentNullException.ThrowIfNull(onScan);
        _onScan = onScan;
        // SerialPort raises DataReceived on a worker thread. Remember the caller's
        // context so a scanner configured by WPF never mutates UI state cross-thread.
        _callbackContext = SynchronizationContext.Current;
        if (IsConnected) return Task.CompletedTask;

        StopPort();
        var port = new SerialPort(_portName, 9600, Parity.None, 8, StopBits.One) { NewLine = "\r" };
        port.DataReceived += OnDataReceived;
        try
        {
            port.Open();
            _port = port;
        }
        catch
        {
            port.DataReceived -= OnDataReceived;
            port.Dispose();
            _onScan = null;
            _callbackContext = null;
            throw;
        }
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        StopPort();
        _onScan = null;
        _callbackContext = null;
        return Task.CompletedTask;
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (sender is not SerialPort port) return;
        try
        {
            var line = port.ReadLine().Trim();
            var callback = _onScan;
            if (line.Length == 0 || callback == null) return;

            var context = _callbackContext;
            if (context == null)
            {
                callback(line);
            }
            else
            {
                context.Post(static state =>
                {
                    var (scanner, handler, code) =
                        ((SerialBarcodeScanner, Action<string>, string))state!;
                    // A read can already be queued when the scanner is stopped or
                    // restarted. Do not deliver that stale scan to the next screen.
                    if (ReferenceEquals(scanner._onScan, handler)) handler(code);
                }, (this, callback, line));
            }
        }
        catch (InvalidOperationException) { }
        catch (IOException) { }
        catch (TimeoutException) { }
    }

    private void StopPort()
    {
        var port = _port;
        _port = null;
        if (port == null) return;
        port.DataReceived -= OnDataReceived;
        try
        {
            if (port.IsOpen) port.Close();
        }
        catch (InvalidOperationException) { }
        catch (IOException) { }
        finally
        {
            port.Dispose();
        }
    }

    public void Dispose()
    {
        StopPort();
        _onScan = null;
        _callbackContext = null;
    }
}

/// <summary>
/// Null scanner - never fires. Used when no scanner is configured so
/// the POS UI can still be operated via on-screen search.
/// </summary>
public class NullBarcodeScanner : IBarcodeScanner
{
    public bool IsConnected => false;
    public Task StartAsync(Action<string> onScan)
    {
        ArgumentNullException.ThrowIfNull(onScan);
        return Task.CompletedTask;
    }
    public Task StopAsync() => Task.CompletedTask;
}
