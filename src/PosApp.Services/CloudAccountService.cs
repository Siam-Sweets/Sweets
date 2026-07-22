using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Data;

namespace PosApp.Services;

/// <summary>
/// Configures the self-hosted PosApp cloud account and uploads the baseline
/// snapshot used by the offline-first incremental synchronization engine.
/// </summary>
public sealed class CloudAccountService : ICloudAccountService
{
    private const int MaximumSnapshotBytes = 15_000_000;
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(2)
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AppDbContext _db;
    private readonly CloudCredentialStore _credentials = new();

    public CloudAccountService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<CloudAccountStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var credential = await _credentials.LoadAsync(cancellationToken);
        if (credential == null)
        {
            var identity = await _credentials.GetOrCreateDeviceIdentityAsync(cancellationToken);
            return new CloudAccountStatus
            {
                DeviceName = identity.DeviceName,
                Message = "Cloud account is not connected. Local checkout remains available."
            };
        }

        return ToStatus(credential, "Cloud account connected. Offline-first synchronization is available.");
    }

    public async Task TestConnectionAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        var normalizedEndpoint = NormalizeEndpoint(endpoint);
        using var response = await Http.GetAsync($"{normalizedEndpoint}/v1/health", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<CloudAccountStatus> SignUpAsync(
        CloudSignUpRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var endpoint = NormalizeEndpoint(request.Endpoint);
        var email = NormalizeEmail(request.Email);
        ValidatePassword(request.Password);
        var displayName = NormalizeRequired(request.DisplayName, 100, "Display name");
        var registrationKey = NormalizeRequired(request.RegistrationKey, 256, "Registration key");
        var identity = await _credentials.GetOrCreateDeviceIdentityAsync(cancellationToken);
        var deviceName = NormalizeDeviceName(request.DeviceName, identity.DeviceName);

        var auth = await PostAsync<CloudAuthResponse>(endpoint, "/v1/auth/signup", new
        {
            email,
            password = request.Password,
            displayName,
            registrationKey,
            deviceKey = identity.DeviceKey,
            deviceName,
            platform = "Windows",
            appVersion = CurrentVersion()
        }, null, cancellationToken);

        var credential = ToCredential(endpoint, identity.DeviceKey, auth);
        await _credentials.SaveAsync(credential, cancellationToken);
        return ToStatus(credential, "Cloud account created and this device was registered.");
    }

    public async Task<CloudAccountStatus> SignInAsync(
        CloudSignInRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var endpoint = NormalizeEndpoint(request.Endpoint);
        var email = NormalizeEmail(request.Email);
        ValidatePassword(request.Password);
        var identity = await _credentials.GetOrCreateDeviceIdentityAsync(cancellationToken);
        var deviceName = NormalizeDeviceName(request.DeviceName, identity.DeviceName);

        var auth = await PostAsync<CloudAuthResponse>(endpoint, "/v1/auth/login", new
        {
            email,
            password = request.Password,
            deviceKey = identity.DeviceKey,
            deviceName,
            platform = "Windows",
            appVersion = CurrentVersion()
        }, null, cancellationToken);

        var credential = ToCredential(endpoint, identity.DeviceKey, auth);
        await _credentials.SaveAsync(credential, cancellationToken);
        return ToStatus(credential, "Signed in and this device was registered.");
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
        => _credentials.ClearAsync(cancellationToken);

    public async Task<CloudSnapshotUploadSummary> UploadInitialSnapshotsAsync(
        CancellationToken cancellationToken = default)
    {
        var credential = await RequireCredentialAsync(cancellationToken);
        var stores = await _db.Stores.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken);
        if (stores.Count == 0) throw new InvalidOperationException("No local stores are available to upload.");

        long totalRows = 0;
        foreach (var store in stores)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await BuildSnapshotAsync(store, cancellationToken);
            var snapshotJson = JsonSerializer.Serialize(snapshot, JsonOptions);
            var byteCount = Encoding.UTF8.GetByteCount(snapshotJson);
            if (byteCount > MaximumSnapshotBytes)
            {
                throw new InvalidOperationException(
                    $"The initial snapshot for {store.Name} is {byteCount / 1_000_000d:0.0} MB. " +
                    "Snapshots are limited to 15 MB per store. Reduce archived data before uploading a new full snapshot.");
            }

            credential = await EnsureFreshAccessTokenAsync(credential, cancellationToken);
            var state = await _db.SyncStates.AsNoTracking()
                .FirstOrDefaultAsync(x => x.StoreId == store.Id, cancellationToken);
            var uploaded = await PostAsync<CloudSnapshotResponse>(credential.Endpoint, "/v1/sync/snapshot/upload", new
            {
                store = new
                {
                    syncId = store.SyncId,
                    store.Code,
                    store.Name,
                    store.Address,
                    store.Phone,
                    store.IsActive,
                    store.CreatedAt,
                    store.UpdatedAt
                },
                schemaVersion = 2,
                appVersion = CurrentVersion(),
                syncCursor = state?.PullCursor ?? 0,
                rowCount = snapshot.RowCount,
                payload = snapshot
            }, credential.AccessToken, cancellationToken);

            totalRows += snapshot.RowCount;
            await RecordSnapshotSuccessAsync(
                store.Id, credential.DeviceId, uploaded.SyncCursor, cancellationToken);
        }

        var uploadedAt = DateTimeOffset.UtcNow;
        credential.InitialSnapshotUploadedAt = uploadedAt;
        await _credentials.SaveAsync(credential, cancellationToken);
        return new CloudSnapshotUploadSummary
        {
            StoreCount = stores.Count,
            TotalRows = totalRows,
            UploadedAt = uploadedAt
        };
    }

    private async Task<StoreSnapshot> BuildSnapshotAsync(Store store, CancellationToken cancellationToken)
    {
        var entities = new SortedDictionary<string, IReadOnlyList<SortedDictionary<string, object?>>>(StringComparer.Ordinal)
        {
            [nameof(CashMovement)] = await ReadRowsAsync<CashMovement>(store.Id, cancellationToken),
            [nameof(CashSession)] = await ReadRowsAsync<CashSession>(store.Id, cancellationToken),
            [nameof(Category)] = await ReadRowsAsync<Category>(store.Id, cancellationToken),
            [nameof(Customer)] = await ReadRowsAsync<Customer>(store.Id, cancellationToken),
            [nameof(Discount)] = await ReadRowsAsync<Discount>(store.Id, cancellationToken),
            [nameof(Product)] = await ReadRowsAsync<Product>(store.Id, cancellationToken),
            [nameof(PurchaseDocument)] = await ReadRowsAsync<PurchaseDocument>(store.Id, cancellationToken),
            [nameof(PurchaseItem)] = await ReadRowsAsync<PurchaseItem>(store.Id, cancellationToken),
            [nameof(Sale)] = await ReadRowsAsync<Sale>(store.Id, cancellationToken),
            [nameof(SaleItem)] = await ReadRowsAsync<SaleItem>(store.Id, cancellationToken),
            [nameof(SalePayment)] = await ReadRowsAsync<SalePayment>(store.Id, cancellationToken),
            [nameof(Setting)] = await ReadSettingsAsync(store.Id, cancellationToken),
            [nameof(StockTransaction)] = await ReadRowsAsync<StockTransaction>(store.Id, cancellationToken),
            [nameof(StockTransfer)] = await ReadRowsAsync<StockTransfer>(store.Id, cancellationToken),
            [nameof(StockTransferItem)] = await ReadRowsAsync<StockTransferItem>(store.Id, cancellationToken),
            [nameof(Supplier)] = await ReadRowsAsync<Supplier>(store.Id, cancellationToken),
            [nameof(Tax)] = await ReadRowsAsync<Tax>(store.Id, cancellationToken),
            [nameof(User)] = await ReadRowsAsync<User>(store.Id, cancellationToken)
        };
        var rowCount = 1L + entities.Values.Sum(x => (long)x.Count);
        return new StoreSnapshot
        {
            SchemaVersion = 4,
            AppVersion = CurrentVersion(),
            ExportedAtUtc = DateTimeOffset.UtcNow,
            Store = SyncPayloadSerializer.CreateValues(store),
            Entities = entities,
            RowCount = rowCount
        };
    }

    private async Task<IReadOnlyList<SortedDictionary<string, object?>>> ReadRowsAsync<TEntity>(
        int storeId,
        CancellationToken cancellationToken)
        where TEntity : StoreScopedEntity
    {
        var rows = await _db.Set<TEntity>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.StoreId == storeId)
            .OrderBy(x => x.SyncId)
            .ToListAsync(cancellationToken);
        return rows.Select(x => SyncPayloadSerializer.CreateValuesForSync(x, _db)).ToList();
    }

    private async Task<IReadOnlyList<SortedDictionary<string, object?>>> ReadSettingsAsync(
        int storeId,
        CancellationToken cancellationToken)
    {
        var rows = await _db.Settings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.StoreId == storeId &&
                        !x.Key.StartsWith("cloud:") &&
                        !x.Key.StartsWith("device:"))
            .OrderBy(x => x.SyncId)
            .ToListAsync(cancellationToken);
        return rows.Select(x => SyncPayloadSerializer.CreateValuesForSync(x, _db)).ToList();
    }

    private async Task RecordSnapshotSuccessAsync(
        int storeId,
        string deviceId,
        long pullCursor,
        CancellationToken cancellationToken)
    {
        var state = await _db.SyncStates.FirstOrDefaultAsync(x => x.StoreId == storeId, cancellationToken);
        if (state == null)
        {
            state = new SyncState { StoreId = storeId };
            _db.SyncStates.Add(state);
        }
        state.DeviceId = deviceId;
        state.PullCursor = Math.Max(state.PullCursor, pullCursor);
        state.LastSyncAt = DateTime.UtcNow;
        state.LastSuccessfulSyncAt = DateTime.UtcNow;
        state.LastSnapshotUploadedAt = DateTime.UtcNow;
        state.LastError = null;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<CloudCredential> RequireCredentialAsync(CancellationToken cancellationToken)
        => await _credentials.LoadAsync(cancellationToken)
           ?? throw new InvalidOperationException("Connect a cloud account before uploading a snapshot.");

    private async Task<CloudCredential> EnsureFreshAccessTokenAsync(
        CloudCredential credential,
        CancellationToken cancellationToken)
    {
        if (credential.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return credential;
        var refreshed = await PostAsync<CloudAuthResponse>(credential.Endpoint, "/v1/auth/refresh", new
        {
            refreshToken = credential.RefreshToken,
            deviceKey = credential.DeviceKey
        }, null, cancellationToken);
        var updated = ToCredential(credential.Endpoint, credential.DeviceKey, refreshed);
        updated.InitialSnapshotUploadedAt = credential.InitialSnapshotUploadedAt;
        await _credentials.SaveAsync(updated, cancellationToken);
        return updated;
    }

    private static CloudCredential ToCredential(
        string endpoint,
        string deviceKey,
        CloudAuthResponse auth)
    {
        if (auth.Owner == null || auth.Device == null || auth.Tokens == null ||
            string.IsNullOrWhiteSpace(auth.Tokens.AccessToken) ||
            string.IsNullOrWhiteSpace(auth.Tokens.RefreshToken))
            throw new InvalidOperationException("The cloud API returned an incomplete authentication response.");

        return new CloudCredential
        {
            Endpoint = endpoint,
            OwnerId = auth.Owner.Id,
            Email = auth.Owner.Email,
            DisplayName = auth.Owner.DisplayName,
            DeviceId = auth.Device.Id,
            DeviceKey = deviceKey,
            DeviceName = auth.Device.Name,
            AccessToken = auth.Tokens.AccessToken,
            RefreshToken = auth.Tokens.RefreshToken,
            AccessTokenExpiresAt = auth.Tokens.ExpiresAt
        };
    }

    private static CloudAccountStatus ToStatus(CloudCredential credential, string message)
        => new()
        {
            IsConfigured = true,
            IsAuthenticated = !string.IsNullOrWhiteSpace(credential.AccessToken),
            Endpoint = credential.Endpoint,
            Email = credential.Email,
            DisplayName = credential.DisplayName,
            DeviceName = credential.DeviceName,
            DeviceId = credential.DeviceId,
            AccessTokenExpiresAt = credential.AccessTokenExpiresAt,
            InitialSnapshotUploadedAt = credential.InitialSnapshotUploadedAt,
            Message = message
        };

    private static async Task<T> PostAsync<T>(
        string endpoint,
        string path,
        object payload,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(accessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw CreateApiException(response.StatusCode, json);
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

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        throw CreateApiException(response.StatusCode, json);
    }

    private static Exception CreateApiException(HttpStatusCode statusCode, string json)
    {
        try
        {
            var error = JsonSerializer.Deserialize<CloudApiError>(json, JsonOptions);
            if (!string.IsNullOrWhiteSpace(error?.Error))
                return new InvalidOperationException(error.Error);
        }
        catch (JsonException) { }
        return new InvalidOperationException($"Cloud API request failed ({(int)statusCode} {statusCode}).");
    }

    private static string NormalizeEndpoint(string? endpoint)
    {
        var value = endpoint?.Trim().TrimEnd('/') ?? string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Enter a valid cloud API URL.");
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            throw new InvalidOperationException("The cloud API URL must use HTTPS. HTTP is allowed only for localhost development.");
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("The cloud API URL cannot contain a query string or fragment.");
        return value;
    }

    private static string NormalizeEmail(string? email)
    {
        var normalized = email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length is < 3 or > 254 || !normalized.Contains('@'))
            throw new InvalidOperationException("Enter a valid email address.");
        return normalized;
    }

    private static void ValidatePassword(string? password)
    {
        if (password == null || password.Length < 10 || password.Length > 128)
            throw new InvalidOperationException("Cloud password must contain 10 to 128 characters.");
    }

    private static string NormalizeRequired(string? value, int maximum, string field)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0) throw new InvalidOperationException($"{field} is required.");
        if (normalized.Length > maximum) throw new InvalidOperationException($"{field} cannot exceed {maximum} characters.");
        return normalized;
    }

    private static string NormalizeDeviceName(string? requested, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim();
        if (value.Length > 100) value = value[..100];
        return value.Length == 0 ? "Windows POS" : value;
    }

    private static string CurrentVersion()
        => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.9.1";

    private sealed class StoreSnapshot
    {
        public int SchemaVersion { get; init; }
        public string AppVersion { get; init; } = string.Empty;
        public DateTimeOffset ExportedAtUtc { get; init; }
        public SortedDictionary<string, object?> Store { get; init; } = new(StringComparer.Ordinal);
        public SortedDictionary<string, IReadOnlyList<SortedDictionary<string, object?>>> Entities { get; init; } = new(StringComparer.Ordinal);
        public long RowCount { get; init; }
    }

    private sealed class CloudApiError
    {
        public string Error { get; set; } = string.Empty;
    }

    private sealed class CloudSnapshotResponse
    {
        public string SnapshotId { get; set; } = string.Empty;
        public long Version { get; set; }
        public long SyncCursor { get; set; }
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

internal sealed class CloudCredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes("PosApp.CloudCredentials.v1"));
    private readonly string _folder;
    private readonly string _credentialPath;
    private readonly string _identityPath;

    public CloudCredentialStore()
    {
        _folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PosApp");
        _credentialPath = Path.Combine(_folder, "cloud-credentials.dat");
        _identityPath = Path.Combine(_folder, "cloud-device.json");
    }

    public async Task<CloudCredential?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_credentialPath)) return null;
        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(_credentialPath, cancellationToken);
            var clearBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<CloudCredential>(clearBytes, JsonOptions);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "Saved cloud credentials cannot be decrypted for this Windows user. Disconnect and sign in again.", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Saved cloud credentials are invalid. Disconnect and sign in again.", ex);
        }
    }

    public async Task SaveAsync(CloudCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        Directory.CreateDirectory(_folder);
        var clearBytes = JsonSerializer.SerializeToUtf8Bytes(credential, JsonOptions);
        var protectedBytes = ProtectedData.Protect(clearBytes, Entropy, DataProtectionScope.CurrentUser);
        var temp = _credentialPath + ".tmp";
        await File.WriteAllBytesAsync(temp, protectedBytes, cancellationToken);
        File.Move(temp, _credentialPath, true);
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_credentialPath)) File.Delete(_credentialPath);
        return Task.CompletedTask;
    }

    public async Task<CloudDeviceIdentity> GetOrCreateDeviceIdentityAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(_identityPath))
            {
                var existing = JsonSerializer.Deserialize<CloudDeviceIdentity>(
                    await File.ReadAllTextAsync(_identityPath, cancellationToken), JsonOptions);
                if (existing != null && existing.DeviceKey.Length >= 16) return existing;
            }
        }
        catch (JsonException) { }

        Directory.CreateDirectory(_folder);
        var identity = new CloudDeviceIdentity
        {
            DeviceKey = Guid.NewGuid().ToString("N"),
            DeviceName = string.IsNullOrWhiteSpace(Environment.MachineName)
                ? "Windows POS"
                : Environment.MachineName
        };
        var temp = _identityPath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(identity, JsonOptions), cancellationToken);
        File.Move(temp, _identityPath, true);
        return identity;
    }
}

internal sealed class CloudCredential
{
    public string Endpoint { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceKey { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTimeOffset AccessTokenExpiresAt { get; set; }
    public DateTimeOffset? InitialSnapshotUploadedAt { get; set; }
}

internal sealed class CloudDeviceIdentity
{
    public string DeviceKey { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
}
