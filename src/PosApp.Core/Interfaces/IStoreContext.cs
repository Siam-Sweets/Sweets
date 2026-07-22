using PosApp.Core.Entities;

namespace PosApp.Core.Interfaces;

public interface IStoreContext
{
    int StoreId { get; }
    string StoreSyncId { get; }
    bool IsCloudSyncEnabled { get; }
    bool IsCloudCaptureSuppressed { get; }
    IDisposable SuppressCloudCapture();
    void SetCurrentStore(Store store);
}

public interface IStoreService
{
    Task InitializeAsync();
    Task<Store> GetCurrentStoreAsync();
    Task<IReadOnlyList<Store>> GetStoresAsync(bool includeInactive = true);
    Task<Store> SaveStoreAsync(Store store);
    Task SetStoreActiveAsync(int storeId, bool isActive);
    Task SelectStoreAsync(int storeId);
}
