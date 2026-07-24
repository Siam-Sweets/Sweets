using System.ComponentModel.DataAnnotations;

namespace PosApp.Core.Entities;

/// <summary>One independently operated shop inside the local PosApp installation.</summary>
public sealed class Store
{
    public int Id { get; set; }

    [MaxLength(32)]
    public string SyncId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(24)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Address { get; set; }

    [MaxLength(30)]
    public string? Phone { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public long SyncVersion { get; set; } = 1;
    public DateTime SyncUpdatedAt { get; set; } = DateTime.UtcNow;
    public long CloudVersion { get; set; }
}

/// <summary>
/// Shared metadata used to isolate records by store and give every record a
/// permanent identifier that can be used by the cloud synchronization layer.
/// </summary>
public abstract class StoreScopedEntity
{
    public int StoreId { get; set; }

    [MaxLength(32)]
    public string SyncId { get; set; } = Guid.NewGuid().ToString("N");

    public long SyncVersion { get; set; } = 1;
    public DateTime SyncUpdatedAt { get; set; } = DateTime.UtcNow;
    public long CloudVersion { get; set; }
}

/// <summary>A durable local change waiting to be accepted by the cloud API.</summary>
public sealed class SyncOutboxItem
{
    public long Id { get; set; }
    public int StoreId { get; set; }

    [MaxLength(32)]
    public string ChangeId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(80)]
    public string EntityType { get; set; } = string.Empty;

    [MaxLength(32)]
    public string EntitySyncId { get; set; } = string.Empty;

    [MaxLength(12)]
    public string Operation { get; set; } = "upsert";

    public long EntityVersion { get; set; }
    public long BaseCloudVersion { get; set; }

    [MaxLength(64)]
    public string OperationId { get; set; } = Guid.NewGuid().ToString("N");
    public string PayloadJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAt { get; set; }

    [MaxLength(1000)]
    public string? LastError { get; set; }
}

/// <summary>Per-store cloud cursor and last synchronization result.</summary>
public sealed class SyncState
{
    public int Id { get; set; }
    public int StoreId { get; set; }

    [MaxLength(64)]
    public string DeviceId { get; set; } = string.Empty;

    public long PullCursor { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public DateTime? LastSuccessfulSyncAt { get; set; }
    public DateTime? LastSnapshotUploadedAt { get; set; }

    [MaxLength(1000)]
    public string? LastError { get; set; }
}

/// <summary>A cloud revision conflict retained for explicit review instead of overwriting local data.</summary>
public sealed class SyncConflict
{
    public long Id { get; set; }
    public int StoreId { get; set; }

    [MaxLength(80)]
    public string EntityType { get; set; } = string.Empty;

    [MaxLength(32)]
    public string EntitySyncId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string ChangeId { get; set; } = string.Empty;

    public long LocalBaseCloudVersion { get; set; }
    public long RemoteCloudVersion { get; set; }

    [MaxLength(12)]
    public string LocalOperation { get; set; } = "upsert";

    [MaxLength(12)]
    public string RemoteOperation { get; set; } = "upsert";

    public string LocalPayloadJson { get; set; } = "{}";
    public string RemotePayloadJson { get; set; } = "{}";

    [MaxLength(1000)]
    public string Message { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }

    [MaxLength(24)]
    public string? Resolution { get; set; }

    public string? ResolvedPayloadJson { get; set; }
}

/// <summary>One locally retained synchronization attempt for diagnostics and support.</summary>
public sealed class SyncRun
{
    public long Id { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    [MaxLength(20)]
    public string Status { get; set; } = "running";

    [MaxLength(64)]
    public string DeviceId { get; set; } = string.Empty;

    public int StoreCount { get; set; }
    public int PushedChanges { get; set; }
    public int PulledChanges { get; set; }
    public int ConflictCount { get; set; }
    public int PendingAfter { get; set; }

    [MaxLength(1000)]
    public string? Error { get; set; }
}

