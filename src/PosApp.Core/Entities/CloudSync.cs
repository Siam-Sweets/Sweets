using System.ComponentModel.DataAnnotations;

namespace PosApp.Core.Entities;

/// <summary>
/// Non-secret linkage between the active local profile and one cloud organization.
/// Access and refresh tokens are deliberately stored outside SQLite by the
/// Windows protected token store.
/// </summary>
public sealed class CloudAccountState
{
    public int Id { get; set; } = 1;

    [MaxLength(512)]
    public string ApiBaseUrl { get; set; } = string.Empty;

    [MaxLength(64)]
    public string TenantId { get; set; } = string.Empty;

    [MaxLength(160)]
    public string TenantName { get; set; } = string.Empty;

    [MaxLength(64)]
    public string CurrentStoreId { get; set; } = string.Empty;

    [MaxLength(160)]
    public string CurrentStoreName { get; set; } = string.Empty;

    [MaxLength(64)]
    public string CurrentCloudUserId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("D");

    [MaxLength(160)]
    public string DeviceName { get; set; } = Environment.MachineName;

    public bool IsEnabled { get; set; }
    public bool IsDeviceRevoked { get; set; }
    public bool RequiresReconciliation { get; set; }

    [MaxLength(1024)]
    public string? ReconciliationBackupPath { get; set; }

    [MaxLength(64)]
    public string? ActiveMigrationId { get; set; }

    [MaxLength(64)]
    public string? ActiveMigrationStoreId { get; set; }

    [MaxLength(1024)]
    public string? ActiveMigrationBackupPath { get; set; }

    public bool IsMigrationSnapshotQueued { get; set; }

    public long LastServerCursor { get; set; }
    public DateTime? LastSuccessfulSyncAtUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
    public int ServerApiVersion { get; set; }
    public int ServerSchemaVersion { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Maps an existing local integer key to the stable UUID used by every device.
/// Keeping this separate lets old installations retain all historical keys.
/// </summary>
public sealed class SyncIdentity
{
    public int Id { get; set; }

    [MaxLength(64)]
    public string EntityType { get; set; } = string.Empty;

    public int LocalId { get; set; }

    [MaxLength(64)]
    public string RecordId { get; set; } = Guid.NewGuid().ToString("D");

    [MaxLength(64)]
    public string TenantId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? StoreId { get; set; }

    public long ServerVersion { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAtUtc { get; set; }

    [MaxLength(64)]
    public string? LastModifiedDeviceId { get; set; }
}

public sealed class SyncOutboxOperation
{
    public long Id { get; set; }

    [MaxLength(64)]
    public string OperationId { get; set; } = Guid.NewGuid().ToString("D");

    [MaxLength(64)]
    public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(64)]
    public string EntityType { get; set; } = string.Empty;

    [MaxLength(64)]
    public string RecordId { get; set; } = string.Empty;

    public int LocalId { get; set; }

    [MaxLength(64)]
    public string? StoreId { get; set; }

    [MaxLength(64)]
    public string CreatedByUserId { get; set; } = string.Empty;

    public SyncOperationKind Operation { get; set; }
    public long BaseVersion { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public SyncOutboxStatus Status { get; set; } = SyncOutboxStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }

    [MaxLength(80)]
    public string? LastErrorCode { get; set; }

    [MaxLength(500)]
    public string? LastErrorMessage { get; set; }
}

public enum SyncOperationKind
{
    Upsert = 0,
    Delete = 1
}

public enum SyncOutboxStatus
{
    Pending = 0,
    Uploading = 1,
    Synchronized = 2,
    Conflict = 3,
    Failed = 4
}

public sealed class SyncCursorState
{
    public int Id { get; set; }

    [MaxLength(64)]
    public string TenantId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string StoreId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string DeviceId { get; set; } = string.Empty;

    public long Cursor { get; set; }
    public DateTime? LastPullAtUtc { get; set; }
    public DateTime? LastPushAtUtc { get; set; }
}

public sealed class SyncConflict
{
    public long Id { get; set; }

    [MaxLength(64)]
    public string ConflictId { get; set; } = Guid.NewGuid().ToString("D");

    [MaxLength(64)]
    public string EntityType { get; set; } = string.Empty;

    [MaxLength(64)]
    public string RecordId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? OperationId { get; set; }

    public long LocalBaseVersion { get; set; }
    public long ServerVersion { get; set; }
    public string LocalPayloadJson { get; set; } = "{}";
    public string ServerPayloadJson { get; set; } = "{}";

    [MaxLength(64)]
    public string? ServerStoreId { get; set; }

    public DateTime? ServerUpdatedAtUtc { get; set; }
    public DateTime? ServerDeletedAtUtc { get; set; }

    [MaxLength(64)]
    public string? ServerLastModifiedDeviceId { get; set; }

    public SyncConflictStatus Status { get; set; } = SyncConflictStatus.Unresolved;
    public DateTime DetectedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAtUtc { get; set; }

    [MaxLength(64)]
    public string? ResolvedByUserId { get; set; }
}

public enum SyncConflictStatus
{
    Unresolved = 0,
    KeepLocal = 1,
    UseServer = 2,
    Resolved = 3
}

public sealed class CloudCachedStore
{
    public int Id { get; set; }

    [MaxLength(64)]
    public string CloudStoreId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string TenantId { get; set; } = string.Empty;

    [MaxLength(160)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(80)]
    public string Code { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class CloudCachedDeviceSession
{
    public int Id { get; set; }

    [MaxLength(64)]
    public string CloudSessionId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string DeviceId { get; set; } = string.Empty;

    [MaxLength(160)]
    public string DeviceName { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? StoreId { get; set; }

    [MaxLength(160)]
    public string? StoreName { get; set; }

    [MaxLength(160)]
    public string OperatingSystem { get; set; } = string.Empty;

    public DateTime FirstRegisteredAtUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
    public DateTime? LastSyncAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public bool IsCurrent { get; set; }
    public bool IsRevoked { get; set; }
}

/// <summary>Local financial expense record; cloud sync treats it as append-only once posted.</summary>
public sealed class Expense
{
    public int Id { get; set; }

    [MaxLength(100)]
    public string Category { get; set; } = string.Empty;

    [MaxLength(300)]
    public string Description { get; set; } = string.Empty;

    public decimal Amount { get; set; }
    public DateTime ExpenseDate { get; set; } = DateTime.UtcNow;
    public int UserId { get; set; }
    public bool IsVoided { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Process-wide capture scope. A PosApp process has one active organization,
/// store, and device at a time; pulled changes explicitly suppress capture.
/// </summary>
public static class SyncCaptureContext
{
    private static readonly object Gate = new();
    private static SyncCaptureSnapshot _current = SyncCaptureSnapshot.Disabled;

    public static event EventHandler? OutboxChanged;

    public static SyncCaptureSnapshot Current
    {
        get { lock (Gate) return _current; }
    }

    public static void Enable(string tenantId, string storeId, string deviceId, string? userId)
    {
        lock (Gate)
            _current = new SyncCaptureSnapshot(true, tenantId, storeId, deviceId, userId);
    }

    public static void Disable()
    {
        lock (Gate) _current = SyncCaptureSnapshot.Disabled;
    }

    public static void NotifyOutboxChanged() => OutboxChanged?.Invoke(null, EventArgs.Empty);
}

public sealed record SyncCaptureSnapshot(
    bool Enabled,
    string TenantId,
    string StoreId,
    string DeviceId,
    string? UserId)
{
    public static SyncCaptureSnapshot Disabled { get; } = new(false, string.Empty, string.Empty, string.Empty, null);
}
