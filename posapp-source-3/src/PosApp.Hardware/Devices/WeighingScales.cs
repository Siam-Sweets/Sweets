using System.IO.Ports;
using PosApp.Core.Interfaces;

namespace PosApp.Hardware.Devices;

/// <summary>
/// Serial weighing scale driver. Talks to common POS scales (CAS, Bizerba,
/// Teraoka) using the simple command/response protocol: send 'R' to read,
/// receive a fixed-width response like "S+0001.235kg".
/// Override <see cref="ParseWeight"/> to adapt to your scale's protocol.
/// </summary>
public class SerialWeighingScale : IWeighingScale, IDisposable
{
    private readonly string _portName;
    private SerialPort? _port;

    public SerialWeighingScale(string portName) => _portName = portName;

    public bool IsConnected => _port?.IsOpen == true;

    public Task<decimal?> ReadWeightAsync()
    {
        return Task.Run(() =>
        {
            if (_port == null || !_port.IsOpen)
            {
                try { Open(); }
                catch { return null; }
            }
            try
            {
                _port.WriteLine("R");
                Thread.Sleep(150);
                var raw = _port.ReadExisting();
                return ParseWeight(raw);
            }
            catch { return null; }
        });
    }

    public Task<bool> ZeroAsync()
    {
        return Task.Run(() =>
        {
            if (_port == null || !_port.IsOpen) return false;
            try { _port.WriteLine("Z"); Thread.Sleep(100); return true; }
            catch { return false; }
        });
    }

    private void Open()
    {
        _port = new SerialPort(_portName, 9600, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,
            WriteTimeout = 500
        };
        _port.Open();
    }

    /// <summary>
    /// Override-able parser. Default handles the "S+0001.235kg" format.
    /// </summary>
    protected virtual decimal? ParseWeight(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        // strip non-numeric except . and -
        var numeric = new string(s.Where(c => char.IsDigit(c) || c == '.' || c == '-').ToArray());
        return decimal.TryParse(numeric, out var v) ? v : (decimal?)null;
    }

    public void Dispose() => _port?.Dispose();
}

/// <summary>
/// Null scale - always returns no weight. Used when no scale is attached.
/// </summary>
public class NullWeighingScale : IWeighingScale
{
    public bool IsConnected => false;
    public Task<decimal?> ReadWeightAsync() => Task.FromResult<decimal?>(null);
    public Task<bool> ZeroAsync() => Task.FromResult(true);
}
