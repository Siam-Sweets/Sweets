using System.Globalization;
using System.IO.Ports;
using PosApp.Core.Interfaces;

namespace PosApp.Hardware.Devices;

/// <summary>
/// Serial weighing scale driver. Talks to common POS scales (CAS, Bizerba,
/// Teraoka) using the simple command/response protocol: send 'R' to read,
/// receive a fixed-width response like "S+0001.235kg".
/// Override <see cref="ParseWeight"/> to adapt to a scale's protocol.
/// </summary>
public class SerialWeighingScale : IWeighingScale, IDisposable
{
    private readonly string _portName;
    private readonly object _sync = new();
    private SerialPort? _port;

    public SerialWeighingScale(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
            throw new ArgumentException("A serial port name is required.", nameof(portName));

        _portName = portName.Trim();
    }

    public bool IsConnected
    {
        get
        {
            lock (_sync) return _port?.IsOpen == true;
        }
    }

    public Task<bool> ConnectAsync()
    {
        return Task.Run(() =>
        {
            lock (_sync)
            {
                if (_port?.IsOpen == true) return true;
                try
                {
                    OpenLocked();
                    return _port?.IsOpen == true;
                }
                catch
                {
                    CloseLocked();
                    return false;
                }
            }
        });
    }

    public Task<decimal?> ReadWeightAsync()
    {
        return Task.Run(() =>
        {
            lock (_sync)
            {
                try
                {
                    if (_port?.IsOpen != true) OpenLocked();
                    if (_port?.IsOpen != true) return null;

                    // Clear stale bytes, request the current reading, then allow
                    // the scale enough time to return a complete serial frame.
                    _port.DiscardInBuffer();
                    _port.Write("R\r");
                    Thread.Sleep(200);
                    var raw = _port.ReadExisting();
                    return ParseWeight(raw);
                }
                catch
                {
                    CloseLocked();
                    return null;
                }
            }
        });
    }

    public Task<bool> ZeroAsync()
    {
        return Task.Run(() =>
        {
            lock (_sync)
            {
                try
                {
                    if (_port?.IsOpen != true) OpenLocked();
                    if (_port?.IsOpen != true) return false;
                    _port.Write("Z\r");
                    Thread.Sleep(100);
                    return true;
                }
                catch
                {
                    CloseLocked();
                    return false;
                }
            }
        });
    }

    private void OpenLocked()
    {
        CloseLocked();
        var port = new SerialPort(_portName, 9600, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = 700,
            WriteTimeout = 700,
            NewLine = "\r",
            DtrEnable = true,
            RtsEnable = true
        };

        try
        {
            port.Open();
            _port = port;
        }
        catch
        {
            port.Dispose();
            throw;
        }
    }

    private void CloseLocked()
    {
        if (_port == null) return;
        try
        {
            if (_port.IsOpen) _port.Close();
        }
        catch
        {
            // The device may have been disconnected while the port was open.
        }
        finally
        {
            _port.Dispose();
            _port = null;
        }
    }

    /// <summary>
    /// Override-able parser. Default handles the "S+0001.235kg" format.
    /// </summary>
    protected virtual decimal? ParseWeight(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var numeric = new string(raw.Trim()
            .Where(c => char.IsDigit(c) || c == '.' || c == '-' || c == ',')
            .ToArray())
            .Replace(',', '.');

        return decimal.TryParse(numeric, NumberStyles.Number | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    public void Dispose()
    {
        lock (_sync) CloseLocked();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Keeps the serial scale synchronized with the port saved in application
/// settings. Changing the port takes effect immediately without restarting.
/// </summary>
public sealed class ConfigurableWeighingScale : IWeighingScale, IDisposable
{
    private readonly Func<Task<string?>> _portNameProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SerialWeighingScale? _activeScale;
    private string? _activePortName;
    private bool _disposed;

    public ConfigurableWeighingScale(Func<Task<string?>> portNameProvider)
        => _portNameProvider = portNameProvider ?? throw new ArgumentNullException(nameof(portNameProvider));

    public bool IsConnected => _activeScale?.IsConnected == true;

    public async Task<bool> ConnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var scale = await GetActiveScaleLockedAsync();
            return scale != null && await scale.ConnectAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<decimal?> ReadWeightAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var scale = await GetActiveScaleLockedAsync();
            return scale == null ? null : await scale.ReadWeightAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ZeroAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var scale = await GetActiveScaleLockedAsync();
            return scale != null && await scale.ZeroAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SerialWeighingScale?> GetActiveScaleLockedAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ConfigurableWeighingScale));
        var configuredPort = (await _portNameProvider())?.Trim();

        if (string.IsNullOrWhiteSpace(configuredPort))
        {
            ReplaceActiveScale(null);
            return null;
        }

        if (_activeScale == null ||
            !string.Equals(_activePortName, configuredPort, StringComparison.OrdinalIgnoreCase))
        {
            ReplaceActiveScale(configuredPort);
        }

        return _activeScale;
    }

    private void ReplaceActiveScale(string? portName)
    {
        _activeScale?.Dispose();
        _activeScale = null;
        _activePortName = null;

        if (string.IsNullOrWhiteSpace(portName)) return;
        _activePortName = portName;
        _activeScale = new SerialWeighingScale(portName);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _activeScale?.Dispose();
        _activeScale = null;
        _gate.Dispose();
    }
}

/// <summary>
/// Null scale - always returns no weight. Used when no scale is attached.
/// </summary>
public class NullWeighingScale : IWeighingScale
{
    public bool IsConnected => false;
    public Task<bool> ConnectAsync() => Task.FromResult(false);
    public Task<decimal?> ReadWeightAsync() => Task.FromResult<decimal?>(null);
    public Task<bool> ZeroAsync() => Task.FromResult(true);
}
