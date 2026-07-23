using PosApp.Core.Models;

namespace PosApp.Core.Interfaces;

public interface ICloudSyncService
{
    Task<CloudSyncStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<CloudSyncSummary> SyncNowAsync(CancellationToken cancellationToken = default);
    Task<CloudRestoreSummary> RestoreLatestSnapshotsAsync(
        bool replaceLocalData,
        CancellationToken cancellationToken = default);
    Task<SyncCenterSnapshot> GetSyncCenterAsync(CancellationToken cancellationToken = default);
    Task ResolveConflictAsync(
        SyncConflictResolutionRequest request,
        CancellationToken cancellationToken = default);
    Task<int> RetryFailedChangesAsync(CancellationToken cancellationToken = default);
    Task<int> ClearResolvedConflictsAsync(CancellationToken cancellationToken = default);
}

public interface ICloudSyncCoordinator : IDisposable
{
    void Start();
    void Trigger();
}
