using System.Text.Json;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;

namespace PosApp.Services;

/// <summary>Process-wide selected store, persisted per Windows user/device.</summary>
public sealed class StoreContext : IStoreContext
{
    private readonly string _statePath;
    private readonly object _gate = new();
    private readonly string _credentialPath;
    private readonly AsyncLocal<int> _captureSuppression = new();
    private StoreSelection _selection;

    public StoreContext()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PosApp");
        Directory.CreateDirectory(folder);
        _statePath = Path.Combine(folder, "store-selection.json");
        _credentialPath = Path.Combine(folder, "cloud-credentials.dat");
        _selection = Load();
    }

    public int StoreId
    {
        get { lock (_gate) return Math.Max(1, _selection.StoreId); }
    }

    public string StoreSyncId
    {
        get { lock (_gate) return _selection.StoreSyncId ?? string.Empty; }
    }

    // Capture starts only after this Windows user has connected a cloud account.
    // Applying downloaded changes uses SuppressCloudCapture so remote rows are not
    // echoed back into the outbox.
    public bool IsCloudSyncEnabled => File.Exists(_credentialPath);
    public bool IsCloudCaptureSuppressed => _captureSuppression.Value > 0;

    public IDisposable SuppressCloudCapture()
    {
        _captureSuppression.Value++;
        return new CaptureScope(this);
    }

    public void SetCurrentStore(Store store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (store.Id <= 0) throw new InvalidOperationException("The store must be saved before it can be selected.");
        lock (_gate)
        {
            _selection = new StoreSelection { StoreId = store.Id, StoreSyncId = store.SyncId };
            var json = JsonSerializer.Serialize(_selection);
            var temp = _statePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _statePath, true);
        }
    }

    private StoreSelection Load()
    {
        try
        {
            if (!File.Exists(_statePath)) return new StoreSelection();
            return JsonSerializer.Deserialize<StoreSelection>(File.ReadAllText(_statePath))
                   ?? new StoreSelection();
        }
        catch
        {
            return new StoreSelection();
        }
    }


    private sealed class CaptureScope : IDisposable
    {
        private StoreContext? _owner;

        public CaptureScope(StoreContext owner) => _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner != null) owner._captureSuppression.Value = Math.Max(0, owner._captureSuppression.Value - 1);
        }
    }

    private sealed class StoreSelection
    {
        public int StoreId { get; set; } = 1;
        public string? StoreSyncId { get; set; }
    }
}
