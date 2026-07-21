using PosApp.Core.Entities;

namespace PosApp.Core.Models;

public class CloudLoginRequest
{
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string UsernameOrEmail { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string OfflinePin { get; set; } = string.Empty;
    public string DeviceName { get; set; } = Environment.MachineName;
}

public sealed class CloudOrganizationRequest : CloudLoginRequest
{
    public string OrganizationName { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public sealed class CloudAuthTokens
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime AccessTokenExpiresAtUtc { get; set; }
    public DateTime RefreshTokenExpiresAtUtc { get; set; }
    public string SessionId { get; set; } = string.Empty;
}

public sealed class CloudUserProfile
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public UserRole Role { get; set; }
    public bool IsActive { get; set; }
    public IReadOnlyList<string> Permissions { get; set; } = Array.Empty<string>();
}

public sealed class CloudUserCreateRequest
{
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Cashier;
    public string StoreId { get; set; } = string.Empty;
}

public sealed class CloudUserUpdateRequest
{
    public string? FullName { get; set; }
    public UserRole? Role { get; set; }
    public bool? IsActive { get; set; }
}

public sealed class CloudStoreDto
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}

public sealed class CloudAuthenticationResult
{
    public CloudAuthTokens Tokens { get; set; } = new();
    public CloudUserProfile User { get; set; } = new();
    public string OrganizationId { get; set; } = string.Empty;
    public string OrganizationName { get; set; } = string.Empty;
    public CloudStoreDto Store { get; set; } = new();
    public string DeviceId { get; set; } = string.Empty;
    public int ApiVersion { get; set; }
    public int SchemaVersion { get; set; }
    public User? LocalUser { get; set; }
}

public sealed class SyncOperationDto
{
    public string OperationId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string RecordId { get; set; } = string.Empty;
    public string? StoreId { get; set; }
    public string Operation { get; set; } = "upsert";
    public long BaseVersion { get; set; }
    public object Payload { get; set; } = new();
    public DateTime ClientTimestampUtc { get; set; }
}

public sealed class SyncPushRequest
{
    public string DeviceId { get; set; } = string.Empty;
    public string StoreId { get; set; } = string.Empty;
    public int ClientSchemaVersion { get; set; } = CloudProtocol.ClientSchemaVersion;
    public IReadOnlyList<SyncOperationDto> Operations { get; set; } = Array.Empty<SyncOperationDto>();
}

public sealed class SyncOperationResultDto
{
    public string OperationId { get; set; } = string.Empty;
    public string RecordId { get; set; } = string.Empty;
    public bool Accepted { get; set; }
    public bool Duplicate { get; set; }
    public long ServerVersion { get; set; }
    public string? ErrorCode { get; set; }
    public string? Message { get; set; }
    public object? ServerPayload { get; set; }
    public string? ServerStoreId { get; set; }
    public DateTime? ServerUpdatedAtUtc { get; set; }
    public DateTime? ServerDeletedAtUtc { get; set; }
    public string? ServerLastModifiedDeviceId { get; set; }
}

public sealed class SyncPushResponse
{
    public IReadOnlyList<SyncOperationResultDto> Results { get; set; } = Array.Empty<SyncOperationResultDto>();
    public long ServerCursor { get; set; }
    public string RequestId { get; set; } = string.Empty;
}

public sealed class SyncChangeDto
{
    public long Cursor { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string RecordId { get; set; } = string.Empty;
    public string StoreId { get; set; } = string.Empty;
    public long Version { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string LastModifiedDeviceId { get; set; } = string.Empty;
    public object Payload { get; set; } = new();
}

public sealed class SyncPullResponse
{
    public IReadOnlyList<SyncChangeDto> Changes { get; set; } = Array.Empty<SyncChangeDto>();
    public long NextCursor { get; set; }
    public bool HasMore { get; set; }
    public string RequestId { get; set; } = string.Empty;
}

public sealed class CloudSyncStatus
{
    public bool IsSignedIn { get; set; }
    public bool IsOnline { get; set; }
    public bool IsSyncing { get; set; }
    public bool IsDeviceRevoked { get; set; }
    public bool RequiresReconciliation { get; set; }
    public string State { get; set; } = "offline";
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
    public string? LastRequestId { get; set; }
    public DateTime? LastSuccessfulSyncAtUtc { get; set; }
    public int PendingUploadCount { get; set; }
    public int ConflictCount { get; set; }
    public int DownloadedChangeCount { get; set; }
    public long Cursor { get; set; }
}

public sealed class CloudDeviceSessionDto
{
    public string SessionId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string? StoreId { get; set; }
    public string? StoreName { get; set; }
    public string OperatingSystem { get; set; } = string.Empty;
    public DateTime FirstRegisteredAtUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
    public DateTime? LastSyncAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public bool IsCurrent { get; set; }
    public bool IsRevoked { get; set; }
}

public sealed class CloudMigrationPreview
{
    public bool CloudBusinessDataIsEmpty { get; set; }
    public bool InitialMigrationCompletedByThisDevice { get; set; }
    public string? ResumableMigrationId { get; set; }
    public IReadOnlyDictionary<string, int> LocalCounts { get; set; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> CloudCounts { get; set; } = new Dictionary<string, int>();
    public string? BlockingCode { get; set; }
    public string? BlockingReason { get; set; }
}

public sealed class CloudMigrationResult
{
    public string BackupPath { get; set; } = string.Empty;
    public IReadOnlyDictionary<string, int> UploadedCounts { get; set; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> VerifiedCloudCounts { get; set; } = new Dictionary<string, int>();
    public int ConflictCount { get; set; }
    public DateTime CompletedAtUtc { get; set; }
}

public sealed class ApiErrorEnvelope
{
    public ApiErrorBody Error { get; set; } = new();
    public string RequestId { get; set; } = string.Empty;
}

public sealed class ApiErrorBody
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public object? Details { get; set; }
}

public static class CloudProtocol
{
    public const string ClientVersion = "2.1.1";
    public const int ApiVersion = 1;
    public const int ClientSchemaVersion = 4;
    // Turso's HTTP transaction protocol uses several sequential outbound calls
    // while validating each financial record. Two operations keep the worst-case
    // Worker invocation below Cloudflare Free's external-subrequest ceiling.
    public const int MaxPushBatch = 2;
    public const int MaxPullBatch = 100;
}
