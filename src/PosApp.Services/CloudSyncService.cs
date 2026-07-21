using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Data;

namespace PosApp.Services;

public sealed class CloudSyncService : ICloudSyncService, IDisposable
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly CloudApiClient _api;
    private readonly CloudSessionManager _session;
    private readonly SyncRecordApplier _applier;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly object _lifecycleGate = new();
    private CancellationTokenSource? _periodicCancellation;
    private Task? _periodicTask;
    private CloudSyncStatus _status = new();

    public CloudSyncService(
        IDbContextFactory<AppDbContext> dbFactory,
        CloudApiClient api,
        CloudSessionManager session,
        SyncRecordApplier applier)
    {
        _dbFactory = dbFactory;
        _api = api;
        _session = session;
        _applier = applier;
    }

    public event EventHandler<CloudSyncStatus>? StatusChanged;
    public CloudSyncStatus CurrentStatus => _status;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_session.Account == null) await _session.InitializeAsync(cancellationToken);
        lock (_lifecycleGate)
        {
            if (_periodicTask != null) return;
            _periodicCancellation = new CancellationTokenSource();
            // A periodic loop started during sign-in must not retain that
            // sign-in's AsyncLocal diagnostic ID after the UI attempt ends.
            using (CloudDiagnosticLogger.SuppressScope())
                _periodicTask = RunPeriodicAsync(_periodicCancellation.Token);
            NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
            SyncCaptureContext.OutboxChanged += OutboxChanged;
        }
        await RefreshStatusAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? task;
        lock (_lifecycleGate)
        {
            NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
            SyncCaptureContext.OutboxChanged -= OutboxChanged;
            _periodicCancellation?.Cancel();
            task = _periodicTask;
            _periodicTask = null;
            _periodicCancellation = null;
        }
        if (task != null)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
        }
    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        await _syncGate.WaitAsync(cancellationToken);
        _syncGate.Release();
    }

    public async Task<CloudSyncStatus> SyncNowAsync(
        bool userInitiated = false,
        CancellationToken cancellationToken = default)
    {
        // A startup/background synchronization may already own the gate when
        // first-run onboarding requests its required complete download. A
        // user-initiated call must wait for that operation and then perform its
        // own verified cycle; returning the previous status after three seconds
        // can make a successful sign-in look like an incomplete migration.
        if (userInitiated)
            await _syncGate.WaitAsync(cancellationToken);
        else if (!await _syncGate.WaitAsync(TimeSpan.Zero, cancellationToken))
            return CurrentStatus;
        var currentStage = "gate_acquired";
        try
        {
            if (CloudDiagnosticLogger.HasActiveAttempt)
                await CloudDiagnosticLogger.WriteAsync("sync.gate_acquired", details:
                    new Dictionary<string, object?> { ["userInitiated"] = userInitiated });

            var account = _session.Account;
            currentStage = "session_validation";
            if (!_session.IsSignedIn || account == null)
            {
                var signedOut = await SetStateAsync("signed_out", false, "AUTH_REQUIRED", null,
                    cancellationToken);
                await WriteStatusDiagnosticsAsync("sync.session_unavailable", signedOut, "blocked",
                    cancellationToken);
                return signedOut;
            }
            currentStage = "cursor_load";
            await LoadStoredCursorAsync(account, cancellationToken);
            if (account.IsDeviceRevoked)
            {
                var revoked = await SetStateAsync("revoked", false, "DEVICE_REVOKED", null,
                    cancellationToken);
                await WriteStatusDiagnosticsAsync("sync.device_revoked", revoked, "blocked",
                    cancellationToken);
                return revoked;
            }
            if (account.RequiresReconciliation)
            {
                var reconciliation = await SetStateAsync("reconciliation_required", false,
                    "RESTORE_RECONCILIATION_REQUIRED", null, cancellationToken);
                await WriteStatusDiagnosticsAsync("sync.reconciliation_required", reconciliation, "blocked",
                    cancellationToken);
                return reconciliation;
            }
            // Windows network-availability notifications are only a scheduling
            // hint. They can report offline while HTTPS to the Worker is already
            // usable, so let the bounded API request determine connectivity.
            currentStage = "interrupted_upload_recovery";
            await RecoverInterruptedUploadsAsync(account, cancellationToken);
            currentStage = "sync_state_update";
            var syncing = await SetStateAsync("syncing", true, null, null, cancellationToken);
            if (CloudDiagnosticLogger.HasActiveAttempt)
                await WriteStatusDiagnosticsAsync("sync.started", syncing, "started", cancellationToken);
            currentStage = "server_compatibility_check";
            var meta = await _api.GetAuthorizedAsync<ServerMetaEnvelope>("/api/v1/meta", cancellationToken);
            if (meta.ApiVersion != CloudProtocol.ApiVersion ||
                meta.MinimumClientSchemaVersion > CloudProtocol.ClientSchemaVersion ||
                meta.SchemaVersion < CloudProtocol.ClientSchemaVersion)
                throw new CloudApiException("CLIENT_VERSION_INCOMPATIBLE",
                    "This PosApp version must be updated before it can synchronize.",
                    System.Net.HttpStatusCode.Conflict);

            if (CloudDiagnosticLogger.HasActiveAttempt)
                await CloudDiagnosticLogger.WriteAsync("sync.server_compatibility_validated", "success",
                    new Dictionary<string, object?>
                    {
                        ["serverApiVersion"] = meta.ApiVersion,
                        ["serverSchemaVersion"] = meta.SchemaVersion,
                        ["minimumClientSchemaVersion"] = meta.MinimumClientSchemaVersion
                    });

            if (!account.RequiresReconciliation)
            {
                currentStage = "push_pending_operations";
                await PushPendingAsync(account, cancellationToken);
            }

            currentStage = "pull_server_changes";
            var downloadedChanges = await PullChangesAsync(account, cancellationToken);
            account.LastSuccessfulSyncAtUtc = DateTime.UtcNow;
            account.UpdatedAtUtc = DateTime.UtcNow;
            currentStage = "persist_sync_completion";
            await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
            {
                db.SuppressSyncCapture = true;
                db.CloudAccountStates.Update(account);
                await db.SaveChangesAsync(cancellationToken);
            }
            _session.UpdateAccount(account);
            var completed = await SetStateAsync(
                account.RequiresReconciliation ? "reconciliation_required" : "up_to_date",
                false, account.RequiresReconciliation ? "RESTORE_RECONCILIATION_REQUIRED" : null,
                null, cancellationToken, downloadedChanges: downloadedChanges);
            await WriteStatusDiagnosticsAsync("sync.completed", completed,
                completed.PendingUploadCount == 0 && completed.ConflictCount == 0 ? "success" : "attention",
                cancellationToken);
            return completed;
        }
        catch (CloudApiException exception)
        {
            await CloudDiagnosticLogger.WriteAsync("sync.api_exception_captured", "error",
                new Dictionary<string, object?>
                {
                    ["failedStage"] = currentStage,
                    ["errorCode"] = exception.Code,
                    ["requestId"] = exception.RequestId,
                    ["httpStatus"] = (int)exception.StatusCode
                }, exception);
            await HandleSecurityStateAsync(exception, cancellationToken);
            var failure = await SetStateAsync(MapState(exception.Code), false, exception.Code, exception.Message,
                cancellationToken, exception.RequestId);
            await WriteStatusDiagnosticsAsync("sync.api_failed", failure, "error", cancellationToken, exception);
            return failure;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (CloudDiagnosticLogger.HasActiveAttempt)
                await CloudDiagnosticLogger.WriteAsync("sync.cancelled", "cancelled");
            throw;
        }
        catch (Exception exception)
        {
            await CloudDiagnosticLogger.WriteAsync("sync.unexpected_exception_captured", "error",
                new Dictionary<string, object?> { ["failedStage"] = currentStage }, exception);
            var failure = await SetStateAsync("error", false, "SYNC_FAILED",
                "Synchronization could not be completed. Local work is safe and will be retried.", cancellationToken);
            await WriteStatusDiagnosticsAsync("sync.unexpected_failure", failure, "error",
                cancellationToken, exception);
            return failure;
        }
        finally
        {
            _syncGate.Release();
        }
    }

    public async Task RetryFailedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var failed = await db.SyncOutboxOperations.Where(value =>
            value.Status == SyncOutboxStatus.Failed || value.Status == SyncOutboxStatus.Pending).ToListAsync(cancellationToken);
        foreach (var operation in failed)
        {
            operation.Status = SyncOutboxStatus.Pending;
            operation.NextAttemptAtUtc = null;
            operation.LastErrorCode = null;
            operation.LastErrorMessage = null;
        }
        db.SuppressSyncCapture = true;
        await db.SaveChangesAsync(cancellationToken);
        await SyncNowAsync(true, cancellationToken);
    }

    public async Task ResolveConflictAsync(
        long conflictId,
        SyncConflictStatus resolution,
        CancellationToken cancellationToken = default)
    {
        if (resolution is not (SyncConflictStatus.KeepLocal or SyncConflictStatus.UseServer))
            throw new ArgumentException("Choose either the local or server version.", nameof(resolution));

        SyncChangeDto? serverChange = null;
        string? conflictRecordId = null;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            db.SuppressSyncCapture = true;
            var conflict = await db.SyncConflicts.SingleAsync(value => value.Id == conflictId, cancellationToken);
            conflictRecordId = conflict.ConflictId;
            var pending = await db.SyncOutboxOperations.FirstOrDefaultAsync(value =>
                value.OperationId == conflict.OperationId, cancellationToken);
            if (resolution == SyncConflictStatus.KeepLocal)
            {
                if (pending == null) throw new InvalidOperationException("The local change is no longer available.");
                pending.OperationId = Guid.NewGuid().ToString("D");
                pending.IdempotencyKey = Guid.NewGuid().ToString("N");
                pending.BaseVersion = conflict.ServerVersion;
                pending.Status = SyncOutboxStatus.Pending;
                pending.AttemptCount = 0;
                pending.NextAttemptAtUtc = null;
            }
            else
            {
                if (pending != null) pending.Status = SyncOutboxStatus.Synchronized;
                var account = await db.CloudAccountStates.AsNoTracking().SingleAsync(cancellationToken);
                serverChange = new SyncChangeDto
                {
                    EntityType = conflict.EntityType,
                    RecordId = conflict.RecordId,
                    StoreId = conflict.ServerStoreId ?? account.CurrentStoreId,
                    Version = conflict.ServerVersion,
                    UpdatedAtUtc = conflict.ServerUpdatedAtUtc ?? DateTime.UtcNow,
                    DeletedAtUtc = conflict.ServerDeletedAtUtc,
                    LastModifiedDeviceId = conflict.ServerLastModifiedDeviceId ?? "conflict-resolution",
                    Payload = JsonDocument.Parse(conflict.ServerPayloadJson).RootElement.Clone()
                };
            }
            conflict.Status = resolution;
            conflict.ResolvedAtUtc = DateTime.UtcNow;
            conflict.ResolvedByUserId = _session.User?.Id;
            await db.SaveChangesAsync(cancellationToken);
        }

        if (serverChange != null && _session.Account != null)
            await _applier.ApplyAsync(new[] { serverChange }, _session.Account.TenantId,
                _session.Account.DeviceId, cancellationToken);
        if (_session.Account != null)
            await _api.PostAuthorizedAsync<OkEnvelope>("/api/v1/audit/events", new
            {
                action = "sync.conflict_resolved",
                affectedType = "sync_conflict",
                affectedId = conflictRecordId,
                storeId = _session.Account.CurrentStoreId
            }, cancellationToken);
        await SyncNowAsync(true, cancellationToken);
    }

    private async Task PushPendingAsync(CloudAccountState account, CancellationToken cancellationToken)
    {
        for (var batchNumber = 0; batchNumber < 20; batchNumber++)
        {
            List<SyncOutboxOperation> pending;
            await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
            {
                var now = DateTime.UtcNow;
                pending = await db.SyncOutboxOperations.Where(value =>
                        value.CreatedByUserId == account.CurrentCloudUserId &&
                        (value.StoreId == null || value.StoreId == account.CurrentStoreId) &&
                        value.Status == SyncOutboxStatus.Pending &&
                        (value.NextAttemptAtUtc == null || value.NextAttemptAtUtc <= now))
                    .OrderBy(value => value.Id)
                    .Take(CloudProtocol.MaxPushBatch * 2)
                    .ToListAsync(cancellationToken);
                var completingSavedSales = pending.Where(IsCompletingSavedSale)
                    .Select(value => value.RecordId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                pending = pending.OrderBy(value => PushDependencyOrder(value, completingSavedSales))
                    .ThenBy(value => value.Id)
                    .Take(CloudProtocol.MaxPushBatch)
                    .ToList();
                if (pending.Count == 0) return;

                if (CloudDiagnosticLogger.HasActiveAttempt)
                    await CloudDiagnosticLogger.WriteAsync("sync.push_batch_started", "started",
                        new Dictionary<string, object?>
                        {
                            ["batchNumber"] = batchNumber + 1,
                            ["operationCount"] = pending.Count,
                            ["entitySummary"] = GroupSummary(pending.Select(value => value.EntityType)),
                            ["maximumPreviousAttempts"] = pending.Max(value => value.AttemptCount)
                        });
                foreach (var operation in pending)
                {
                    operation.Status = SyncOutboxStatus.Uploading;
                    operation.LastAttemptAtUtc = now;
                    operation.AttemptCount++;
                }
                db.SuppressSyncCapture = true;
                await db.SaveChangesAsync(cancellationToken);
            }

            SyncPushResponse response;
            try
            {
                var request = new SyncPushRequest
                {
                    DeviceId = account.DeviceId,
                    StoreId = account.CurrentStoreId,
                    Operations = pending.Select(value => new SyncOperationDto
                    {
                        OperationId = value.OperationId,
                        IdempotencyKey = value.IdempotencyKey,
                        EntityType = value.EntityType,
                        RecordId = value.RecordId,
                        StoreId = value.StoreId,
                        Operation = value.Operation == SyncOperationKind.Delete ? "delete" : "upsert",
                        BaseVersion = value.BaseVersion,
                        Payload = JsonDocument.Parse(value.PayloadJson).RootElement.Clone(),
                        ClientTimestampUtc = SyncWireTimestamp.NormalizeUtc(value.CreatedAtUtc)
                    }).ToArray()
                };
                response = await _api.PostAuthorizedAsync<SyncPushResponse>("/api/v1/sync/push", request,
                    cancellationToken);
                await PersistCursorAsync(account, pulled: false, pushed: true,
                    cancellationToken: cancellationToken);

                if (CloudDiagnosticLogger.HasActiveAttempt)
                    await CloudDiagnosticLogger.WriteAsync("sync.push_batch_received", "success",
                        new Dictionary<string, object?>
                        {
                            ["batchNumber"] = batchNumber + 1,
                            ["resultCount"] = response.Results.Count,
                            ["accepted"] = response.Results.Count(value => value.Accepted),
                            ["duplicates"] = response.Results.Count(value => value.Duplicate),
                            ["rejected"] = response.Results.Count(value => !value.Accepted && !value.Duplicate),
                            ["errorCodeSummary"] = GroupSummary(response.Results
                                .Where(value => !string.IsNullOrWhiteSpace(value.ErrorCode))
                                .Select(value => value.ErrorCode!)),
                            ["serverCursor"] = response.ServerCursor,
                            ["requestId"] = response.RequestId
                        });
            }
            catch (Exception pushException)
            {
                if (CloudDiagnosticLogger.HasActiveAttempt)
                    await CloudDiagnosticLogger.WriteAsync("sync.push_batch_failed", "error",
                        new Dictionary<string, object?>
                        {
                            ["batchNumber"] = batchNumber + 1,
                            ["operationCount"] = pending.Count,
                            ["entitySummary"] = GroupSummary(pending.Select(value => value.EntityType)),
                            ["errorCode"] = (pushException as CloudApiException)?.Code,
                            ["requestId"] = (pushException as CloudApiException)?.RequestId
                        }, pushException);
                try
                {
                    await ResetUploadingAsync(pending, cancellationToken);
                }
                catch (Exception recoveryException)
                {
                    // Cleanup must never replace the actual Worker/API failure.
                    // RecoverInterruptedUploadsAsync will safely reset these rows
                    // before the next synchronization attempt if this fallback
                    // itself cannot access SQLite.
                    await CloudDiagnosticLogger.WriteAsync("sync.upload_reset_failed", "error",
                        new Dictionary<string, object?>
                        {
                            ["batchNumber"] = batchNumber + 1,
                            ["operationCount"] = pending.Count,
                            ["originalExceptionType"] = pushException.GetType().FullName
                        }, recoveryException);
                }
                throw;
            }

            await using var updateDb = await _dbFactory.CreateDbContextAsync(cancellationToken);
            updateDb.SuppressSyncCapture = true;
            foreach (var result in response.Results)
            {
                var operation = await updateDb.SyncOutboxOperations.SingleOrDefaultAsync(value =>
                    value.OperationId == result.OperationId, cancellationToken);
                if (operation == null) continue;
                var identity = await updateDb.SyncIdentities.SingleOrDefaultAsync(value =>
                    value.RecordId == operation.RecordId, cancellationToken);
                if (result.Accepted || result.Duplicate)
                {
                    operation.Status = SyncOutboxStatus.Synchronized;
                    operation.LastErrorCode = null;
                    operation.LastErrorMessage = null;
                    if (identity != null) identity.ServerVersion = result.ServerVersion;
                }
                else if (result.ErrorCode == "VERSION_CONFLICT")
                {
                    operation.Status = SyncOutboxStatus.Conflict;
                    updateDb.SyncConflicts.Add(new SyncConflict
                    {
                        EntityType = operation.EntityType,
                        RecordId = operation.RecordId,
                        OperationId = operation.OperationId,
                        LocalBaseVersion = operation.BaseVersion,
                        ServerVersion = result.ServerVersion,
                        LocalPayloadJson = operation.PayloadJson,
                        ServerPayloadJson = result.ServerPayload == null
                            ? "{}"
                            : JsonSerializer.Serialize(result.ServerPayload),
                        ServerStoreId = result.ServerStoreId,
                        ServerUpdatedAtUtc = result.ServerUpdatedAtUtc,
                        ServerDeletedAtUtc = result.ServerDeletedAtUtc,
                        ServerLastModifiedDeviceId = result.ServerLastModifiedDeviceId
                    });
                }
                else
                {
                    operation.Status = IsPermanentOperationError(result.ErrorCode)
                        ? SyncOutboxStatus.Failed
                        : SyncOutboxStatus.Pending;
                    operation.LastErrorCode = result.ErrorCode;
                    operation.LastErrorMessage = result.Message;
                    operation.NextAttemptAtUtc = DateTime.UtcNow + SyncBackoffPolicy.ForAttempt(operation.AttemptCount);
                }
            }
            await updateDb.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<int> PullChangesAsync(CloudAccountState account, CancellationToken cancellationToken)
    {
        var downloaded = 0;
        for (var page = 0; page < 200; page++)
        {
            var response = await _api.GetAuthorizedAsync<SyncPullResponse>(
                $"/api/v1/sync/pull?cursor={account.LastServerCursor}&storeId={Uri.EscapeDataString(account.CurrentStoreId)}&limit={CloudProtocol.MaxPullBatch}",
                cancellationToken);
            var applied = await _applier.ApplyAsync(response.Changes, account.TenantId,
                account.DeviceId, cancellationToken);
            downloaded += applied;

            if (CloudDiagnosticLogger.HasActiveAttempt)
                await CloudDiagnosticLogger.WriteAsync("sync.pull_page_applied", "success",
                    new Dictionary<string, object?>
                    {
                        ["pageNumber"] = page + 1,
                        ["receivedChanges"] = response.Changes.Count,
                        ["appliedChanges"] = applied,
                        ["entitySummary"] = GroupSummary(response.Changes.Select(value => value.EntityType)),
                        ["nextCursor"] = response.NextCursor,
                        ["hasMore"] = response.HasMore,
                        ["requestId"] = response.RequestId
                    });
            account.LastServerCursor = response.NextCursor;
            await PersistCursorAsync(account, pulled: true, pushed: false,
                cancellationToken: cancellationToken);
            if (!response.HasMore) return downloaded;
        }
        throw new CloudApiException("PARTIAL_BATCH_FAILURE",
            "More synchronized changes remain on the server. Run synchronization again to continue safely.",
            System.Net.HttpStatusCode.ServiceUnavailable);
    }

    private async Task WriteStatusDiagnosticsAsync(
        string stage,
        CloudSyncStatus status,
        string outcome,
        CancellationToken cancellationToken,
        Exception? exception = null)
    {
        if (!CloudDiagnosticLogger.HasActiveAttempt && exception == null) return;

        IReadOnlyDictionary<string, object?>? details = null;
        if (status.PendingUploadCount > 0 || status.ConflictCount > 0 || exception != null)
        {
            try { details = await BuildQueueDiagnosticSummaryAsync(cancellationToken); }
            catch { /* A failed diagnostic query must not change sync behavior. */ }
        }

        await CloudDiagnosticLogger.WriteStatusAsync(stage, status, outcome, details, exception);
    }

    private async Task<IReadOnlyDictionary<string, object?>> BuildQueueDiagnosticSummaryAsync(
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var outbox = await db.SyncOutboxOperations.AsNoTracking()
            .Where(value => value.Status == SyncOutboxStatus.Pending ||
                            value.Status == SyncOutboxStatus.Uploading ||
                            value.Status == SyncOutboxStatus.Failed ||
                            value.Status == SyncOutboxStatus.Conflict)
            .Select(value => new
            {
                value.EntityType,
                value.Status,
                value.LastErrorCode,
                value.AttemptCount
            })
            .ToListAsync(cancellationToken);
        var conflicts = await db.SyncConflicts.AsNoTracking()
            .Where(value => value.Status == SyncConflictStatus.Unresolved)
            .Select(value => value.EntityType)
            .ToListAsync(cancellationToken);

        return new Dictionary<string, object?>
        {
            ["outboxStatusSummary"] = GroupSummary(outbox.Select(value => value.Status.ToString())),
            ["outboxEntitySummary"] = GroupSummary(outbox.Select(value => value.EntityType)),
            ["outboxErrorCodeSummary"] = GroupSummary(outbox
                .Where(value => !string.IsNullOrWhiteSpace(value.LastErrorCode))
                .Select(value => value.LastErrorCode!)),
            ["maximumAttemptCount"] = outbox.Count == 0 ? 0 : outbox.Max(value => value.AttemptCount),
            ["conflictEntitySummary"] = GroupSummary(conflicts)
        };
    }

    private static string GroupSummary(IEnumerable<string> values)
        => string.Join(",", values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}:{group.Count()}"));

    private async Task ResetUploadingAsync(
        IReadOnlyList<SyncOutboxOperation> operations,
        CancellationToken cancellationToken)
    {
        await SyncOutboxUploadRecovery.ResetAsync(
            _dbFactory,
            operations.Select(value => value.OperationId),
            cancellationToken);
    }

    private async Task RecoverInterruptedUploadsAsync(
        CloudAccountState account,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var interrupted = await db.SyncOutboxOperations.Where(value =>
            value.CreatedByUserId == account.CurrentCloudUserId &&
            value.Status == SyncOutboxStatus.Uploading).ToListAsync(cancellationToken);
        if (interrupted.Count == 0) return;
        foreach (var operation in interrupted)
        {
            operation.Status = SyncOutboxStatus.Pending;
            operation.NextAttemptAtUtc = null;
        }
        db.SuppressSyncCapture = true;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task LoadStoredCursorAsync(
        CloudAccountState account,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var cursor = await db.SyncCursorStates.AsNoTracking().SingleOrDefaultAsync(value =>
            value.TenantId == account.TenantId && value.StoreId == account.CurrentStoreId &&
            value.DeviceId == account.DeviceId, cancellationToken);
        if (cursor != null) account.LastServerCursor = cursor.Cursor;
    }

    private async Task PersistCursorAsync(
        CloudAccountState account,
        bool pulled,
        bool pushed,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var cursor = await db.SyncCursorStates.SingleOrDefaultAsync(value =>
            value.TenantId == account.TenantId && value.StoreId == account.CurrentStoreId &&
            value.DeviceId == account.DeviceId, cancellationToken);
        if (cursor == null)
        {
            cursor = new SyncCursorState
            {
                TenantId = account.TenantId,
                StoreId = account.CurrentStoreId,
                DeviceId = account.DeviceId
            };
            db.SyncCursorStates.Add(cursor);
        }
        cursor.Cursor = Math.Max(cursor.Cursor, account.LastServerCursor);
        if (pulled) cursor.LastPullAtUtc = DateTime.UtcNow;
        if (pushed) cursor.LastPushAtUtc = DateTime.UtcNow;
        db.SuppressSyncCapture = true;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task HandleSecurityStateAsync(CloudApiException exception, CancellationToken cancellationToken)
    {
        if (exception.Code is not ("DEVICE_REVOKED" or "USER_DISABLED" or "SESSION_REVOKED" or
            "ORGANIZATION_DISABLED" or "STORE_DISABLED" or "REFRESH_TOKEN_REVOKED" or
            "REFRESH_TOKEN_EXPIRED" or "REFRESH_TOKEN_REUSE")) return;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var state = await db.CloudAccountStates.SingleOrDefaultAsync(cancellationToken);
        if (state != null)
        {
            state.IsDeviceRevoked = exception.Code == "DEVICE_REVOKED";
            state.IsEnabled = false;
            state.UpdatedAtUtc = DateTime.UtcNow;
            db.SuppressSyncCapture = true;
            await db.SaveChangesAsync(cancellationToken);
            _session.UpdateAccount(state);
        }
    }

    private async Task<CloudSyncStatus> SetStateAsync(
        string state,
        bool syncing,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken,
        string? requestId = null,
        int downloadedChanges = 0)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await db.CloudAccountStates.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var pending = await db.SyncOutboxOperations.CountAsync(value =>
            value.Status == SyncOutboxStatus.Pending ||
            value.Status == SyncOutboxStatus.Uploading ||
            value.Status == SyncOutboxStatus.Failed,
            cancellationToken);
        var conflicts = await db.SyncConflicts.CountAsync(value => value.Status == SyncConflictStatus.Unresolved,
            cancellationToken);
        _status = new CloudSyncStatus
        {
            IsSignedIn = _session.IsSignedIn,
            IsOnline = NetworkInterface.GetIsNetworkAvailable(),
            IsSyncing = syncing,
            IsDeviceRevoked = account?.IsDeviceRevoked == true,
            RequiresReconciliation = account?.RequiresReconciliation == true,
            State = state,
            LastErrorCode = errorCode,
            LastErrorMessage = errorMessage,
            LastRequestId = requestId,
            LastSuccessfulSyncAtUtc = account?.LastSuccessfulSyncAtUtc,
            PendingUploadCount = pending,
            ConflictCount = conflicts,
            DownloadedChangeCount = downloadedChanges,
            Cursor = account?.LastServerCursor ?? 0
        };
        StatusChanged?.Invoke(this, _status);
        return _status;
    }

    private Task<CloudSyncStatus> RefreshStatusAsync(CancellationToken cancellationToken)
        => SetStateAsync(_session.IsSignedIn ? "ready" : "signed_out", false, null, null, cancellationToken);

    private async Task RunPeriodicAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await SyncNowAsync(false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs eventArgs)
    {
        if (eventArgs.IsAvailable) _ = SyncNowAsync(false, CancellationToken.None);
        else _ = SetStateAsync("offline", false, "NETWORK_UNAVAILABLE", null, CancellationToken.None);
    }

    private void OutboxChanged(object? sender, EventArgs eventArgs)
    {
        if (NetworkInterface.GetIsNetworkAvailable()) _ = SyncNowAsync(false, CancellationToken.None);
    }

    private static string MapState(string code) => code switch
    {
        "NETWORK_UNAVAILABLE" or "NETWORK_TIMEOUT" => "offline",
        "DEVICE_REVOKED" => "revoked",
        "AUTH_REQUIRED" or "SESSION_REVOKED" or "REFRESH_TOKEN_EXPIRED" or "REFRESH_TOKEN_REVOKED" or
            "REFRESH_TOKEN_REUSE" or "USER_DISABLED" or "ORGANIZATION_DISABLED" or "STORE_DISABLED" => "session_expired",
        "CLIENT_VERSION_INCOMPATIBLE" or "SERVER_VERSION_INCOMPATIBLE" => "upgrade_required",
        _ => "error"
    };

    private static bool IsPermanentOperationError(string? code) => code is
        "VALIDATION_ERROR" or "PERMISSION_DENIED" or "DUPLICATE_BUSINESS_RECORD" or
        "PAYMENT_TOTAL_EXCEEDED" or "INVENTORY_SOURCE_MISMATCH" or "IMMUTABLE_TRANSACTION" or
        "REFUND_QUANTITY_EXCEEDED" or "CATALOG_VERSION_UNAVAILABLE" or "SERVER_SCHEMA_MISMATCH" or
        "STORE_ACCESS_DENIED" or "CROSS_TENANT_ACCESS_DENIED" or "RECORD_NOT_FOUND";

    private static int PushDependencyOrder(
        SyncOutboxOperation operation,
        IReadOnlySet<string> completingSavedSales)
    {
        // A synchronized saved sale already exists in Turso. Its old/new draft
        // lines may be spread across several bounded batches, so apply them while
        // the server header is still Suspended and finalize the header afterward.
        if (operation.EntityType == "sale_items" &&
            TryPayloadRecordId(operation.PayloadJson, "saleRecordId") is { } saleId &&
            completingSavedSales.Contains(saleId))
            return 45;
        return SyncEntityRegistry.TryGet(operation.EntityType, out var descriptor)
            ? descriptor.DependencyOrder
            : int.MaxValue;
    }

    private static bool IsCompletingSavedSale(SyncOutboxOperation operation)
    {
        if (operation.EntityType != "sales" || operation.Operation != SyncOperationKind.Upsert ||
            operation.BaseVersion <= 0) return false;
        try
        {
            using var document = JsonDocument.Parse(operation.PayloadJson);
            return document.RootElement.TryGetProperty("status", out var status) &&
                   status.TryGetInt32(out var value) && value == 0;
        }
        catch (JsonException) { return false; }
    }

    private static string? TryPayloadRecordId(string payloadJson, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(propertyName, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    public void Dispose()
    {
        _periodicCancellation?.Cancel();
        _periodicCancellation?.Dispose();
        _syncGate.Dispose();
    }

    private sealed class ServerMetaEnvelope
    {
        public int ApiVersion { get; set; }
        public int SchemaVersion { get; set; }
        public int MinimumClientSchemaVersion { get; set; }
    }

    private sealed class OkEnvelope { public bool Ok { get; set; } }
}

internal static class SyncOutboxUploadRecovery
{
    internal static async Task ResetAsync(
        IDbContextFactory<AppDbContext> dbFactory,
        IEnumerable<string> operationIds,
        CancellationToken cancellationToken)
    {
        var ids = operationIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0) return;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // Use Enumerable.Contains explicitly. With LangVersion=latest on some
        // .NET 8 Windows runners, ids.Contains(...) binds to
        // MemoryExtensions.Contains(ReadOnlySpan<T>, T). EF Core cannot place
        // that ref-struct overload in an expression tree and throws before SQL
        // is executed (ReadOnlySpan<T> violates the interpreter constraint).
        var rows = await db.SyncOutboxOperations
            .Where(value => Enumerable.Contains(ids, value.OperationId))
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            row.Status = SyncOutboxStatus.Pending;
            row.NextAttemptAtUtc = DateTime.UtcNow + SyncBackoffPolicy.ForAttempt(row.AttemptCount);
        }
        db.SuppressSyncCapture = true;
        await db.SaveChangesAsync(cancellationToken);
    }
}

internal static class SyncWireTimestamp
{
    internal static DateTime NormalizeUtc(DateTime timestamp)
        => timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp,
            DateTimeKind.Local => timestamp.ToUniversalTime(),
            // Microsoft.Data.Sqlite materializes a stored UTC DateTime with an
            // unspecified Kind. Its ticks already represent UTC, so converting
            // it as local time would shift the actual business timestamp.
            _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
        };
}

public static class SyncBackoffPolicy
{
    public static TimeSpan ForAttempt(int attempt)
    {
        var seconds = Math.Min(300, Math.Pow(2, Math.Clamp(attempt, 1, 8)));
        var jitter = Random.Shared.NextDouble() * Math.Min(10, seconds * 0.2);
        return TimeSpan.FromSeconds(seconds + jitter);
    }
}
