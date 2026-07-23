namespace PosApp.Core.Models;

public sealed class CloudAccountStatus
{
    public bool IsConfigured { get; init; }
    public bool IsAuthenticated { get; init; }
    public string Endpoint { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;
    public DateTimeOffset? AccessTokenExpiresAt { get; init; }
    public DateTimeOffset? InitialSnapshotUploadedAt { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class CloudSignUpRequest
{
    public string Email { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string RegistrationKey { get; init; } = string.Empty;
}

public sealed class CloudSignInRequest
{
    public string Email { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}

public sealed class CloudSnapshotUploadSummary
{
    public int StoreCount { get; init; }
    public long TotalRows { get; init; }
    public DateTimeOffset UploadedAt { get; init; }
}

public sealed class CloudSyncStatus
{
    public bool IsConnected { get; init; }
    public bool IsRunning { get; init; }
    public int PendingChanges { get; init; }
    public int ConflictCount { get; init; }
    public DateTimeOffset? LastSuccessfulSyncAt { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class CloudSyncSummary
{
    public int StoreCount { get; init; }
    public int PushedChanges { get; init; }
    public int PulledChanges { get; init; }
    public int ConflictCount { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
}

public sealed class CloudRestoreSummary
{
    public int StoreCount { get; init; }
    public long RestoredRows { get; init; }
    public DateTimeOffset RestoredAt { get; init; }
}



public enum SyncConflictResolutionMode
{
    KeepLocal,
    UseCloud,
    Merge
}

public sealed class SyncConflictRecord
{
    public long Id { get; init; }
    public int StoreId { get; init; }
    public string StoreName { get; init; } = string.Empty;
    public string EntityType { get; init; } = string.Empty;
    public string EntitySyncId { get; init; } = string.Empty;
    public string ChangeId { get; init; } = string.Empty;
    public long LocalBaseCloudVersion { get; init; }
    public long RemoteCloudVersion { get; init; }
    public string LocalOperation { get; init; } = "upsert";
    public string RemoteOperation { get; init; } = "upsert";
    public string LocalPayloadJson { get; init; } = "{}";
    public string RemotePayloadJson { get; init; } = "{}";
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public bool AllowsFieldMerge { get; init; }
}

public sealed class SyncConflictResolutionRequest
{
    public long ConflictId { get; init; }
    public SyncConflictResolutionMode Mode { get; init; }
    public string? MergedPayloadJson { get; init; }
}

public sealed class SyncStoreDiagnostic
{
    public int StoreId { get; init; }
    public string StoreName { get; init; } = string.Empty;
    public long PullCursor { get; init; }
    public int PendingChanges { get; init; }
    public int FailedChanges { get; init; }
    public int ConflictCount { get; init; }
    public DateTimeOffset? LastSuccessfulSyncAt { get; init; }
    public string LastError { get; init; } = string.Empty;
}

public sealed class SyncRunRecord
{
    public long Id { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string Status { get; init; } = string.Empty;
    public int StoreCount { get; init; }
    public int PushedChanges { get; init; }
    public int PulledChanges { get; init; }
    public int ConflictCount { get; init; }
    public int PendingAfter { get; init; }
    public string Error { get; init; } = string.Empty;
}

public sealed class SyncQueueIssue
{
    public long Id { get; init; }
    public string StoreName { get; init; } = string.Empty;
    public string EntityType { get; init; } = string.Empty;
    public string EntitySyncId { get; init; } = string.Empty;
    public int AttemptCount { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
    public string LastError { get; init; } = string.Empty;
}

public sealed class CloudDeviceRecord
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Platform { get; init; } = string.Empty;
    public string AppVersion { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastSeenAt { get; init; }
    public bool IsCurrent { get; init; }
    public bool IsRevoked { get; init; }
    public int StoreCursorCount { get; init; }
}

public sealed class SyncCenterSnapshot
{
    public CloudSyncStatus Status { get; init; } = new();
    public IReadOnlyList<SyncConflictRecord> Conflicts { get; init; } = Array.Empty<SyncConflictRecord>();
    public IReadOnlyList<SyncStoreDiagnostic> Stores { get; init; } = Array.Empty<SyncStoreDiagnostic>();
    public IReadOnlyList<SyncRunRecord> Runs { get; init; } = Array.Empty<SyncRunRecord>();
    public IReadOnlyList<SyncQueueIssue> QueueIssues { get; init; } = Array.Empty<SyncQueueIssue>();
    public IReadOnlyList<CloudDeviceRecord> Devices { get; init; } = Array.Empty<CloudDeviceRecord>();
}
