using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Data;

namespace PosApp.Services;

public sealed class CloudMigrationService : ICloudMigrationService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly CloudApiClient _api;
    private readonly CloudSessionManager _session;
    private readonly ICloudSyncService _sync;
    private readonly IBackupService _backup;

    public CloudMigrationService(
        IDbContextFactory<AppDbContext> dbFactory,
        CloudApiClient api,
        CloudSessionManager session,
        ICloudSyncService sync,
        IBackupService backup)
    {
        _dbFactory = dbFactory;
        _api = api;
        _session = session;
        _sync = sync;
        _backup = backup;
    }

    public async Task<CloudMigrationPreview> PreviewInitialMigrationAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_session.IsSignedIn)
            throw new InvalidOperationException("Sign in to the online account before migrating data.");
        var account = _session.Account ?? throw new InvalidOperationException("The online account is unavailable.");
        var statusUrl = $"/api/v1/sync/status?storeId={Uri.EscapeDataString(account.CurrentStoreId)}";
        var cloud = await _api.GetAuthorizedAsync<SyncStatusEnvelope>(statusUrl, cancellationToken);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var localCounts = InitialSyncOutboxBuilder.CountAll(db);
        var mayUpload = !cloud.InitialMigrationCompletedByDevice &&
                        (cloud.InitialMigrationOwnedByDevice ||
                         (cloud.InitialMigrationResumableByDevice && !cloud.InitialMigrationInProgress) ||
                         (cloud.BusinessDataEmpty && !cloud.InitialMigrationInProgress));
        return new CloudMigrationPreview
        {
            CloudBusinessDataIsEmpty = mayUpload,
            InitialMigrationCompletedByThisDevice = cloud.InitialMigrationCompletedByDevice,
            ResumableMigrationId = cloud.InitialMigrationResumableByDevice
                ? cloud.LatestDeviceMigrationId
                : null,
            LocalCounts = localCounts,
            CloudCounts = cloud.Counts,
            BlockingCode = mayUpload ? null : cloud.InitialMigrationCompletedByDevice
                ? "MIGRATION_ALREADY_COMPLETED"
                : cloud.InitialMigrationInProgress
                ? "MIGRATION_IN_PROGRESS"
                : "CLOUD_DATA_NOT_EMPTY",
            BlockingReason = mayUpload
                ? null
                : cloud.InitialMigrationCompletedByDevice
                    ? "This device already completed the initial migration. Its verified result can be resumed safely."
                : cloud.InitialMigrationInProgress
                    ? "Another administrator device is currently migrating this store."
                    : "The online organization already contains business data. Automatic merging is blocked to protect financial and inventory records."
        };
    }

    public async Task<CloudMigrationResult> UploadExistingDataAsync(
        CancellationToken cancellationToken = default)
    {
        var preview = await PreviewInitialMigrationAsync(cancellationToken);
        if (preview.InitialMigrationCompletedByThisDevice)
            return await RecoverCompletedMigrationAsync(preview, cancellationToken);
        if (!preview.CloudBusinessDataIsEmpty)
            throw new InvalidOperationException(preview.BlockingReason);

        var account = _session.Account ?? throw new InvalidOperationException("The online account is unavailable.");
        var lease = await _api.PostAuthorizedAsync<MigrationLeaseEnvelope>(
            "/api/v1/migrations/initial/start", new
            {
                storeId = account.CurrentStoreId,
                migrationId = preview.ResumableMigrationId
            }, cancellationToken);
        var backupPath = await _backup.CreateBackupAsync(retentionCount: null);
        IReadOnlyDictionary<string, int> queuedCounts;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var state = await db.CloudAccountStates.SingleAsync(cancellationToken);
            // Reaching this point means the administrator explicitly selected
            // the local snapshot, the Worker verified an empty organization,
            // and a fresh safety backup exists. Release the reconciliation gate
            // so the migration lease can upload its bounded snapshot.
            state.RequiresReconciliation = false;
            state.ReconciliationBackupPath ??= backupPath;
            var alreadyQueued = state.ActiveMigrationId == lease.MigrationId &&
                                state.ActiveMigrationStoreId == account.CurrentStoreId &&
                                state.IsMigrationSnapshotQueued;
            state.ActiveMigrationBackupPath = backupPath;
            if (alreadyQueued)
            {
                queuedCounts = InitialSyncOutboxBuilder.CountAll(db);
                db.SuppressSyncCapture = true;
                await db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                state.ActiveMigrationId = lease.MigrationId;
                state.ActiveMigrationStoreId = account.CurrentStoreId;
                state.IsMigrationSnapshotQueued = false;
                state.UpdatedAtUtc = DateTime.UtcNow;
                db.SuppressSyncCapture = true;
                await db.SaveChangesAsync(cancellationToken);
                queuedCounts = await InitialSyncOutboxBuilder.QueueAllAsync(
                    db, lease.MigrationId, cancellationToken);
            }
            var refreshedState = await db.CloudAccountStates.AsNoTracking().SingleAsync(cancellationToken);
            _session.UpdateAccount(refreshedState);
        }

        CloudSyncStatus status = new();
        for (var cycle = 0; cycle < 100; cycle++)
        {
            status = await _sync.SyncNowAsync(true, cancellationToken);
            var outbox = await GetMigrationOutboxStateAsync(cancellationToken);
            if (outbox.Remaining == 0) break;
            if (outbox.Blocked > 0 || !status.IsOnline || status.State is "offline" or "error" or "session_expired")
                throw new InvalidOperationException(
                    "The migration is incomplete. The local backup and pending operations were retained; use Retry after checking the sync details.");
            if (cycle == 99)
                throw new InvalidOperationException(
                    "The migration batch limit was reached. Pending operations remain safe; continue with Retry before using another device.");
        }

        var verified = await _api.GetAuthorizedAsync<SyncStatusEnvelope>(
            $"/api/v1/sync/status?storeId={Uri.EscapeDataString(account.CurrentStoreId)}", cancellationToken);
        var countMismatches = queuedCounts.Where(value =>
                !verified.Counts.TryGetValue(value.Key, out var cloudCount) || cloudCount < value.Value)
            .Select(value => value.Key)
            .ToArray();
        if (countMismatches.Length > 0)
            throw new InvalidOperationException(
                $"Cloud record-count verification failed for: {string.Join(", ", countMismatches)}. The backup was retained and no automatic merge will be attempted.");
        await _api.PostAuthorizedAsync<OkEnvelope>(
            "/api/v1/migrations/initial/finish", new { storeId = account.CurrentStoreId }, cancellationToken);
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var state = await db.CloudAccountStates.SingleAsync(cancellationToken);
            state.ActiveMigrationId = null;
            state.ActiveMigrationStoreId = null;
            state.IsMigrationSnapshotQueued = false;
            state.UpdatedAtUtc = DateTime.UtcNow;
            db.SuppressSyncCapture = true;
            await db.SaveChangesAsync(cancellationToken);
        }
        return new CloudMigrationResult
        {
            BackupPath = backupPath,
            UploadedCounts = queuedCounts,
            VerifiedCloudCounts = verified.Counts,
            ConflictCount = status.ConflictCount,
            CompletedAtUtc = DateTime.UtcNow
        };
    }

    private async Task<CloudMigrationResult> RecoverCompletedMigrationAsync(
        CloudMigrationPreview preview,
        CancellationToken cancellationToken)
    {
        var mismatches = preview.LocalCounts.Where(value =>
                !preview.CloudCounts.TryGetValue(value.Key, out var cloudCount) || cloudCount < value.Value)
            .Select(value => value.Key)
            .ToArray();
        if (mismatches.Length > 0)
            throw new InvalidOperationException(
                $"The server reports a completed migration, but record-count verification failed for: {string.Join(", ", mismatches)}. Keep the local backup and contact an administrator before retrying.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var state = await db.CloudAccountStates.SingleAsync(cancellationToken);
        var backupPath = state.ActiveMigrationBackupPath;
        state.ActiveMigrationId = null;
        state.ActiveMigrationStoreId = null;
        state.IsMigrationSnapshotQueued = false;
        state.UpdatedAtUtc = DateTime.UtcNow;
        db.SuppressSyncCapture = true;
        await db.SaveChangesAsync(cancellationToken);
        _session.UpdateAccount(state);
        return new CloudMigrationResult
        {
            BackupPath = backupPath ?? string.Empty,
            UploadedCounts = preview.LocalCounts,
            VerifiedCloudCounts = preview.CloudCounts,
            CompletedAtUtc = DateTime.UtcNow
        };
    }

    public async Task MarkRestoreRequiresReconciliationAsync(
        string safetyBackupPath,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var state = await db.CloudAccountStates.SingleOrDefaultAsync(cancellationToken);
        if (state == null || string.IsNullOrWhiteSpace(state.TenantId))
        {
            var recovered = await ReadCloudStateFromBackupAsync(safetyBackupPath, cancellationToken);
            if (recovered == null || string.IsNullOrWhiteSpace(recovered.TenantId)) return;
            if (state == null)
            {
                state = recovered;
                db.CloudAccountStates.Add(state);
            }
            else
            {
                CopyCloudLink(recovered, state);
            }
        }
        state.RequiresReconciliation = true;
        state.ReconciliationBackupPath = safetyBackupPath;
        state.LastServerCursor = 0;
        state.UpdatedAtUtc = DateTime.UtcNow;
        db.SuppressSyncCapture = true;
        await db.SaveChangesAsync(cancellationToken);
        _session.UpdateAccount(state);
    }

    public async Task AcceptServerAfterRestoreAsync(CancellationToken cancellationToken = default)
    {
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var state = await db.CloudAccountStates.SingleAsync(cancellationToken);
            if (!state.RequiresReconciliation)
                throw new InvalidOperationException("This database does not require restore reconciliation.");
            db.SuppressSyncCapture = true;
            db.BypassStoreFilter = true;
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                // "Use server" is deliberately a replacement operation. Keeping
                // restored transaction rows and then pulling UUID-backed cloud
                // rows would duplicate receipts, payments, and stock movements.
                // Keep these statements fixed rather than interpolating identifiers.
                // The list is internal and reviewed, and fixed SQL also lets the EF
                // analyzer verify that no untrusted value can enter the command.
                var deleteStatements = new[]
                {
                    "DELETE FROM \"CashMovements\"",
                    "DELETE FROM \"SalePayments\"",
                    "DELETE FROM \"SaleItems\"",
                    "DELETE FROM \"PurchaseItems\"",
                    "DELETE FROM \"StockTransactions\"",
                    "DELETE FROM \"Expenses\"",
                    "DELETE FROM \"Sales\"",
                    "DELETE FROM \"PurchaseDocuments\"",
                    "DELETE FROM \"CashSessions\"",
                    "DELETE FROM \"Products\"",
                    "DELETE FROM \"Categories\"",
                    "DELETE FROM \"Taxes\"",
                    "DELETE FROM \"Discounts\"",
                    "DELETE FROM \"Customers\"",
                    "DELETE FROM \"Suppliers\"",
                    "DELETE FROM \"SyncConflicts\"",
                    "DELETE FROM \"SyncOutboxOperations\"",
                    "DELETE FROM \"SyncCursorStates\"",
                    "DELETE FROM \"SyncIdentities\""
                };
                foreach (var statement in deleteStatements)
                    await db.Database.ExecuteSqlRawAsync(statement, cancellationToken);
                // Setup completion, onboarding preparation, and other `app:`
                // values belong to this installation and never come from Turso.
                // Retain them while replacing synchronized store settings.
                await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM \"Settings\" WHERE \"Key\" NOT LIKE 'app:%'", cancellationToken);

                state.RequiresReconciliation = false;
                state.LastServerCursor = 0;
                state.UpdatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                _session.UpdateAccount(state);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
        try
        {
            var status = await _sync.SyncNowAsync(true, cancellationToken);
            if (status.State != "up_to_date")
                throw new InvalidOperationException(
                    "Server replacement is not complete. The safety backup remains available; retry reconciliation when the connection is stable.");
            await RecordAuditAsync("backup.restore_reconciled", "organization", cancellationToken);
        }
        catch
        {
            await SetReconciliationRequiredAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task PrepareRestoreAsNewCloudStateAsync(CancellationToken cancellationToken = default)
    {
        var preview = await PreviewInitialMigrationAsync(cancellationToken);
        if (!preview.CloudBusinessDataIsEmpty)
            throw new InvalidOperationException(
                "The restored database can only be uploaded when the selected online organization has no business data.");
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var state = await db.CloudAccountStates.SingleAsync(cancellationToken);
        state.RequiresReconciliation = false;
        state.LastServerCursor = 0;
        state.UpdatedAtUtc = DateTime.UtcNow;
        db.SuppressSyncCapture = true;
        await db.SaveChangesAsync(cancellationToken);
        _session.UpdateAccount(state);
        await UploadExistingDataAsync(cancellationToken);
        await RecordAuditAsync("backup.restore_reconciled", "organization", cancellationToken);
    }

    private async Task RecordAuditAsync(
        string action,
        string affectedType,
        CancellationToken cancellationToken)
    {
        var account = _session.Account ?? throw new InvalidOperationException("The online account is unavailable.");
        await _api.PostAuthorizedAsync<OkEnvelope>("/api/v1/audit/events", new
        {
            action,
            affectedType,
            affectedId = account.TenantId,
            storeId = account.CurrentStoreId
        }, cancellationToken);
    }

    private static async Task<CloudAccountState?> ReadCloudStateFromBackupAsync(
        string backupPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath)) return null;
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT "ApiBaseUrl", "TenantId", "TenantName", "CurrentStoreId", "CurrentStoreName",
                       "CurrentCloudUserId", "DeviceId", "DeviceName", "IsEnabled", "IsDeviceRevoked",
                       "LastSuccessfulSyncAtUtc", "LastLoginAtUtc", "ServerApiVersion", "ServerSchemaVersion",
                       "CreatedAtUtc"
                FROM "CloudAccountStates" WHERE "Id" = 1
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;

            string Text(string name) => reader.IsDBNull(reader.GetOrdinal(name))
                ? string.Empty : reader.GetString(reader.GetOrdinal(name));
            DateTime? Date(string name) => DateTime.TryParse(Text(name), null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var value) ? value.ToUniversalTime() : null;
            return new CloudAccountState
            {
                ApiBaseUrl = Text("ApiBaseUrl"),
                TenantId = Text("TenantId"),
                TenantName = Text("TenantName"),
                CurrentStoreId = Text("CurrentStoreId"),
                CurrentStoreName = Text("CurrentStoreName"),
                CurrentCloudUserId = Text("CurrentCloudUserId"),
                DeviceId = Text("DeviceId"),
                DeviceName = Text("DeviceName"),
                IsEnabled = reader.GetInt64(reader.GetOrdinal("IsEnabled")) != 0,
                IsDeviceRevoked = reader.GetInt64(reader.GetOrdinal("IsDeviceRevoked")) != 0,
                LastSuccessfulSyncAtUtc = Date("LastSuccessfulSyncAtUtc"),
                LastLoginAtUtc = Date("LastLoginAtUtc"),
                ServerApiVersion = reader.GetInt32(reader.GetOrdinal("ServerApiVersion")),
                ServerSchemaVersion = reader.GetInt32(reader.GetOrdinal("ServerSchemaVersion")),
                CreatedAtUtc = Date("CreatedAtUtc") ?? DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
        }
        catch (SqliteException)
        {
            // A pre-cloud backup legitimately has no account-state table.
            return null;
        }
    }

    private static void CopyCloudLink(CloudAccountState source, CloudAccountState target)
    {
        target.ApiBaseUrl = source.ApiBaseUrl;
        target.TenantId = source.TenantId;
        target.TenantName = source.TenantName;
        target.CurrentStoreId = source.CurrentStoreId;
        target.CurrentStoreName = source.CurrentStoreName;
        target.CurrentCloudUserId = source.CurrentCloudUserId;
        target.DeviceId = source.DeviceId;
        target.DeviceName = source.DeviceName;
        target.IsEnabled = source.IsEnabled;
        target.IsDeviceRevoked = source.IsDeviceRevoked;
        target.LastSuccessfulSyncAtUtc = source.LastSuccessfulSyncAtUtc;
        target.LastLoginAtUtc = source.LastLoginAtUtc;
        target.ServerApiVersion = source.ServerApiVersion;
        target.ServerSchemaVersion = source.ServerSchemaVersion;
        target.CreatedAtUtc = source.CreatedAtUtc;
    }

    private async Task<MigrationOutboxState> GetMigrationOutboxStateAsync(CancellationToken cancellationToken)
    {
        var account = _session.Account ?? throw new InvalidOperationException("The online account is unavailable.");
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var operations = db.SyncOutboxOperations.AsNoTracking().Where(value =>
            value.CreatedByUserId == account.CurrentCloudUserId &&
            (value.StoreId == null || value.StoreId == account.CurrentStoreId));
        var remaining = await operations.CountAsync(value => value.Status != SyncOutboxStatus.Synchronized,
            cancellationToken);
        var blocked = await operations.CountAsync(value =>
            value.Status == SyncOutboxStatus.Failed || value.Status == SyncOutboxStatus.Conflict,
            cancellationToken);
        return new MigrationOutboxState(remaining, blocked);
    }

    private async Task SetReconciliationRequiredAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var state = await db.CloudAccountStates.SingleAsync(cancellationToken);
        state.RequiresReconciliation = true;
        state.UpdatedAtUtc = DateTime.UtcNow;
        db.SuppressSyncCapture = true;
        await db.SaveChangesAsync(cancellationToken);
        _session.UpdateAccount(state);
    }

    private sealed class SyncStatusEnvelope
    {
        public bool BusinessDataEmpty { get; set; }
        public bool InitialMigrationInProgress { get; set; }
        public bool InitialMigrationOwnedByDevice { get; set; }
        public bool InitialMigrationCompletedByDevice { get; set; }
        public bool InitialMigrationResumableByDevice { get; set; }
        public string? LatestDeviceMigrationId { get; set; }
        public IReadOnlyDictionary<string, int> Counts { get; set; } = new Dictionary<string, int>();
    }
    private sealed class MigrationLeaseEnvelope
    {
        public string MigrationId { get; set; } = string.Empty;
        public DateTime ExpiresAtUtc { get; set; }
    }
    private sealed class OkEnvelope { public bool Ok { get; set; } }
    private sealed record MigrationOutboxState(int Remaining, int Blocked);
}
