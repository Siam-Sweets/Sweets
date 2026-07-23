using System.Net.NetworkInformation;
using Microsoft.Extensions.DependencyInjection;
using PosApp.Core.Interfaces;
using PosApp.Data;

namespace PosApp.Services;

/// <summary>
/// Starts best-effort synchronization after startup, on network reconnection,
/// periodically, and whenever a local transaction commits outbox records.
/// Failures never block checkout and are exposed through SyncState/Settings.
/// </summary>
public sealed class CloudSyncCoordinator : ICloudSyncCoordinator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private Timer? _timer;
    private int _started;
    private int _disposed;

    public CloudSyncCoordinator(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        AppDbContext.CloudOutboxChanged += OnOutboxChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        _timer = new Timer(_ => Trigger(), null, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1));
    }

    public void Trigger()
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _started) == 0) return;
        _ = RunAsync();
    }

    private void OnOutboxChanged(object? sender, EventArgs e) => Trigger();

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) Trigger();
    }

    private async Task RunAsync()
    {
        if (!await _runGate.WaitAsync(0)) return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<ICloudSyncService>();
            var status = await sync.GetStatusAsync();
            if (!status.IsConnected) return;
            await sync.SyncNowAsync();
        }
        catch
        {
            // CloudSyncService persists the actionable error per store. Automatic
            // background failures are intentionally non-modal so sales continue.
        }
        finally
        {
            _runGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        AppDbContext.CloudOutboxChanged -= OnOutboxChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _timer?.Dispose();
        _runGate.Dispose();
    }
}
