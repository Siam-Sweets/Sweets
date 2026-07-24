using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Core.Utilities;
using PosApp.Data;

namespace PosApp.Services;

/// <summary>
/// Drains the local outbox, downloads cloud changes by cursor, retains revision
/// conflicts, and restores complete snapshots for a newly registered device.
/// Local SQLite remains authoritative for checkout while the network is absent.
/// </summary>
public sealed partial class CloudSyncService : ICloudSyncService
{
    private const int PushBatchSize = 1000;
    private const int PullBatchSize = 1000;
    private const int MaximumPushBatchesPerRun = 20;
    private static readonly SemaphoreSlim SyncGate = new(1, 1);
    private static int _isRunning;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly IReadOnlyDictionary<string, string> TableNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(Store)] = "Stores",
            [nameof(Category)] = "Categories",
            [nameof(Customer)] = "Customers",
            [nameof(Discount)] = "Discounts",
            [nameof(Product)] = "Products",
            [nameof(PurchaseDocument)] = "PurchaseDocuments",
            [nameof(PurchaseItem)] = "PurchaseItems",
            [nameof(Sale)] = "Sales",
            [nameof(SaleItem)] = "SaleItems",
            [nameof(SalePayment)] = "SalePayments",
            [nameof(StockTransaction)] = "StockTransactions",
            [nameof(StockTransfer)] = "StockTransfers",
            [nameof(StockTransferItem)] = "StockTransferItems",
            [nameof(Supplier)] = "Suppliers",
            [nameof(Tax)] = "Taxes",
            [nameof(User)] = "Users",
            [nameof(CashSession)] = "CashSessions",
            [nameof(CashMovement)] = "CashMovements",
            [nameof(Setting)] = "Settings"
        };

    private static readonly IReadOnlyDictionary<string, Type> EntityTypes =
        new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            [nameof(Category)] = typeof(Category),
            [nameof(Customer)] = typeof(Customer),
            [nameof(Discount)] = typeof(Discount),
            [nameof(Product)] = typeof(Product),
            [nameof(PurchaseDocument)] = typeof(PurchaseDocument),
            [nameof(PurchaseItem)] = typeof(PurchaseItem),
            [nameof(Sale)] = typeof(Sale),
            [nameof(SaleItem)] = typeof(SaleItem),
            [nameof(SalePayment)] = typeof(SalePayment),
            [nameof(StockTransaction)] = typeof(StockTransaction),
            [nameof(StockTransfer)] = typeof(StockTransfer),
            [nameof(StockTransferItem)] = typeof(StockTransferItem),
            [nameof(Supplier)] = typeof(Supplier),
            [nameof(Tax)] = typeof(Tax),
            [nameof(User)] = typeof(User),
            [nameof(CashSession)] = typeof(CashSession),
            [nameof(CashMovement)] = typeof(CashMovement),
            [nameof(Setting)] = typeof(Setting)
        };

    private static readonly string[] RestoreOrder =
    {
        nameof(User), nameof(Category), nameof(Customer), nameof(Supplier),
        nameof(Tax), nameof(Discount), nameof(Setting), nameof(Product),
        nameof(CashSession), nameof(PurchaseDocument), nameof(Sale), nameof(StockTransfer),
        nameof(PurchaseItem), nameof(SaleItem), nameof(SalePayment), nameof(StockTransferItem),
        nameof(CashMovement), nameof(StockTransaction)
    };

    private static readonly HashSet<string> ForeignKeyProperties = new(StringComparer.Ordinal)
    {
        nameof(Product.CategoryId),
        nameof(PurchaseDocument.SupplierId), nameof(PurchaseDocument.UserId),
        nameof(PurchaseItem.PurchaseDocumentId), nameof(PurchaseItem.ProductId),
        nameof(Sale.CustomerId), nameof(Sale.UserId), nameof(Sale.CashSessionId), nameof(Sale.RefundedSaleId),
        nameof(SaleItem.SaleId), nameof(SaleItem.ProductId), nameof(SaleItem.RefundedSaleItemId),
        nameof(SalePayment.SaleId),
        nameof(StockTransaction.ProductId), nameof(StockTransaction.SaleId),
        nameof(StockTransaction.SaleItemId), nameof(StockTransaction.StockTransferId),
        nameof(StockTransaction.StockTransferItemId), nameof(StockTransaction.UserId),
        nameof(StockTransfer.DestinationStoreId), nameof(StockTransfer.CreatedByUserId),
        nameof(StockTransfer.DispatchedByUserId), nameof(StockTransfer.ReceivedByUserId),
        nameof(StockTransfer.CancelledByUserId), nameof(StockTransferItem.StockTransferId),
        nameof(StockTransferItem.ProductId), nameof(StockTransferItem.DestinationProductId),
        nameof(CashSession.OpenedByUserId), nameof(CashSession.ClosedByUserId),
        nameof(CashMovement.CashSessionId), nameof(CashMovement.UserId)
    };

    private readonly AppDbContext _db;
    private readonly IStoreContext _storeContext;
    private readonly ICloudAccountService _account;
    private readonly IBackupService _backup;
    private readonly CloudCredentialStore _credentials = new();

    public CloudSyncService(
        AppDbContext db,
        IStoreContext storeContext,
        ICloudAccountService account,
        IBackupService backup)
    {
        _db = db;
        _storeContext = storeContext;
        _account = account;
        _backup = backup;
    }

    public async Task<CloudSyncStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var credential = await _credentials.LoadAsync(cancellationToken);
        var pending = await _db.SyncOutbox.CountAsync(cancellationToken);
        var conflicts = await _db.SyncConflicts.CountAsync(x => x.ResolvedAt == null, cancellationToken);
        var lastSuccess = await _db.SyncStates
            .Where(x => x.LastSuccessfulSyncAt != null)
            .MaxAsync(x => (DateTime?)x.LastSuccessfulSyncAt, cancellationToken);
        return new CloudSyncStatus
        {
            IsConnected = credential != null,
            IsRunning = Volatile.Read(ref _isRunning) != 0,
            PendingChanges = pending,
            ConflictCount = conflicts,
            LastSuccessfulSyncAt = lastSuccess.HasValue
                ? new DateTimeOffset(DateTime.SpecifyKind(lastSuccess.Value, DateTimeKind.Utc))
                : null,
            Message = credential == null
                ? "Cloud account is not connected."
                : conflicts > 0
                    ? "Synchronization is active, but one or more changes require review."
                    : pending > 0
                        ? "Local changes are waiting to synchronize."
                        : "Cloud synchronization is up to date."
        };
    }

    public async Task<CloudSyncSummary> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        await SyncGate.WaitAsync(cancellationToken);
        Interlocked.Exchange(ref _isRunning, 1);
        SyncRun? syncRun = null;
        var pushed = 0;
        var pulled = 0;
        var storeCount = 0;
        try
        {
            var credential = await RequireCredentialAsync(cancellationToken);
            syncRun = await StartSyncRunAsync(credential.DeviceId, cancellationToken);
            var cloudStores = await GetCloudStoresAsync(credential, cancellationToken);
            if (credential.InitialSnapshotUploadedAt == null)
            {
                if (cloudStores.Count > 0)
                {
                    throw new InvalidOperationException(
                        "Cloud stores already exist for this account. Restore cloud data on this device before enabling synchronization.");
                }

                await _account.UploadInitialSnapshotsAsync(cancellationToken);
                credential = await RequireCredentialAsync(cancellationToken);
            }

            var stores = await _db.Stores.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken);
            storeCount = stores.Count;
            var localStoreIds = stores.Select(x => x.SyncId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingCloudStores = cloudStores.Where(x => !localStoreIds.Contains(x.SyncId)).ToList();
            if (missingCloudStores.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Cloud contains {missingCloudStores.Count} store(s) that are not on this device. " +
                    "Use Restore Cloud Data to download the complete multi-store baseline.");
            }

            var needsSnapshotBaseline = false;
            foreach (var store in stores)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = await GetOrCreateStateAsync(store.Id, credential.DeviceId, cancellationToken);
                needsSnapshotBaseline |= state.LastSnapshotUploadedAt == null;
                state.LastSyncAt = DateTime.UtcNow;
                state.LastError = null;
                await _db.SaveChangesAsync(cancellationToken);

                try
                {
                    pushed += await PushStoreAsync(store, credential, cancellationToken);
                    pulled += await PullStoreAsync(store, state, credential, cancellationToken);
                    state.LastSuccessfulSyncAt = DateTime.UtcNow;
                    state.LastError = null;
                    await _db.SaveChangesAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    // A failed remote apply may clear tracked entities after a
                    // transaction rollback. Re-query the state before recording
                    // the error so diagnostics are never lost.
                    var failedState = await GetOrCreateStateAsync(store.Id, credential.DeviceId, cancellationToken);
                    failedState.LastError = Limit(ex.GetBaseException().Message, 1000);
                    failedState.LastSyncAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync(cancellationToken);
                    throw;
                }
            }

            // A newly created store needs a full restore baseline in addition
            // to its incremental log. Uploading all stores is rare and keeps the
            // restore set consistent across devices.
            if (needsSnapshotBaseline)
                await _account.UploadInitialSnapshotsAsync(cancellationToken);

            var conflictCount = await _db.SyncConflicts.CountAsync(x => x.ResolvedAt == null, cancellationToken);
            var pendingAfter = await _db.SyncOutbox.CountAsync(cancellationToken);
            await CompleteSyncRunAsync(syncRun, storeCount, pushed, pulled, conflictCount, pendingAfter, cancellationToken);
            return new CloudSyncSummary
            {
                StoreCount = stores.Count,
                PushedChanges = pushed,
                PulledChanges = pulled,
                ConflictCount = conflictCount,
                CompletedAt = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex)
        {
            await FailSyncRunAsync(syncRun, storeCount, pushed, pulled, ex, cancellationToken);
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
            SyncGate.Release();
        }
    }

    public async Task<CloudRestoreSummary> RestoreLatestSnapshotsAsync(
        bool replaceLocalData,
        CancellationToken cancellationToken = default)
    {
        if (!replaceLocalData)
            throw new InvalidOperationException("Cloud restore must replace the local database contents to preserve relational integrity.");

        await SyncGate.WaitAsync(cancellationToken);
        Interlocked.Exchange(ref _isRunning, 1);
        try
        {
            var credential = await RequireCredentialAsync(cancellationToken);
            credential = await EnsureFreshAccessTokenAsync(credential, cancellationToken);
            var snapshotSet = await GetAsync<SnapshotSetDownload>(
                credential, "/v1/sync/snapshot/set/latest", cancellationToken);
            if (snapshotSet.Snapshots.Count == 0)
                throw new InvalidOperationException("No complete cloud backup set is available.");
            ValidateSnapshotSet(snapshotSet);

            await _backup.CreateBackupAsync(retentionCount: 20);
            var restoredRows = await ReplaceLocalDataAsync(snapshotSet.Snapshots, cancellationToken);
            credential.InitialSnapshotUploadedAt = DateTimeOffset.UtcNow;
            await _credentials.SaveAsync(credential, cancellationToken);
            return new CloudRestoreSummary
            {
                StoreCount = snapshotSet.Snapshots.Count,
                RestoredRows = restoredRows,
                RestoredAt = DateTimeOffset.UtcNow
            };
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
            SyncGate.Release();
        }
    }

    private async Task<int> PushStoreAsync(
        Store store,
        CloudCredential credential,
        CancellationToken cancellationToken)
    {
        var acceptedTotal = 0;
        for (var batchNumber = 0; batchNumber < MaximumPushBatchesPerRun; batchNumber++)
        {
            var oldest = await _db.SyncOutbox
                .Where(x => x.StoreId == store.Id &&
                            (x.LastError == null || !x.LastError.StartsWith("Conflict:")))
                .OrderBy(x => x.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (oldest == null) break;

            var operationId = string.IsNullOrWhiteSpace(oldest.OperationId)
                ? oldest.ChangeId
                : oldest.OperationId;
            var batch = await _db.SyncOutbox
                .Where(x => x.StoreId == store.Id &&
                            (x.OperationId == operationId ||
                             (x.OperationId == "" && x.ChangeId == operationId)))
                .OrderBy(x => x.EntityType == nameof(Store) ? 0 : 1)
                .ThenBy(x => x.Id)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0) break;
            var wireBatch = batch
                .GroupBy(x => new { x.EntityType, x.EntitySyncId })
                .Select(group => group.OrderBy(x => x.Id).Last())
                .OrderBy(x => x.EntityType == nameof(Store) ? 0 : 1)
                .ThenBy(x => x.Id)
                .ToList();
            if (wireBatch.Count > PushBatchSize)
                throw new InvalidOperationException(
                    $"Cloud operation {operationId} contains {wireBatch.Count} records; the limit is {PushBatchSize}.");

            credential = await EnsureFreshAccessTokenAsync(credential, cancellationToken);
            PushResponse response;
            try
            {
                response = await PostAsync<PushResponse>(credential, "/v1/sync/push", new
                {
                    storeSyncId = store.SyncId,
                    operationId,
                    changes = wireBatch.Select(x => new
                    {
                        changeId = x.ChangeId,
                        operationId,
                        entityType = x.EntityType,
                        entitySyncId = x.EntitySyncId,
                        operation = x.Operation,
                        entityVersion = x.EntityVersion,
                        baseCloudVersion = x.BaseCloudVersion,
                        payload = JsonSerializer.Deserialize<JsonElement>(x.PayloadJson, JsonOptions)
                    }).ToArray()
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                foreach (var item in batch)
                {
                    item.AttemptCount++;
                    item.LastAttemptAt = DateTime.UtcNow;
                    item.LastError = Limit(ex.GetBaseException().Message, 1000);
                }
                await _db.SaveChangesAsync(cancellationToken);
                throw;
            }

            foreach (var group in batch.GroupBy(x => new { x.EntityType, x.EntitySyncId }))
            {
                var rows = group.OrderBy(x => x.Id).ToList();
                var item = rows[^1];
                var result = response.Results.FirstOrDefault(x => x.ChangeId == item.ChangeId)
                             ?? throw new InvalidOperationException("The cloud API omitted a push result.");
                foreach (var row in rows)
                {
                    row.AttemptCount++;
                    row.LastAttemptAt = DateTime.UtcNow;
                }
                if (response.Committed && string.Equals(result.Status, "accepted", StringComparison.OrdinalIgnoreCase))
                {
                    await UpdateEntityCloudVersionAsync(
                        item.StoreId, item.EntityType, item.EntitySyncId, result.CloudVersion, item.Id, cancellationToken);
                    _db.SyncOutbox.RemoveRange(rows);
                    acceptedTotal += rows.Count;
                    continue;
                }

                if (string.Equals(result.Status, "conflict", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var row in rows)
                        row.LastError = "Conflict: " + Limit(result.Message, 960);
                    await AddConflictAsync(new SyncConflict
                    {
                        StoreId = item.StoreId,
                        EntityType = item.EntityType,
                        EntitySyncId = item.EntitySyncId,
                        ChangeId = item.ChangeId,
                        LocalBaseCloudVersion = item.BaseCloudVersion,
                        RemoteCloudVersion = result.CloudVersion,
                        LocalOperation = item.Operation,
                        RemoteOperation = NormalizeRemoteOperation(result.Operation),
                        LocalPayloadJson = item.PayloadJson,
                        RemotePayloadJson = result.Payload.ValueKind == JsonValueKind.Undefined
                            ? "{}"
                            : result.Payload.GetRawText(),
                        Message = string.IsNullOrWhiteSpace(result.Message)
                            ? "The cloud record changed on another device."
                            : result.Message
                    }, cancellationToken);
                }
                else
                {
                    foreach (var row in rows)
                        row.LastError = "Operation blocked because another record in the same business operation conflicted.";
                }
            }
            await _db.SaveChangesAsync(cancellationToken);
            if (!response.Committed) break;
        }
        return acceptedTotal;
    }

    private async Task<int> PullStoreAsync(
        Store store,
        SyncState state,
        CloudCredential credential,
        CancellationToken cancellationToken)
    {
        var pulled = 0;
        while (true)
        {
            credential = await EnsureFreshAccessTokenAsync(credential, cancellationToken);
            var response = await GetAsync<PullResponse>(
                credential,
                $"/v1/sync/pull?storeSyncId={Uri.EscapeDataString(store.SyncId)}" +
                $"&after={state.PullCursor}&limit={PullBatchSize}",
                cancellationToken);
            if (response.Changes.Count == 0) break;

            var ordered = response.Changes.OrderBy(ChangePriority).ThenBy(x => x.Cursor).ToList();
            var failed = new List<(PullChange Change, Exception Error)>();
            foreach (var change in ordered)
            {
                try
                {
                    await ApplyRemoteChangeAsync(store, credential, change, cancellationToken);
                    pulled++;
                }
                catch (Exception ex)
                {
                    failed.Add((change, ex));
                }
            }

            // Retry once after the remainder of the page has materialized. This
            // handles valid dependency ordering gaps without pinning the cursor.
            foreach (var failure in failed)
            {
                try
                {
                    await ApplyRemoteChangeAsync(store, credential, failure.Change, cancellationToken);
                    pulled++;
                }
                catch (Exception retryError)
                {
                    await QuarantineRemoteChangeAsync(store.Id, failure.Change, retryError, cancellationToken);
                }
            }

            state = await GetOrCreateStateAsync(store.Id, credential.DeviceId, cancellationToken);
            state.PullCursor = Math.Max(state.PullCursor, response.NextCursor);
            state.DeviceId = credential.DeviceId;
            await _db.SaveChangesAsync(cancellationToken);
            if (!response.HasMore) break;
        }
        return pulled;
    }

    private async Task QuarantineRemoteChangeAsync(
        int storeId, PullChange change, Exception error, CancellationToken cancellationToken)
    {
        _db.ChangeTracker.Clear();
        await AddConflictAsync(new SyncConflict
        {
            StoreId = storeId,
            EntityType = change.EntityType,
            EntitySyncId = change.EntitySyncId,
            ChangeId = change.ChangeId,
            LocalBaseCloudVersion = 0,
            RemoteCloudVersion = change.CloudVersion,
            LocalOperation = "upsert",
            RemoteOperation = NormalizeRemoteOperation(change.Operation),
            LocalPayloadJson = "{}",
            RemotePayloadJson = change.Payload.ValueKind == JsonValueKind.Undefined
                ? "{}"
                : change.Payload.GetRawText(),
            Message = "Remote record was quarantined: " + Limit(error.GetBaseException().Message, 900)
        }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task ApplyRemoteChangeAsync(
        Store store,
        CloudCredential credential,
        PullChange change,
        CancellationToken cancellationToken)
    {
        var pending = await _db.SyncOutbox
            .Where(x => x.StoreId == store.Id && x.EntityType == change.EntityType &&
                        x.EntitySyncId == change.EntitySyncId)
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (pending != null)
        {
            if (string.Equals(change.OriginDeviceId, credential.DeviceId, StringComparison.Ordinal))
            {
                await UpdateEntityCloudVersionAsync(
                    store.Id, change.EntityType, change.EntitySyncId,
                    change.CloudVersion, pending.ChangeId == change.ChangeId ? pending.Id : 0,
                    cancellationToken);
                if (pending.ChangeId == change.ChangeId) _db.SyncOutbox.Remove(pending);
                await _db.SaveChangesAsync(cancellationToken);
                return;
            }

            await AddConflictAsync(new SyncConflict
            {
                StoreId = store.Id,
                EntityType = change.EntityType,
                EntitySyncId = change.EntitySyncId,
                ChangeId = change.ChangeId,
                LocalBaseCloudVersion = pending.BaseCloudVersion,
                RemoteCloudVersion = change.CloudVersion,
                LocalOperation = pending.Operation,
                RemoteOperation = NormalizeRemoteOperation(change.Operation),
                LocalPayloadJson = pending.PayloadJson,
                RemotePayloadJson = change.Payload.GetRawText(),
                Message = "A remote change arrived while this device has an unsynchronized local edit."
            }, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            using (_storeContext.SuppressCloudCapture())
            {
                if (string.Equals(change.Operation, "delete", StringComparison.OrdinalIgnoreCase))
                    await DeleteRemoteEntityAsync(store.Id, change, cancellationToken);
                else
                    await UpsertRemoteEntityAsync(store.Id, change, cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
            }
            await _db.CommitExternalTransactionAsync(transaction, cancellationToken);
        }
        catch
        {
            await _db.RollbackExternalTransactionAsync(transaction, cancellationToken);
            throw;
        }
    }

    private async Task UpsertRemoteEntityAsync(
        int storeId,
        PullChange change,
        CancellationToken cancellationToken)
    {
        switch (change.EntityType)
        {
            case nameof(Store):
                await UpsertStoreAsync(change, cancellationToken);
                break;
            case nameof(Category): await UpsertAsync<Category>(storeId, change, cancellationToken); break;
            case nameof(Customer): await UpsertAsync<Customer>(storeId, change, cancellationToken); break;
            case nameof(Discount): await UpsertAsync<Discount>(storeId, change, cancellationToken); break;
            case nameof(Product): await UpsertAsync<Product>(storeId, change, cancellationToken); break;
            case nameof(PurchaseDocument): await UpsertAsync<PurchaseDocument>(storeId, change, cancellationToken); break;
            case nameof(PurchaseItem): await UpsertAsync<PurchaseItem>(storeId, change, cancellationToken); break;
            case nameof(Sale): await UpsertAsync<Sale>(storeId, change, cancellationToken); break;
            case nameof(SaleItem): await UpsertAsync<SaleItem>(storeId, change, cancellationToken); break;
            case nameof(SalePayment): await UpsertAsync<SalePayment>(storeId, change, cancellationToken); break;
            case nameof(StockTransaction): await UpsertAsync<StockTransaction>(storeId, change, cancellationToken); break;
            case nameof(StockTransfer): await UpsertAsync<StockTransfer>(storeId, change, cancellationToken); break;
            case nameof(StockTransferItem): await UpsertAsync<StockTransferItem>(storeId, change, cancellationToken); break;
            case nameof(Supplier): await UpsertAsync<Supplier>(storeId, change, cancellationToken); break;
            case nameof(Tax): await UpsertAsync<Tax>(storeId, change, cancellationToken); break;
            case nameof(User): await UpsertAsync<User>(storeId, change, cancellationToken); break;
            case nameof(CashSession): await UpsertAsync<CashSession>(storeId, change, cancellationToken); break;
            case nameof(CashMovement): await UpsertAsync<CashMovement>(storeId, change, cancellationToken); break;
            case nameof(Setting):
                if (!SettingSyncPolicy.IsDeviceLocal(
                        GetString(change.Payload, nameof(Setting.Key))))
                    await UpsertAsync<Setting>(storeId, change, cancellationToken);
                break;
            default: throw new InvalidOperationException($"Unsupported cloud entity type: {change.EntityType}.");
        }
    }

    private async Task UpsertStoreAsync(PullChange change, CancellationToken cancellationToken)
    {
        var entity = await _db.Stores.FirstOrDefaultAsync(x => x.SyncId == change.EntitySyncId, cancellationToken);
        if (entity == null)
        {
            entity = new Store { SyncId = change.EntitySyncId };
            _db.Stores.Add(entity);
        }
        ApplyScalarProperties(entity, change.Payload);
        entity.CloudVersion = change.CloudVersion;
    }

    private async Task UpsertAsync<TEntity>(
        int storeId,
        PullChange change,
        CancellationToken cancellationToken)
        where TEntity : StoreScopedEntity, new()
    {
        var entity = await _db.Set<TEntity>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.StoreId == storeId && x.SyncId == change.EntitySyncId, cancellationToken);
        if (entity == null)
        {
            entity = new TEntity { StoreId = storeId, SyncId = change.EntitySyncId };
            _db.Set<TEntity>().Add(entity);
        }

        ApplyScalarProperties(entity, change.Payload);
        await ResolveReferencesAsync(entity, change.Payload, storeId, cancellationToken);
        entity.CloudVersion = change.CloudVersion;
    }

    private async Task DeleteRemoteEntityAsync(
        int storeId,
        PullChange change,
        CancellationToken cancellationToken)
    {
        if (change.EntityType == nameof(Store))
        {
            var store = await _db.Stores.FirstOrDefaultAsync(x => x.SyncId == change.EntitySyncId, cancellationToken);
            if (store != null)
            {
                store.IsActive = false;
                store.CloudVersion = change.CloudVersion;
                store.SyncUpdatedAt = DateTime.UtcNow;
            }
            return;
        }

        if (ImmutableLedgerEntities.Contains(change.EntityType))
            throw new InvalidOperationException(
                $"Remote deletion of immutable {change.EntityType} records is not permitted.");
        if (!EntityTypes.TryGetValue(change.EntityType, out var type))
            throw new InvalidOperationException($"Unsupported cloud entity type: {change.EntityType}.");
        var entity = await FindEntityAsync(type, storeId, change.EntitySyncId, cancellationToken);
        if (entity == null) return;
        var activeProperty = entity.GetType().GetProperty("IsActive");
        if (activeProperty?.CanWrite == true && activeProperty.PropertyType == typeof(bool))
        {
            activeProperty.SetValue(entity, false);
            if (entity is StoreScopedEntity scoped)
            {
                scoped.CloudVersion = change.CloudVersion;
                scoped.SyncUpdatedAt = DateTime.UtcNow;
            }
        }
        else
        {
            _db.Remove(entity);
        }
    }

    private async Task ResolveReferencesAsync(
        StoreScopedEntity entity,
        JsonElement payload,
        int storeId,
        CancellationToken cancellationToken)
    {
        switch (entity)
        {
            case Product x:
                x.CategoryId = await RequireIdAsync<Category>(payload, "CategorySyncId", storeId, cancellationToken);
                break;
            case PurchaseDocument x:
                x.SupplierId = await OptionalIdAsync<Supplier>(payload, "SupplierSyncId", storeId, cancellationToken);
                x.UserId = await RequireIdAsync<User>(payload, "UserSyncId", storeId, cancellationToken);
                break;
            case PurchaseItem x:
                x.PurchaseDocumentId = await RequireIdAsync<PurchaseDocument>(payload, "PurchaseDocumentSyncId", storeId, cancellationToken);
                x.ProductId = await RequireIdAsync<Product>(payload, "ProductSyncId", storeId, cancellationToken);
                break;
            case Sale x:
                x.CustomerId = await OptionalIdAsync<Customer>(payload, "CustomerSyncId", storeId, cancellationToken);
                x.UserId = await RequireIdAsync<User>(payload, "UserSyncId", storeId, cancellationToken);
                x.CashSessionId = await OptionalIdAsync<CashSession>(payload, "CashSessionSyncId", storeId, cancellationToken);
                x.RefundedSaleId = await OptionalIdAsync<Sale>(payload, "RefundedSaleSyncId", storeId, cancellationToken);
                break;
            case SaleItem x:
                x.SaleId = await RequireIdAsync<Sale>(payload, "SaleSyncId", storeId, cancellationToken);
                x.ProductId = await RequireIdAsync<Product>(payload, "ProductSyncId", storeId, cancellationToken);
                x.RefundedSaleItemId = await OptionalIdAsync<SaleItem>(payload, "RefundedSaleItemSyncId", storeId, cancellationToken);
                break;
            case SalePayment x:
                x.SaleId = await RequireIdAsync<Sale>(payload, "SaleSyncId", storeId, cancellationToken);
                break;
            case StockTransfer x:
                x.DestinationStoreId = await RequireStoreIdAsync(payload, "DestinationStoreSyncId", cancellationToken);
                x.CreatedByUserId = await OptionalIdAsync<User>(payload, "CreatedByUserSyncId", storeId, cancellationToken);
                x.DispatchedByUserId = await OptionalIdAsync<User>(payload, "DispatchedByUserSyncId", storeId, cancellationToken);
                x.ReceivedByUserId = await OptionalIdAsync<User>(payload, "ReceivedByUserSyncId", x.DestinationStoreId, cancellationToken);
                x.CancelledByUserId = await OptionalIdAsync<User>(payload, "CancelledByUserSyncId", storeId, cancellationToken);
                break;
            case StockTransferItem x:
            {
                x.StockTransferId = await RequireIdAsync<StockTransfer>(payload, "StockTransferSyncId", storeId, cancellationToken);
                x.ProductId = await RequireIdAsync<Product>(payload, "ProductSyncId", storeId, cancellationToken);
                var transfer = await _db.StockTransfers.IgnoreQueryFilters().AsNoTracking()
                    .FirstAsync(t => t.Id == x.StockTransferId, cancellationToken);
                x.DestinationProductId = await RequireOrCreateDestinationProductAsync(
                    payload, transfer.DestinationStoreId, x.ProductId, cancellationToken);
                break;
            }
            case StockTransaction x:
                x.ProductId = await RequireIdAsync<Product>(payload, "ProductSyncId", storeId, cancellationToken);
                x.SaleId = await OptionalIdAsync<Sale>(payload, "SaleSyncId", storeId, cancellationToken);
                x.SaleItemId = await OptionalIdAsync<SaleItem>(payload, "SaleItemSyncId", storeId, cancellationToken);
                x.StockTransferId = await OptionalGlobalIdAsync<StockTransfer>(payload, "StockTransferSyncId", cancellationToken);
                x.StockTransferItemId = await OptionalGlobalIdAsync<StockTransferItem>(payload, "StockTransferItemSyncId", cancellationToken);
                x.UserId = await OptionalIdAsync<User>(payload, "UserSyncId", storeId, cancellationToken);
                break;
            case CashSession x:
                x.OpenedByUserId = await RequireIdAsync<User>(payload, "OpenedByUserSyncId", storeId, cancellationToken);
                x.ClosedByUserId = await OptionalIdAsync<User>(payload, "ClosedByUserSyncId", storeId, cancellationToken);
                break;
            case CashMovement x:
                x.CashSessionId = await RequireIdAsync<CashSession>(payload, "CashSessionSyncId", storeId, cancellationToken);
                x.UserId = await RequireIdAsync<User>(payload, "UserSyncId", storeId, cancellationToken);
                break;
        }
    }

    private async Task<int> RequireStoreIdAsync(JsonElement payload, string property, CancellationToken cancellationToken)
    {
        var syncId = GetString(payload, property);
        if (string.IsNullOrWhiteSpace(syncId))
            throw new InvalidOperationException($"Cloud change is missing {property}.");
        var existing = await _db.Stores.FirstOrDefaultAsync(x => x.SyncId == syncId, cancellationToken);
        if (existing != null) return existing.Id;

        var requestedCode = Limit(GetString(payload, "DestinationStoreCode")?.Trim(), 24);
        var requestedName = Limit(GetString(payload, "DestinationStoreName")?.Trim(), 100);
        var code = string.IsNullOrWhiteSpace(requestedCode) ? $"SYNC-{syncId[..Math.Min(8, syncId.Length)]}" : requestedCode.ToUpperInvariant();
        if (await _db.Stores.AnyAsync(x => x.Code == code, cancellationToken))
            code = $"SYNC-{syncId[..Math.Min(8, syncId.Length)]}".ToUpperInvariant();
        var store = new Store
        {
            SyncId = syncId,
            Code = code,
            Name = string.IsNullOrWhiteSpace(requestedName) ? $"Synced Store {code}" : requestedName,
            Address = Limit(GetString(payload, "DestinationStoreAddress"), 500),
            Phone = Limit(GetString(payload, "DestinationStorePhone"), 30),
            IsActive = GetBoolean(payload, "DestinationStoreIsActive") ?? true
        };
        _db.Stores.Add(store);
        await _db.SaveChangesAsync(cancellationToken);
        return store.Id;
    }

    private async Task<int> RequireOrCreateDestinationProductAsync(
        JsonElement payload,
        int destinationStoreId,
        int sourceProductId,
        CancellationToken cancellationToken)
    {
        var productSyncId = GetString(payload, "DestinationProductSyncId");
        if (string.IsNullOrWhiteSpace(productSyncId))
            throw new InvalidOperationException("Cloud transfer item is missing DestinationProductSyncId.");
        var existingId = await _db.Products.IgnoreQueryFilters()
            .Where(x => x.StoreId == destinationStoreId && x.SyncId == productSyncId)
            .Select(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        if (existingId > 0) return existingId;

        var source = await _db.Products.IgnoreQueryFilters().AsNoTracking().Include(x => x.Category)
            .FirstOrDefaultAsync(x => x.Id == sourceProductId, cancellationToken)
            ?? throw new InvalidOperationException("Cloud transfer item depends on a missing source product.");
        var categorySyncId = GetString(payload, "DestinationCategorySyncId");
        Category? category = null;
        if (!string.IsNullOrWhiteSpace(categorySyncId))
        {
            category = await _db.Categories.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.StoreId == destinationStoreId && x.SyncId == categorySyncId, cancellationToken);
        }
        var sourceCategoryName = source.Category?.Name ?? "Transferred Products";
        category ??= await _db.Categories.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.StoreId == destinationStoreId && x.Name == sourceCategoryName, cancellationToken);
        if (category == null)
        {
            category = new Category
            {
                StoreId = destinationStoreId,
                SyncId = string.IsNullOrWhiteSpace(categorySyncId) ? Guid.NewGuid().ToString("N") : categorySyncId,
                Name = sourceCategoryName,
                Description = source.Category?.Description,
                Color = source.Category?.Color ?? "#64748B",
                SortOrder = source.Category?.SortOrder ?? 999,
                IsActive = true
            };
            _db.Categories.Add(category);
            await _db.SaveChangesAsync(cancellationToken);
        }

        var product = new Product
        {
            StoreId = destinationStoreId,
            SyncId = productSyncId,
            Name = source.Name,
            Description = source.Description,
            Sku = source.Sku,
            Barcode = source.Barcode,
            CategoryId = category.Id,
            Price = source.Price,
            CostPrice = source.CostPrice,
            TaxRate = source.TaxRate,
            Unit = source.EffectiveUnit,
            StockQuantity = 0m,
            LowStockThreshold = source.LowStockThreshold,
            ImagePath = null,
            IsWeighted = source.IsWeighted,
            IsActive = source.IsActive,
            AllowDiscount = source.AllowDiscount
        };
        _db.Products.Add(product);
        await _db.SaveChangesAsync(cancellationToken);
        return product.Id;
    }

    private async Task<int?> OptionalGlobalIdAsync<TEntity>(JsonElement payload, string property, CancellationToken cancellationToken)
        where TEntity : StoreScopedEntity
    {
        var syncId = GetString(payload, property);
        if (string.IsNullOrWhiteSpace(syncId)) return null;
        var local = _db.Set<TEntity>().Local.FirstOrDefault(x => x.SyncId == syncId);
        if (local != null) return ReadEntityId(local);
        var id = await _db.Set<TEntity>().IgnoreQueryFilters().Where(x => x.SyncId == syncId)
            .Select(x => EF.Property<int>(x, "Id")).FirstOrDefaultAsync(cancellationToken);
        return id <= 0 ? null : id;
    }

    private async Task<int> RequireIdAsync<TEntity>(
        JsonElement payload,
        string property,
        int storeId,
        CancellationToken cancellationToken)
        where TEntity : StoreScopedEntity
    {
        var id = await OptionalIdAsync<TEntity>(payload, property, storeId, cancellationToken);
        return id ?? throw new InvalidOperationException(
            $"Cloud change depends on a missing {typeof(TEntity).Name} record ({property}).");
    }

    private async Task<int?> OptionalIdAsync<TEntity>(
        JsonElement payload,
        string property,
        int storeId,
        CancellationToken cancellationToken)
        where TEntity : StoreScopedEntity
    {
        var syncId = GetString(payload, property);
        if (string.IsNullOrWhiteSpace(syncId)) return null;
        var local = _db.Set<TEntity>().Local.FirstOrDefault(x => x.StoreId == storeId && x.SyncId == syncId);
        if (local != null) return ReadEntityId(local);
        var id = await _db.Set<TEntity>()
            .IgnoreQueryFilters()
            .Where(x => x.StoreId == storeId && x.SyncId == syncId)
            .Select(x => EF.Property<int>(x, "Id"))
            .FirstOrDefaultAsync(cancellationToken);
        return id <= 0 ? null : id;
    }

    private static void ApplyScalarProperties(object entity, JsonElement payload)
    {
        foreach (var property in entity.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanWrite || property.GetIndexParameters().Length != 0) continue;
            if (property.GetCustomAttribute<NotMappedAttribute>() != null) continue;
            if (property.Name is "Id" or "StoreId" or "SyncId" or "CloudVersion" or "ImagePath") continue;
            if (ForeignKeyProperties.Contains(property.Name)) continue;
            if (!TryGetProperty(payload, property.Name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Null)
            {
                if (!property.PropertyType.IsValueType || Nullable.GetUnderlyingType(property.PropertyType) != null)
                    property.SetValue(entity, null);
                continue;
            }

            try
            {
                property.SetValue(entity, JsonSerializer.Deserialize(value.GetRawText(), property.PropertyType, JsonOptions));
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Cloud value for {entity.GetType().Name}.{property.Name} is invalid.", ex);
            }
        }
    }

    private async Task<object?> FindEntityAsync(
        Type type,
        int storeId,
        string syncId,
        CancellationToken cancellationToken)
    {
        var method = typeof(CloudSyncService).GetMethod(
            nameof(FindEntityGenericAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(type);
        var task = (Task<object?>)method.Invoke(this, new object[] { storeId, syncId, cancellationToken })!;
        return await task;
    }

    private async Task<object?> FindEntityGenericAsync<TEntity>(
        int storeId,
        string syncId,
        CancellationToken cancellationToken)
        where TEntity : StoreScopedEntity
        => await _db.Set<TEntity>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.StoreId == storeId && x.SyncId == syncId, cancellationToken);

    private async Task UpdateEntityCloudVersionAsync(
        int storeId,
        string entityType,
        string entitySyncId,
        long cloudVersion,
        long acceptedOutboxId,
        CancellationToken cancellationToken)
    {
        if (!TableNames.TryGetValue(entityType, out var table))
            throw new InvalidOperationException($"Unsupported cloud entity type: {entityType}.");
        var sql = entityType == nameof(Store)
            ? $"UPDATE \"{table}\" SET \"CloudVersion\" = {{0}} WHERE \"SyncId\" = {{1}};"
            : $"UPDATE \"{table}\" SET \"CloudVersion\" = {{0}} WHERE \"StoreId\" = {{1}} AND \"SyncId\" = {{2}};";
        if (entityType == nameof(Store))
            await _db.Database.ExecuteSqlRawAsync(sql, new object[] { cloudVersion, entitySyncId }, cancellationToken);
        else
            await _db.Database.ExecuteSqlRawAsync(sql, new object[] { cloudVersion, storeId, entitySyncId }, cancellationToken);

        // Keep tracked entities consistent with the direct SQL update. EF identity
        // resolution would otherwise return an older cloud revision later in the
        // same synchronization run.
        foreach (var entry in _db.ChangeTracker.Entries())
        {
            var matches = entry.Entity switch
            {
                Store trackedStore => entityType == nameof(Store) &&
                                      string.Equals(trackedStore.SyncId, entitySyncId, StringComparison.Ordinal),
                StoreScopedEntity trackedEntity =>
                    string.Equals(entry.Entity.GetType().Name, entityType, StringComparison.Ordinal) &&
                    trackedEntity.StoreId == storeId &&
                    string.Equals(trackedEntity.SyncId, entitySyncId, StringComparison.Ordinal),
                _ => false
            };
            if (!matches) continue;

            var cloudProperty = entry.Property(nameof(Store.CloudVersion));
            var current = Convert.ToInt64(cloudProperty.CurrentValue ?? 0L);
            var accepted = Math.Max(current, cloudVersion);
            cloudProperty.CurrentValue = accepted;
            cloudProperty.OriginalValue = accepted;
            cloudProperty.IsModified = false;
        }

        var later = _db.SyncOutbox.Where(x =>
            x.StoreId == storeId && x.EntityType == entityType && x.EntitySyncId == entitySyncId &&
            x.Id != acceptedOutboxId && x.BaseCloudVersion < cloudVersion);
        await later.ExecuteUpdateAsync(
            updates => updates.SetProperty(x => x.BaseCloudVersion, cloudVersion), cancellationToken);
        foreach (var tracked in _db.ChangeTracker.Entries<SyncOutboxItem>()
                     .Select(x => x.Entity)
                     .Where(x => x.StoreId == storeId && x.EntityType == entityType &&
                                 x.EntitySyncId == entitySyncId && x.Id != acceptedOutboxId &&
                                 x.BaseCloudVersion < cloudVersion))
        {
            tracked.BaseCloudVersion = cloudVersion;
        }
    }

    private async Task AddConflictAsync(SyncConflict conflict, CancellationToken cancellationToken)
    {
        var existing = await _db.SyncConflicts.FirstOrDefaultAsync(
            x => x.ChangeId == conflict.ChangeId, cancellationToken);
        if (existing == null)
        {
            _db.SyncConflicts.Add(conflict);
            return;
        }
        existing.RemoteCloudVersion = conflict.RemoteCloudVersion;
        existing.LocalOperation = conflict.LocalOperation;
        existing.RemoteOperation = conflict.RemoteOperation;
        existing.RemotePayloadJson = conflict.RemotePayloadJson;
        existing.LocalPayloadJson = conflict.LocalPayloadJson;
        existing.Message = conflict.Message;
        existing.ResolvedAt = null;
        existing.Resolution = null;
        existing.ResolvedPayloadJson = null;
    }

    private async Task<SyncState> GetOrCreateStateAsync(
        int storeId,
        string deviceId,
        CancellationToken cancellationToken)
    {
        var state = await _db.SyncStates.FirstOrDefaultAsync(x => x.StoreId == storeId, cancellationToken);
        if (state != null)
        {
            state.DeviceId = deviceId;
            return state;
        }
        state = new SyncState { StoreId = storeId, DeviceId = deviceId };
        _db.SyncStates.Add(state);
        return state;
    }

    private async Task<long> ReplaceLocalDataAsync(
        IReadOnlyList<SnapshotDownload> snapshots,
        CancellationToken cancellationToken)
    {
        _db.ChangeTracker.Clear();
        await _db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await _db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF;", cancellationToken);
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                using (_storeContext.SuppressCloudCapture())
                {
                    await DbSchemaUpgrader.DropIntegrityGuardsAsync(_db);
                    foreach (var table in new[]
                             {
                                 "CashMovements", "SalePayments", "StockTransactions", "StockTransferItems", "SaleItems", "PurchaseItems",
                                 "StockTransfers", "Sales", "PurchaseDocuments", "CashSessions", "Products", "Categories", "Customers",
                                 "Suppliers", "Taxes", "Discounts", "Settings", "Users", "SyncOutbox",
                                 "SyncConflicts", "SyncStates", "SyncRuns", "Stores"
                             })
                    {
                        await _db.Database.ExecuteSqlRawAsync($"DELETE FROM \"{table}\";", cancellationToken);
                    }

                    var storeIds = new Dictionary<string, int>(StringComparer.Ordinal);
                    long restoredRows = 0;
                    foreach (var snapshot in snapshots)
                    {
                        var row = RequireProperty(snapshot.Payload, "store");
                        var syncId = GetString(row, nameof(Store.SyncId))
                                     ?? throw new InvalidOperationException("A cloud snapshot is missing its store sync ID.");
                        var synthetic = new PullChange
                        {
                            ChangeId = $"restore-store-{syncId}",
                            EntityType = nameof(Store),
                            EntitySyncId = syncId,
                            EntityVersion = GetLong(row, nameof(Store.SyncVersion), 1),
                            CloudVersion = GetLong(row, nameof(Store.CloudVersion), 0),
                            Operation = "upsert",
                            Payload = row
                        };
                        await UpsertRemoteEntityAsync(0, synthetic, cancellationToken);
                        await _db.SaveChangesAsync(cancellationToken);
                        var local = await _db.Stores.FirstAsync(x => x.SyncId == syncId, cancellationToken);
                        storeIds[syncId] = local.Id;
                        restoredRows++;
                    }

                    foreach (var entityName in RestoreOrder)
                    {
                        foreach (var snapshot in snapshots)
                        {
                            var storeRow = RequireProperty(snapshot.Payload, "store");
                            var storeSyncId = GetString(storeRow, nameof(Store.SyncId))
                                              ?? throw new InvalidOperationException("A snapshot store sync ID is missing.");
                            var entities = RequireProperty(snapshot.Payload, "entities");
                            if (!TryGetProperty(entities, entityName, out var rows) || rows.ValueKind != JsonValueKind.Array)
                                continue;
                            foreach (var row in rows.EnumerateArray())
                            {
                                if (entityName == nameof(Setting))
                                {
                                    var key = GetString(row, nameof(Setting.Key)) ?? string.Empty;
                                    if (SettingSyncPolicy.IsDeviceLocal(key))
                                        continue;
                                }
                                var syncId = GetString(row, nameof(StoreScopedEntity.SyncId))
                                             ?? throw new InvalidOperationException($"A cloud {entityName} row is missing its sync ID.");
                                var synthetic = new PullChange
                                {
                                    ChangeId = $"restore-{entityName}-{syncId}",
                                    EntityType = entityName,
                                    EntitySyncId = syncId,
                                    EntityVersion = GetLong(row, nameof(StoreScopedEntity.SyncVersion), 1),
                                    CloudVersion = GetLong(row, nameof(StoreScopedEntity.CloudVersion), 0),
                                    Operation = "upsert",
                                    Payload = row
                                };
                                await UpsertRemoteEntityAsync(storeIds[storeSyncId], synthetic, cancellationToken);
                                restoredRows++;
                            }
                            await _db.SaveChangesAsync(cancellationToken);
                        }
                    }

                    foreach (var snapshot in snapshots)
                    {
                        var storeElement = RequireProperty(snapshot.Payload, "store");
                        var storeSyncId = GetString(storeElement, nameof(Store.SyncId))!;
                        var restoredStoreId = storeIds[storeSyncId];
                        _db.Settings.Add(new Setting
                        {
                            StoreId = restoredStoreId,
                            Key = SettingSyncPolicy.SetupCompleteKey,
                            Value = "true",
                            Description = "Device-local online onboarding state"
                        });
                        _db.Settings.Add(new Setting
                        {
                            StoreId = restoredStoreId,
                            Key = SettingSyncPolicy.SetupPreparedKey,
                            Value = "false",
                            Description = "Device-local online onboarding state"
                        });
                        _db.SyncStates.Add(new SyncState
                        {
                            StoreId = restoredStoreId,
                            PullCursor = snapshot.SyncCursor,
                            LastSyncAt = DateTime.UtcNow,
                            LastSuccessfulSyncAt = DateTime.UtcNow,
                            LastSnapshotUploadedAt = DateTime.UtcNow
                        });
                    }
                    await _db.SaveChangesAsync(cancellationToken);
                    await DbSchemaUpgrader.EnsureIntegrityGuardsAsync(_db);

                    var connection = _db.Database.GetDbConnection();
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction.GetDbTransaction();
                    command.CommandText = "PRAGMA foreign_key_check;";
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    if (await reader.ReadAsync(cancellationToken))
                        throw new InvalidOperationException(
                            "Cloud restore failed relational validation. The pre-restore backup remains available.");

                    await _db.CommitExternalTransactionAsync(transaction, cancellationToken);
                    return restoredRows;
                }
            }
            catch
            {
                await _db.RollbackExternalTransactionAsync(transaction, cancellationToken);
                throw;
            }
            finally
            {
                await _db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;", cancellationToken);
            }
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }

    private static void ValidateSnapshotSet(SnapshotSetDownload set)
    {
        if (string.IsNullOrWhiteSpace(set.BackupSetId))
            throw new InvalidOperationException("Cloud backup set ID is missing.");
        if (set.Snapshots.Count == 0)
            throw new InvalidOperationException("Cloud backup set contains no store snapshots.");
        if (set.Snapshots.Any(x => !string.Equals(x.BackupSetId, set.BackupSetId, StringComparison.Ordinal)))
            throw new InvalidOperationException("Cloud restore contains snapshots from different backup sets.");
        if (set.Snapshots.Any(x => x.CapturedAt.ToUniversalTime() != set.CapturedAt.ToUniversalTime()))
            throw new InvalidOperationException("Cloud restore contains snapshots captured at different times.");
        if (set.Snapshots.Select(x => x.SnapshotId).Any(string.IsNullOrWhiteSpace) ||
            set.Snapshots.Select(x => x.SnapshotId).Distinct(StringComparer.Ordinal).Count() != set.Snapshots.Count)
            throw new InvalidOperationException("Cloud restore contains missing or duplicate snapshot IDs.");

        var storeSyncIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in set.Snapshots)
        {
            if (string.IsNullOrWhiteSpace(snapshot.PayloadJson))
                throw new InvalidOperationException("Cloud snapshot payload text is missing.");
            try
            {
                snapshot.Payload = JsonSerializer.Deserialize<JsonElement>(snapshot.PayloadJson, JsonOptions).Clone();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Cloud snapshot payload is invalid JSON.", ex);
            }
            if (snapshot.SchemaVersion != 5)
                throw new InvalidOperationException(
                    $"Cloud snapshot schema {snapshot.SchemaVersion} is not supported by this application.");
            if (!Version.TryParse(snapshot.AppVersion, out var appVersion) || appVersion.Major != 1)
                throw new InvalidOperationException("Cloud snapshot application version is incompatible.");
            var payloadSchema = GetLong(snapshot.Payload, "schemaVersion", -1);
            if (payloadSchema != snapshot.SchemaVersion)
                throw new InvalidOperationException("Cloud snapshot schema metadata does not match its payload.");
            var exportedAt = GetDateTimeOffset(snapshot.Payload, "exportedAtUtc");
            if (exportedAt == null || exportedAt.Value.ToUniversalTime() != set.CapturedAt.ToUniversalTime())
                throw new InvalidOperationException("Cloud snapshot capture metadata does not match its backup set.");
            var store = RequireProperty(snapshot.Payload, "store");
            var storeSyncId = GetString(store, nameof(Store.SyncId));
            if (string.IsNullOrWhiteSpace(storeSyncId) || !storeSyncIds.Add(storeSyncId))
                throw new InvalidOperationException("Cloud restore contains a missing or duplicate store sync ID.");
            var actualRows = CountSnapshotRows(snapshot.Payload);
            if (actualRows != snapshot.RowCount)
                throw new InvalidOperationException("Cloud snapshot row count validation failed.");
            var digestBytes = SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.PayloadJson));
            var digest = Convert.ToBase64String(digestBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            if (string.IsNullOrWhiteSpace(snapshot.Sha256) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(digest), Encoding.ASCII.GetBytes(snapshot.Sha256)))
                throw new InvalidOperationException("Cloud snapshot integrity validation failed.");
        }
    }

    private static long CountSnapshotRows(JsonElement payload)
    {
        long count = 1;
        var entities = RequireProperty(payload, "entities");
        if (entities.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Cloud snapshot entity collection is invalid.");
        foreach (var property in entities.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"Cloud snapshot collection {property.Name} is invalid.");
            count += property.Value.GetArrayLength();
        }
        return count;
    }

    private async Task<IReadOnlyList<CloudStore>> GetCloudStoresAsync(
        CloudCredential credential,
        CancellationToken cancellationToken)
    {
        credential = await EnsureFreshAccessTokenAsync(credential, cancellationToken);
        var response = await GetAsync<CloudStoreListResponse>(credential, "/v1/stores", cancellationToken);
        return response.Stores;
    }

    private async Task<CloudCredential> RequireCredentialAsync(CancellationToken cancellationToken)
        => await _credentials.LoadAsync(cancellationToken)
           ?? throw new InvalidOperationException("Connect a cloud account before synchronizing.");

    private async Task<CloudCredential> EnsureFreshAccessTokenAsync(
        CloudCredential credential,
        CancellationToken cancellationToken)
    {
        if (credential.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return credential;
        var refreshed = await PostWithoutTokenAsync<CloudAuthResponse>(credential.Endpoint, "/v1/auth/refresh", new
        {
            refreshToken = credential.RefreshToken,
            deviceKey = credential.DeviceKey
        }, cancellationToken);
        if (refreshed.Owner == null || refreshed.Device == null || refreshed.Tokens == null)
            throw new InvalidOperationException("The cloud API returned an incomplete refresh response.");
        credential.OwnerId = refreshed.Owner.Id;
        credential.Email = refreshed.Owner.Email;
        credential.DisplayName = refreshed.Owner.DisplayName;
        credential.DeviceId = refreshed.Device.Id;
        credential.DeviceName = refreshed.Device.Name;
        credential.AccessToken = refreshed.Tokens.AccessToken;
        credential.RefreshToken = refreshed.Tokens.RefreshToken;
        credential.AccessTokenExpiresAt = refreshed.Tokens.ExpiresAt;
        await _credentials.SaveAsync(credential, cancellationToken);
        return credential;
    }

    private static async Task<T> PostAsync<T>(
        CloudCredential credential,
        string path,
        object payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, credential.Endpoint + path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        return await SendAsync<T>(request, cancellationToken);
    }

    private static async Task<T> PostWithoutTokenAsync<T>(
        string endpoint,
        string path,
        object payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await SendAsync<T>(request, cancellationToken);
    }

    private static async Task<T> GetAsync<T>(
        CloudCredential credential,
        string path,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, credential.Endpoint + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        return await SendAsync<T>(request, cancellationToken);
    }

    private static async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw CreateApiException(response.StatusCode, json);
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                   ?? throw new InvalidOperationException("The cloud API returned an empty response.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The cloud API returned an invalid response.", ex);
        }
    }

    private static Exception CreateApiException(HttpStatusCode statusCode, string json)
    {
        try
        {
            var error = JsonSerializer.Deserialize<CloudApiError>(json, JsonOptions);
            if (!string.IsNullOrWhiteSpace(error?.Error)) return new InvalidOperationException(error.Error);
        }
        catch (JsonException) { }
        return new InvalidOperationException($"Cloud API request failed ({(int)statusCode} {statusCode}).");
    }

    private static int ChangePriority(PullChange change)
    {
        var priority = change.EntityType switch
        {
            nameof(Store) => 0,
            nameof(User) or nameof(Category) or nameof(Customer) or nameof(Supplier) or
                nameof(Tax) or nameof(Discount) or nameof(Setting) => 10,
            nameof(Product) => 20,
            nameof(CashSession) => 30,
            nameof(PurchaseDocument) or nameof(Sale) or nameof(StockTransfer) => 40,
            nameof(PurchaseItem) or nameof(SaleItem) or nameof(SalePayment) or nameof(CashMovement) or nameof(StockTransferItem) => 50,
            nameof(StockTransaction) => 60,
            _ => 100
        };
        return string.Equals(change.Operation, "delete", StringComparison.OrdinalIgnoreCase)
            ? 1000 - priority
            : priority;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value)) return true;
        if (name.Length > 0)
        {
            var camel = char.ToLowerInvariant(name[0]) + name[1..];
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(camel, out value)) return true;
        }
        value = default;
        return false;
    }

    private static JsonElement RequireProperty(JsonElement element, string name)
        => TryGetProperty(element, name, out var value)
            ? value
            : throw new InvalidOperationException($"Cloud snapshot is missing {name}.");

    private static string? GetString(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static long GetLong(JsonElement element, string name, long fallback)
    {
        if (!TryGetProperty(element, name, out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return long.TryParse(value.ToString(), out number) ? number : fallback;
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var parsed)) return parsed;
        return DateTimeOffset.TryParse(value.ToString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed)
            ? parsed
            : null;
    }

    private static bool? GetBoolean(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        return bool.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static int ReadEntityId(StoreScopedEntity entity)
        => (int)(entity.GetType().GetProperty("Id")?.GetValue(entity) ?? 0);

    private static string Limit(string? value, int maximum)
    {
        var text = value ?? string.Empty;
        return text.Length <= maximum ? text : text[..maximum];
    }

    private sealed class PushResponse
    {
        public bool Committed { get; set; }
        public List<PushResult> Results { get; set; } = new();
    }

    private sealed class PushResult
    {
        public string ChangeId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public long CloudVersion { get; set; }
        public string Operation { get; set; } = string.Empty;
        public JsonElement Payload { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    private sealed class PullResponse
    {
        public long NextCursor { get; set; }
        public bool HasMore { get; set; }
        public List<PullChange> Changes { get; set; } = new();
    }

    private sealed class PullChange
    {
        public long Cursor { get; set; }
        public string ChangeId { get; set; } = string.Empty;
        public string OperationId { get; set; } = string.Empty;
        public string EntityType { get; set; } = string.Empty;
        public string EntitySyncId { get; set; } = string.Empty;
        public long CloudVersion { get; set; }
        public long EntityVersion { get; set; }
        public string Operation { get; set; } = string.Empty;
        public JsonElement Payload { get; set; }
        public string OriginDeviceId { get; set; } = string.Empty;
    }

    private sealed class CloudStoreListResponse
    {
        public List<CloudStore> Stores { get; set; } = new();
    }

    private sealed class CloudStore
    {
        public string SyncId { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    private sealed class SnapshotSetDownload
    {
        public string BackupSetId { get; set; } = string.Empty;
        public DateTimeOffset CapturedAt { get; set; }
        public List<SnapshotDownload> Snapshots { get; set; } = new();
    }

    private sealed class SnapshotDownload
    {
        public string SnapshotId { get; set; } = string.Empty;
        public string BackupSetId { get; set; } = string.Empty;
        public DateTimeOffset CapturedAt { get; set; }
        public long Version { get; set; }
        public int SchemaVersion { get; set; }
        public string AppVersion { get; set; } = string.Empty;
        public long RowCount { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public long SyncCursor { get; set; }
        public string PayloadJson { get; set; } = string.Empty;
        public JsonElement Payload { get; set; }
    }

    private sealed class CloudApiError
    {
        public string Error { get; set; } = string.Empty;
    }

    private sealed class CloudAuthResponse
    {
        public CloudOwner? Owner { get; set; }
        public CloudDevice? Device { get; set; }
        public CloudTokens? Tokens { get; set; }
    }

    private sealed class CloudOwner
    {
        public string Id { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
    }

    private sealed class CloudDevice
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    private sealed class CloudTokens
    {
        public string AccessToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
