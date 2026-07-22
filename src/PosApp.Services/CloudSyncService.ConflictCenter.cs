using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Models;
using PosApp.Data;

namespace PosApp.Services;

public sealed partial class CloudSyncService
{
    private static readonly HashSet<string> ImmutableLedgerEntities = new(StringComparer.Ordinal)
    {
        nameof(Sale), nameof(SaleItem), nameof(SalePayment), nameof(StockTransaction),
        nameof(PurchaseDocument), nameof(PurchaseItem), nameof(CashSession), nameof(CashMovement),
        nameof(StockTransfer), nameof(StockTransferItem)
    };

    public async Task<SyncCenterSnapshot> GetSyncCenterAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        var stores = await _db.Stores.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var states = await _db.SyncStates.AsNoTracking().ToListAsync(cancellationToken);
        var pending = await _db.SyncOutbox.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var conflicts = await _db.SyncConflicts.AsNoTracking()
            .Where(x => x.ResolvedAt == null)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        var runs = await _db.SyncRuns.AsNoTracking()
            .OrderByDescending(x => x.StartedAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        var storeNames = stores.ToDictionary(x => x.Id, x => x.Name);
        var latestPending = pending
            .GroupBy(x => new { x.StoreId, x.EntityType, x.EntitySyncId })
            .ToDictionary(x => x.Key, x => x.OrderByDescending(row => row.Id).First());

        var conflictRows = conflicts.Select(conflict =>
        {
            latestPending.TryGetValue(
                new { conflict.StoreId, conflict.EntityType, conflict.EntitySyncId }, out var newestLocal);
            return new SyncConflictRecord
            {
                Id = conflict.Id,
                StoreId = conflict.StoreId,
                StoreName = storeNames.GetValueOrDefault(conflict.StoreId, $"Store {conflict.StoreId}"),
                EntityType = conflict.EntityType,
                EntitySyncId = conflict.EntitySyncId,
                ChangeId = conflict.ChangeId,
                LocalBaseCloudVersion = conflict.LocalBaseCloudVersion,
                RemoteCloudVersion = conflict.RemoteCloudVersion,
                LocalOperation = NormalizeRemoteOperation(newestLocal?.Operation ?? conflict.LocalOperation),
                RemoteOperation = NormalizeRemoteOperation(conflict.RemoteOperation),
                LocalPayloadJson = newestLocal?.PayloadJson ?? conflict.LocalPayloadJson,
                RemotePayloadJson = conflict.RemotePayloadJson,
                Message = conflict.Message,
                CreatedAt = ToOffset(conflict.CreatedAt),
                AllowsFieldMerge = !ImmutableLedgerEntities.Contains(conflict.EntityType) &&
                                   NormalizeRemoteOperation(newestLocal?.Operation ?? conflict.LocalOperation) == "upsert" &&
                                   NormalizeRemoteOperation(conflict.RemoteOperation) == "upsert"
            };
        }).ToList();

        var storeDiagnostics = stores.Select(store =>
        {
            var state = states.FirstOrDefault(x => x.StoreId == store.Id);
            var storePending = pending.Where(x => x.StoreId == store.Id).ToList();
            return new SyncStoreDiagnostic
            {
                StoreId = store.Id,
                StoreName = store.Name,
                PullCursor = state?.PullCursor ?? 0,
                PendingChanges = storePending.Count,
                FailedChanges = storePending.Count(x => !string.IsNullOrWhiteSpace(x.LastError)),
                ConflictCount = conflicts.Count(x => x.StoreId == store.Id),
                LastSuccessfulSyncAt = state?.LastSuccessfulSyncAt is DateTime last
                    ? ToOffset(last)
                    : null,
                LastError = state?.LastError ?? string.Empty
            };
        }).ToList();

        var queueIssues = pending
            .Where(x => !string.IsNullOrWhiteSpace(x.LastError))
            .OrderByDescending(x => x.LastAttemptAt ?? x.CreatedAt)
            .Take(100)
            .Select(x => new SyncQueueIssue
            {
                Id = x.Id,
                StoreName = storeNames.GetValueOrDefault(x.StoreId, $"Store {x.StoreId}"),
                EntityType = x.EntityType,
                EntitySyncId = x.EntitySyncId,
                AttemptCount = x.AttemptCount,
                LastAttemptAt = x.LastAttemptAt is DateTime attempted ? ToOffset(attempted) : null,
                LastError = x.LastError ?? string.Empty
            }).ToList();

        var runRows = runs.Select(x => new SyncRunRecord
        {
            Id = x.Id,
            StartedAt = ToOffset(x.StartedAt),
            CompletedAt = x.CompletedAt is DateTime completed ? ToOffset(completed) : null,
            Status = x.Status,
            StoreCount = x.StoreCount,
            PushedChanges = x.PushedChanges,
            PulledChanges = x.PulledChanges,
            ConflictCount = x.ConflictCount,
            PendingAfter = x.PendingAfter,
            Error = x.Error ?? string.Empty
        }).ToList();

        var devices = await TryGetDevicesAsync(cancellationToken);
        return new SyncCenterSnapshot
        {
            Status = status,
            Conflicts = conflictRows,
            Stores = storeDiagnostics,
            Runs = runRows,
            QueueIssues = queueIssues,
            Devices = devices
        };
    }

    public async Task ResolveConflictAsync(
        SyncConflictResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        await SyncGate.WaitAsync(cancellationToken);
        try
        {
            var conflict = await _db.SyncConflicts.FirstOrDefaultAsync(
                x => x.Id == request.ConflictId && x.ResolvedAt == null, cancellationToken)
                ?? throw new InvalidOperationException("The selected conflict has already been resolved or no longer exists.");
            var store = await _db.Stores.FirstOrDefaultAsync(x => x.Id == conflict.StoreId, cancellationToken)
                        ?? throw new InvalidOperationException("The conflict store no longer exists.");
            var pending = await _db.SyncOutbox
                .Where(x => x.StoreId == conflict.StoreId && x.EntityType == conflict.EntityType &&
                            x.EntitySyncId == conflict.EntitySyncId)
                .OrderBy(x => x.Id)
                .ToListAsync(cancellationToken);

            var latestLocal = pending.LastOrDefault();
            var localOperation = NormalizeRemoteOperation(latestLocal?.Operation ?? conflict.LocalOperation);
            var localPayload = latestLocal?.PayloadJson ?? conflict.LocalPayloadJson;
            var remoteOperation = NormalizeRemoteOperation(conflict.RemoteOperation);
            var chosenOperation = request.Mode == SyncConflictResolutionMode.UseCloud
                ? remoteOperation
                : request.Mode == SyncConflictResolutionMode.Merge ? "upsert" : localOperation;
            var chosenPayload = request.Mode switch
            {
                SyncConflictResolutionMode.UseCloud => conflict.RemotePayloadJson,
                SyncConflictResolutionMode.Merge => request.MergedPayloadJson ?? string.Empty,
                _ => localPayload
            };

            if (request.Mode == SyncConflictResolutionMode.Merge)
            {
                if (ImmutableLedgerEntities.Contains(conflict.EntityType))
                    throw new InvalidOperationException("Ledger records cannot be field-merged. Choose the complete local or cloud record.");
                if (localOperation != "upsert" || remoteOperation != "upsert")
                    throw new InvalidOperationException("A deleted record cannot be field-merged.");
            }

            var payload = ParsePayload(chosenPayload, chosenOperation);
            var synthetic = new PullChange
            {
                EntityType = conflict.EntityType,
                EntitySyncId = conflict.EntitySyncId,
                CloudVersion = conflict.RemoteCloudVersion,
                EntityVersion = Math.Max(1, latestLocal?.EntityVersion ?? 1),
                Operation = chosenOperation,
                Payload = payload
            };

            using (_storeContext.SuppressCloudCapture())
            {
                if (chosenOperation == "delete")
                    await DeleteRemoteEntityAsync(store.Id, synthetic, cancellationToken);
                else
                    await UpsertRemoteEntityAsync(store.Id, synthetic, cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
            }

            _db.SyncOutbox.RemoveRange(pending);

            string? resolvedPayload = chosenOperation == "upsert" ? chosenPayload : "{}";
            if (request.Mode != SyncConflictResolutionMode.UseCloud)
            {
                var entityVersion = Math.Max(1, pending.Select(x => x.EntityVersion).DefaultIfEmpty(1).Max() + 1);
                if (chosenOperation == "upsert")
                {
                    resolvedPayload = await RefreshLocalSyncPayloadAsync(
                        store.Id, conflict.EntityType, conflict.EntitySyncId,
                        conflict.RemoteCloudVersion, entityVersion, cancellationToken);
                }

                _db.SyncOutbox.Add(new SyncOutboxItem
                {
                    StoreId = store.Id,
                    ChangeId = Guid.NewGuid().ToString("N"),
                    EntityType = conflict.EntityType,
                    EntitySyncId = conflict.EntitySyncId,
                    Operation = chosenOperation,
                    EntityVersion = entityVersion,
                    BaseCloudVersion = conflict.RemoteCloudVersion,
                    PayloadJson = resolvedPayload ?? "{}"
                });
            }

            var related = await _db.SyncConflicts
                .Where(x => x.StoreId == conflict.StoreId && x.EntityType == conflict.EntityType &&
                            x.EntitySyncId == conflict.EntitySyncId && x.ResolvedAt == null)
                .ToListAsync(cancellationToken);
            var now = DateTime.UtcNow;
            var resolution = request.Mode switch
            {
                SyncConflictResolutionMode.UseCloud => "cloud",
                SyncConflictResolutionMode.Merge => "merged",
                _ => "local"
            };
            foreach (var row in related)
            {
                row.ResolvedAt = now;
                row.Resolution = resolution;
                row.ResolvedPayloadJson = resolvedPayload;
            }
            await _db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            SyncGate.Release();
        }
    }

    public async Task<int> RetryFailedChangesAsync(CancellationToken cancellationToken = default)
    {
        var failed = await _db.SyncOutbox
            .Where(x => x.LastError != null && !x.LastError.StartsWith("Conflict:"))
            .ToListAsync(cancellationToken);
        foreach (var item in failed)
        {
            item.AttemptCount = 0;
            item.LastAttemptAt = null;
            item.LastError = null;
        }
        await _db.SaveChangesAsync(cancellationToken);
        return failed.Count;
    }

    public async Task<int> ClearResolvedConflictsAsync(CancellationToken cancellationToken = default)
    {
        return await _db.SyncConflicts
            .Where(x => x.ResolvedAt != null)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<string> RefreshLocalSyncPayloadAsync(
        int storeId,
        string entityType,
        string entitySyncId,
        long cloudVersion,
        long minimumEntityVersion,
        CancellationToken cancellationToken)
    {
        using (_storeContext.SuppressCloudCapture())
        {
            if (entityType == nameof(Store))
            {
                var store = await _db.Stores.FirstOrDefaultAsync(x => x.SyncId == entitySyncId, cancellationToken)
                            ?? throw new InvalidOperationException("The local store record no longer exists.");
                store.CloudVersion = cloudVersion;
                store.SyncVersion = Math.Max(minimumEntityVersion, store.SyncVersion + 1);
                store.SyncUpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
                return SyncPayloadSerializer.Serialize(store);
            }

            if (!EntityTypes.TryGetValue(entityType, out var type))
                throw new InvalidOperationException($"Unsupported cloud entity type: {entityType}.");
            var entity = await FindEntityAsync(type, storeId, entitySyncId, cancellationToken) as StoreScopedEntity
                         ?? throw new InvalidOperationException("The local record no longer exists.");
            entity.CloudVersion = cloudVersion;
            entity.SyncVersion = Math.Max(minimumEntityVersion, entity.SyncVersion + 1);
            entity.SyncUpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return SyncPayloadSerializer.SerializeForSync(entity, _db);
        }
    }

    private async Task<SyncRun> StartSyncRunAsync(string deviceId, CancellationToken cancellationToken)
    {
        var run = new SyncRun { DeviceId = deviceId, StartedAt = DateTime.UtcNow, Status = "running" };
        _db.SyncRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken);
        return run;
    }

    private async Task CompleteSyncRunAsync(
        SyncRun? run,
        int storeCount,
        int pushed,
        int pulled,
        int conflicts,
        int pendingAfter,
        CancellationToken cancellationToken)
    {
        if (run == null) return;
        run.CompletedAt = DateTime.UtcNow;
        run.Status = "completed";
        run.StoreCount = storeCount;
        run.PushedChanges = pushed;
        run.PulledChanges = pulled;
        run.ConflictCount = conflicts;
        run.PendingAfter = pendingAfter;
        run.Error = null;
        await _db.SaveChangesAsync(cancellationToken);
        await TrimSyncRunsAsync(cancellationToken);
    }

    private async Task FailSyncRunAsync(
        SyncRun? run,
        int storeCount,
        int pushed,
        int pulled,
        Exception error,
        CancellationToken cancellationToken)
    {
        if (run == null) return;
        try
        {
            run.CompletedAt = DateTime.UtcNow;
            run.Status = "failed";
            run.StoreCount = storeCount;
            run.PushedChanges = pushed;
            run.PulledChanges = pulled;
            run.ConflictCount = await _db.SyncConflicts.CountAsync(x => x.ResolvedAt == null, CancellationToken.None);
            run.PendingAfter = await _db.SyncOutbox.CountAsync(CancellationToken.None);
            run.Error = Limit(error.GetBaseException().Message, 1000);
            await _db.SaveChangesAsync(CancellationToken.None);
            await TrimSyncRunsAsync(CancellationToken.None);
        }
        catch
        {
            // Diagnostics must never replace the original synchronization error.
        }
    }

    private async Task TrimSyncRunsAsync(CancellationToken cancellationToken)
    {
        var keepIds = await _db.SyncRuns.AsNoTracking()
            .OrderByDescending(x => x.StartedAt)
            .Take(100)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        if (keepIds.Count < 100) return;
        await _db.SyncRuns.Where(x => !keepIds.Contains(x.Id)).ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<CloudDeviceRecord>> TryGetDevicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var credential = await _credentials.LoadAsync(cancellationToken);
            if (credential == null) return Array.Empty<CloudDeviceRecord>();
            credential = await EnsureFreshAccessTokenAsync(credential, cancellationToken);
            var response = await GetAsync<CloudDeviceListResponse>(credential, "/v1/devices", cancellationToken);
            return response.Devices;
        }
        catch
        {
            return Array.Empty<CloudDeviceRecord>();
        }
    }

    private static JsonElement ParsePayload(string payloadJson, string operation)
    {
        if (operation == "delete") return JsonSerializer.Deserialize<JsonElement>("{}", JsonOptions);
        if (string.IsNullOrWhiteSpace(payloadJson))
            throw new InvalidOperationException("The selected conflict payload is empty.");
        try
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson, JsonOptions);
            if (payload.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("A conflict payload must be a JSON object.");
            return payload.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The selected conflict payload is invalid.", ex);
        }
    }

    private static string NormalizeRemoteOperation(string? operation)
        => string.Equals(operation, "upsert", StringComparison.OrdinalIgnoreCase) ? "upsert" : "delete";

    private static DateTimeOffset ToOffset(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed class CloudDeviceListResponse
    {
        public List<CloudDeviceRecord> Devices { get; set; } = new();
    }
}
