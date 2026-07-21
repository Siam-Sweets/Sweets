using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Data;

namespace PosApp.Services;

public sealed class CloudAccountService : ICloudAccountService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly CloudApiClient _api;
    private readonly CloudSessionManager _session;
    private readonly ICloudSyncService _sync;
    private readonly IBackupService _backup;
    private readonly LocalOrganizationProfileStore _profiles;

    public CloudAccountService(
        IDbContextFactory<AppDbContext> dbFactory,
        CloudApiClient api,
        CloudSessionManager session,
        ICloudSyncService sync,
        IBackupService backup,
        LocalOrganizationProfileStore profiles)
    {
        _dbFactory = dbFactory;
        _api = api;
        _session = session;
        _sync = sync;
        _backup = backup;
        _profiles = profiles;
    }

    public async Task InitializeCachedSessionAsync(CancellationToken cancellationToken = default)
    {
        await _session.InitializeAsync(cancellationToken);
        if (_session.Account is { } account && !string.IsNullOrWhiteSpace(account.TenantId))
            _profiles.UpdateActiveProfile(account.TenantId, account.TenantName,
                account.CurrentStoreName, string.Empty);
        if (_session.IsSignedIn)
            await _sync.StartAsync(cancellationToken);
    }

    public async Task<bool> CanUseCachedSessionAsync(
        int localUserId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var state = await db.CloudAccountStates.SingleOrDefaultAsync(cancellationToken);
        if (state == null || string.IsNullOrWhiteSpace(state.TenantId)) return true;
        if (state.IsDeviceRevoked) return false;
        var identity = await db.SyncIdentities.AsNoTracking().SingleOrDefaultAsync(value =>
            value.EntityType == "users" && value.LocalId == localUserId, cancellationToken);
        var matches = identity != null && identity.DeletedAtUtc == null &&
                      string.Equals(identity.TenantId, state.TenantId, StringComparison.OrdinalIgnoreCase);
        if (!matches) return false;

        // A shared terminal may cache several organization users. Never let a
        // user who entered an offline PIN inherit another user's access token.
        // Their local work remains allowed and is queued under their own cloud
        // user UUID, but online calls pause until that user signs in online.
        if (!string.Equals(identity!.RecordId, state.CurrentCloudUserId,
                StringComparison.OrdinalIgnoreCase))
        {
            await _sync.StopAsync(cancellationToken);
            state.IsEnabled = false;
            state.UpdatedAtUtc = DateTime.UtcNow;
            db.SuppressSyncCapture = true;
            await db.SaveChangesAsync(cancellationToken);
            _session.UpdateAccount(state);
            await _session.ClearAsync(cancellationToken);
        }

        // Attribute every offline outbox row to the PIN-authenticated local
        // user's own cloud UUID. In particular, never reuse the former token
        // owner's UUID after clearing a mismatched shared-terminal session.
        SyncCaptureContext.Enable(state.TenantId, state.CurrentStoreId, state.DeviceId, identity!.RecordId);
        return matches;
    }

    public async Task<CloudAuthenticationResult> LoginAsync(
        CloudLoginRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateLoginRequest(request);
        var deviceId = await GetOrCreateDeviceIdAsync(cancellationToken);
        var result = await _api.PostAnonymousAsync<CloudAuthenticationResult>(
            request.ApiBaseUrl,
            "/api/v1/auth/login",
            new
            {
                usernameOrEmail = request.UsernameOrEmail.Trim(),
                password = request.Password,
                device = BuildDevice(deviceId, request.DeviceName),
                clientVersion = CloudProtocol.ClientVersion,
                clientSchemaVersion = CloudProtocol.ClientSchemaVersion
            },
            cancellationToken);
        result.DeviceId = deviceId;
        return await CompleteAuthenticationAsync(result, request.ApiBaseUrl, request.OfflinePin,
            request.DeviceName, cancellationToken);
    }

    public async Task<CloudAuthenticationResult> CreateOrganizationAsync(
        CloudOrganizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateLoginRequest(request);
        // A linked profile must never be repurposed for another tenant. The UI
        // creates and restarts into an empty isolated profile before invoking
        // this method for an additional organization.
        await using (var existingDb = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            if (await existingDb.CloudAccountStates.AsNoTracking()
                    .AnyAsync(value => value.TenantId != string.Empty, cancellationToken))
                throw new InvalidOperationException(
                    "This organization profile is already linked. Select Add organization to create a separate protected profile.");
        }
        if (string.IsNullOrWhiteSpace(request.OrganizationName) || request.OrganizationName.Trim().Length > 160)
            throw new ArgumentException("Organization name is required and must be 160 characters or fewer.");
        if (string.IsNullOrWhiteSpace(request.StoreName) || request.StoreName.Trim().Length > 160)
            throw new ArgumentException("Store name is required and must be 160 characters or fewer.");
        if (string.IsNullOrWhiteSpace(request.FullName) || request.FullName.Trim().Length > 100)
            throw new ArgumentException("Full name is required and must be 100 characters or fewer.");

        var deviceId = await GetOrCreateDeviceIdAsync(cancellationToken);
        var result = await _api.PostAnonymousAsync<CloudAuthenticationResult>(
            request.ApiBaseUrl,
            "/api/v1/auth/signup",
            new
            {
                organizationName = request.OrganizationName.Trim(),
                storeName = request.StoreName.Trim(),
                fullName = request.FullName.Trim(),
                username = request.UsernameOrEmail.Trim(),
                email = request.Email.Trim(),
                password = request.Password,
                device = BuildDevice(deviceId, request.DeviceName),
                clientVersion = CloudProtocol.ClientVersion,
                clientSchemaVersion = CloudProtocol.ClientSchemaVersion
            },
            cancellationToken);
        result.DeviceId = deviceId;
        return await CompleteAuthenticationAsync(result, request.ApiBaseUrl, request.OfflinePin,
            request.DeviceName, cancellationToken);
    }

    public async Task LogoutAsync(bool revokeAllDeviceSessions = false, CancellationToken cancellationToken = default)
    {
        if (_session.IsSignedIn)
        {
            try { await _sync.SyncNowAsync(false, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { /* logout still revokes tokens; queued local work remains safe */ }
        }
        await _sync.StopAsync(cancellationToken);
        try
        {
            if (_session.IsSignedIn)
                await _api.PostAuthorizedAsync<OkEnvelope>("/api/v1/auth/logout",
                    new { revokeAllDeviceSessions }, cancellationToken);
        }
        finally
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var state = await db.CloudAccountStates.SingleOrDefaultAsync(cancellationToken);
            if (state != null)
            {
                state.IsEnabled = false;
                state.UpdatedAtUtc = DateTime.UtcNow;
                db.SuppressSyncCapture = true;
                await db.SaveChangesAsync(cancellationToken);
                _session.UpdateAccount(state);
            }
            await _session.ClearAsync(cancellationToken);
            // Secure online logout revokes and removes tokens, but the current
            // cached user may continue operating offline. Keep capturing those
            // operations for that same user so the next authorized sign-in can
            // synchronize them instead of creating an untracked local gap.
            if (state != null && !string.IsNullOrWhiteSpace(state.TenantId) &&
                !string.IsNullOrWhiteSpace(state.CurrentStoreId) &&
                !string.IsNullOrWhiteSpace(state.CurrentCloudUserId))
                SyncCaptureContext.Enable(state.TenantId, state.CurrentStoreId,
                    state.DeviceId, state.CurrentCloudUserId);
        }
    }

    public async Task<IReadOnlyList<CloudStoreDto>> GetStoresAsync(CancellationToken cancellationToken = default)
    {
        var response = await _api.GetAuthorizedAsync<StoresEnvelope>("/api/v1/stores", cancellationToken);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        db.SuppressSyncCapture = true;
        foreach (var store in response.Stores)
        {
            var cached = await db.CloudCachedStores.SingleOrDefaultAsync(
                value => value.CloudStoreId == store.Id, cancellationToken);
            if (cached == null)
            {
                db.CloudCachedStores.Add(new CloudCachedStore
                {
                    CloudStoreId = store.Id,
                    TenantId = store.TenantId,
                    Name = store.Name,
                    Code = store.Code,
                    IsActive = store.IsActive
                });
            }
            else
            {
                cached.Name = store.Name;
                cached.Code = store.Code;
                cached.IsActive = store.IsActive;
                cached.UpdatedAtUtc = DateTime.UtcNow;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        return response.Stores;
    }

    public async Task SelectStoreAsync(string storeId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(storeId)) throw new ArgumentException("Select a store.", nameof(storeId));
        var stores = await GetStoresAsync(cancellationToken);
        var selected = stores.SingleOrDefault(store => store.Id == storeId && store.IsActive)
                       ?? throw new InvalidOperationException("The selected store is unavailable.");

        var current = _session.Account ?? throw new InvalidOperationException("Sign in to select a store.");
        await _api.PostAuthorizedAsync<OkEnvelope>("/api/v1/auth/register-device", new
        {
            device = BuildDevice(current.DeviceId, current.DeviceName),
            storeId = selected.Id
        }, cancellationToken);

        await _sync.StopAsync(cancellationToken);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var state = await db.CloudAccountStates.SingleAsync(cancellationToken);
        await PreserveCursorAsync(db, state, cancellationToken);
        state.CurrentStoreId = selected.Id;
        state.CurrentStoreName = selected.Name;
        state.LastServerCursor = await ReadCursorAsync(db, state.TenantId, selected.Id,
            state.DeviceId, cancellationToken);
        state.UpdatedAtUtc = DateTime.UtcNow;
        db.SuppressSyncCapture = true;
        await db.SaveChangesAsync(cancellationToken);
        _profiles.UpdateActiveProfile(state.TenantId, state.TenantName,
            state.CurrentStoreName, string.Empty);
        _session.UpdateAccount(state);
        await _sync.StartAsync(cancellationToken);
        await _sync.SyncNowAsync(true, cancellationToken);
    }

    public async Task<string> CreateStoreAsync(
        string name,
        string code,
        CancellationToken cancellationToken = default)
    {
        name = name?.Trim() ?? string.Empty;
        code = code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (name.Length is < 1 or > 160)
            throw new ArgumentException("Enter a store name of 160 characters or fewer.", nameof(name));
        if (code.Length is < 2 or > 20 || code.Any(character =>
                !((character is >= 'A' and <= 'Z') || char.IsAsciiDigit(character) || character is '_' or '-')))
            throw new ArgumentException("Use 2 to 20 letters, numbers, underscores, or hyphens for the store code.", nameof(code));
        var response = await _api.PostAuthorizedAsync<CreatedEnvelope>("/api/v1/stores",
            new { name, code }, cancellationToken);
        await GetStoresAsync(cancellationToken);
        return response.Id;
    }

    public async Task<IReadOnlyList<CloudDeviceSessionDto>> GetDeviceSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await _api.GetAuthorizedAsync<SessionsEnvelope>("/api/v1/auth/sessions", cancellationToken);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        db.SuppressSyncCapture = true;
        db.CloudCachedDeviceSessions.RemoveRange(db.CloudCachedDeviceSessions);
        db.CloudCachedDeviceSessions.AddRange(response.Sessions.Select(session => new CloudCachedDeviceSession
        {
            CloudSessionId = session.SessionId,
            DeviceId = session.DeviceId,
            DeviceName = session.DeviceName,
            StoreId = session.StoreId,
            StoreName = session.StoreName,
            OperatingSystem = session.OperatingSystem,
            FirstRegisteredAtUtc = session.FirstRegisteredAtUtc,
            LastLoginAtUtc = session.LastLoginAtUtc,
            LastSyncAtUtc = session.LastSyncAtUtc,
            ExpiresAtUtc = session.ExpiresAtUtc,
            IsCurrent = session.IsCurrent,
            IsRevoked = session.IsRevoked
        }));
        await db.SaveChangesAsync(cancellationToken);
        return response.Sessions;
    }

    public async Task RevokeDeviceSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("Session ID is required.", nameof(sessionId));
        if (string.Equals(_session.Tokens?.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use Secure online logout to end the current session.");
        await _api.DeleteAuthorizedAsync<OkEnvelope>(
            $"/api/v1/auth/sessions/{Uri.EscapeDataString(sessionId)}", cancellationToken);
        await GetDeviceSessionsAsync(cancellationToken);
    }

    public async Task RevokeDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(deviceId, out _)) throw new ArgumentException("Select a valid device.", nameof(deviceId));
        if (string.Equals(_session.Account?.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use Secure online logout instead of revoking this computer.");
        await _api.DeleteAuthorizedAsync<OkEnvelope>(
            $"/api/v1/auth/devices/{Uri.EscapeDataString(deviceId)}", cancellationToken);
        await GetDeviceSessionsAsync(cancellationToken);
    }

    public async Task AuthorizeDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(deviceId, out _)) throw new ArgumentException("Select a valid device.", nameof(deviceId));
        await _api.PatchAuthorizedAsync<OkEnvelope>(
            $"/api/v1/auth/devices/{Uri.EscapeDataString(deviceId)}", new { status = "active" }, cancellationToken);
        await GetDeviceSessionsAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CloudUserProfile>> GetUsersAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await _api.GetAuthorizedAsync<UsersEnvelope>("/api/v1/users", cancellationToken);
        return response.Users;
    }

    public async Task<string> CreateUserAsync(
        CloudUserCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 60 ||
            request.Username.Any(char.IsWhiteSpace))
            throw new ArgumentException("Enter a username of 60 characters or fewer without spaces.");
        if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > 255 || !request.Email.Contains('@'))
            throw new ArgumentException("Enter a valid email address.");
        if (string.IsNullOrWhiteSpace(request.FullName) || request.FullName.Length > 100)
            throw new ArgumentException("Enter a full name of 100 characters or fewer.");
        if (string.IsNullOrWhiteSpace(request.StoreId))
            throw new ArgumentException("Select a store for the user.");
        ValidateCloudPassword(request.Password);
        var localLink = await EnsureLocalUserRecordIdentityAsync(request.Username.Trim(), cancellationToken);
        var response = await _api.PostAuthorizedAsync<CreatedEnvelope>("/api/v1/users", new
        {
            username = request.Username.Trim(),
            email = request.Email.Trim(),
            fullName = request.FullName.Trim(),
            password = request.Password,
            role = request.Role.ToString().ToLowerInvariant(),
            storeId = request.StoreId,
            recordId = localLink?.RecordId,
            permissions = Array.Empty<string>()
        }, cancellationToken);
        if (localLink != null && !string.Equals(localLink.RecordId, response.Id, StringComparison.OrdinalIgnoreCase))
            await ReconcileLocalUserRecordIdentityAsync(localLink, response.Id, cancellationToken);
        _ = _sync.SyncNowAsync(false, CancellationToken.None);
        return response.Id;
    }

    public async Task UpdateUserAsync(
        string userId,
        CloudUserUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(userId, out _)) throw new ArgumentException("Select a valid online user.");
        ArgumentNullException.ThrowIfNull(request);
        await _api.PatchAuthorizedAsync<OkEnvelope>($"/api/v1/users/{Uri.EscapeDataString(userId)}", new
        {
            fullName = string.IsNullOrWhiteSpace(request.FullName) ? null : request.FullName.Trim(),
            role = request.Role?.ToString().ToLowerInvariant(),
            isActive = request.IsActive
        }, cancellationToken);
        _ = _sync.SyncNowAsync(false, CancellationToken.None);
    }

    public async Task ChangePasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        ValidateCloudPassword(newPassword);
        await _api.PostAuthorizedAsync<OkEnvelope>("/api/v1/account/password",
            new { currentPassword, newPassword }, cancellationToken);
    }

    public async Task<CloudAccountState?> GetAccountStateAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CloudAccountStates.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<CloudAuthenticationResult> CompleteAuthenticationAsync(
        CloudAuthenticationResult result,
        string apiBaseUrl,
        string offlinePin,
        string deviceName,
        CancellationToken cancellationToken)
    {
        if (result.ApiVersion != CloudProtocol.ApiVersion || result.SchemaVersion < CloudProtocol.ClientSchemaVersion)
            throw new CloudApiException("SERVER_VERSION_INCOMPATIBLE",
                "This PosApp version is not compatible with the online service.",
                System.Net.HttpStatusCode.Conflict);

        DbSeeder.ValidatePin(offlinePin);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        db.BypassStoreFilter = true;
        db.SuppressSyncCapture = true;
        var linkedAccount = await db.CloudAccountStates.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (linkedAccount != null && !string.IsNullOrWhiteSpace(linkedAccount.TenantId) &&
            !string.Equals(linkedAccount.TenantId, result.OrganizationId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "This local database is linked to another online organization. Use a separate database or complete the documented reconciliation workflow.");
        var setupComplete = await db.Settings.AsNoTracking().AnyAsync(value =>
            value.Key == SetupService.SetupCompleteKey && value.Value == "true", cancellationToken);
        var requiresDataReview = setupComplete &&
                                 InitialSyncOutboxBuilder.HasUnlinkedRecords(db, result.OrganizationId);
        var reconciliationBackupPath = linkedAccount?.ReconciliationBackupPath;
        if (requiresDataReview && linkedAccount?.RequiresReconciliation != true)
            reconciliationBackupPath = await _backup.CreateBackupAsync(retentionCount: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var committed = false;
        try
        {
            var localUser = await UpsertLocalUserAsync(db, result.User, offlinePin, cancellationToken);
            result.LocalUser = localUser;

            var state = await db.CloudAccountStates.SingleOrDefaultAsync(cancellationToken) ?? new CloudAccountState();
            if (db.Entry(state).State == EntityState.Detached) db.CloudAccountStates.Add(state);
            if (!string.IsNullOrWhiteSpace(state.TenantId) && !string.IsNullOrWhiteSpace(state.CurrentStoreId))
                await PreserveCursorAsync(db, state, cancellationToken);
            state.ApiBaseUrl = apiBaseUrl.Trim().TrimEnd('/');
            state.TenantId = result.OrganizationId;
            state.TenantName = result.OrganizationName;
            state.CurrentStoreId = result.Store.Id;
            state.CurrentStoreName = result.Store.Name;
            state.CurrentCloudUserId = result.User.Id;
            state.DeviceId = result.DeviceId;
            state.DeviceName = string.IsNullOrWhiteSpace(deviceName) ? Environment.MachineName : deviceName.Trim();
            state.IsEnabled = true;
            state.IsDeviceRevoked = false;
            state.LastLoginAtUtc = DateTime.UtcNow;
            state.ServerApiVersion = result.ApiVersion;
            state.ServerSchemaVersion = result.SchemaVersion;
            if (requiresDataReview)
            {
                state.RequiresReconciliation = true;
                state.ReconciliationBackupPath = reconciliationBackupPath;
            }
            state.LastServerCursor = await ReadCursorAsync(db, result.OrganizationId, result.Store.Id,
                result.DeviceId, cancellationToken);
            state.UpdatedAtUtc = DateTime.UtcNow;

            var cachedStore = await db.CloudCachedStores.SingleOrDefaultAsync(
                value => value.CloudStoreId == result.Store.Id, cancellationToken);
            if (cachedStore == null)
                db.CloudCachedStores.Add(new CloudCachedStore
                {
                    CloudStoreId = result.Store.Id,
                    TenantId = result.Store.TenantId,
                    Name = result.Store.Name,
                    Code = result.Store.Code,
                    IsActive = true
                });

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            committed = true;
            _profiles.UpdateActiveProfile(result.OrganizationId, result.OrganizationName,
                result.Store.Name, result.User.Username);
            await _session.SetAuthenticatedAsync(state, result.Tokens, result.User, cancellationToken);
            if (setupComplete)
            {
                await _sync.StartAsync(cancellationToken);
                _ = _sync.SyncNowAsync(false, CancellationToken.None);
            }
            return result;
        }
        catch
        {
            if (!committed)
                await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<User> UpsertLocalUserAsync(
        AppDbContext db,
        CloudUserProfile cloudUser,
        string offlinePin,
        CancellationToken cancellationToken)
    {
        var identity = await db.SyncIdentities.SingleOrDefaultAsync(value =>
            value.EntityType == "users" && value.RecordId == cloudUser.Id, cancellationToken);
        User? user = identity == null ? null : await db.Users.FindAsync(new object[] { identity.LocalId }, cancellationToken);
        user ??= await db.Users.FirstOrDefaultAsync(value => value.Username == cloudUser.Username, cancellationToken);

        if (user != null && identity == null)
        {
            var existingIdentity = await db.SyncIdentities.AsNoTracking().SingleOrDefaultAsync(value =>
                value.EntityType == "users" && value.LocalId == user.Id, cancellationToken);
            if (existingIdentity != null)
                throw new InvalidOperationException(
                    "That local user is already linked to another online account. Reconcile the user mapping before signing in.");
        }

        var (hash, salt) = DbSeeder.HashPin(offlinePin);
        if (user == null)
        {
            user = new User
            {
                Username = cloudUser.Username,
                FullName = cloudUser.FullName,
                Email = cloudUser.Email,
                Role = cloudUser.Role,
                IsActive = cloudUser.IsActive,
                PasswordHash = hash,
                PasswordSalt = salt
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            user.FullName = cloudUser.FullName;
            user.Email = cloudUser.Email;
            user.Role = cloudUser.Role;
            user.IsActive = cloudUser.IsActive;
            user.PasswordHash = hash;
            user.PasswordSalt = salt;
            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        if (identity == null)
        {
            db.SyncIdentities.Add(new SyncIdentity
            {
                EntityType = "users",
                LocalId = user.Id,
                RecordId = cloudUser.Id,
                TenantId = cloudUser.TenantId,
                ServerVersion = 1
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        return user;
    }

    private async Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var state = await db.CloudAccountStates.SingleOrDefaultAsync(cancellationToken);
        if (state != null && !string.IsNullOrWhiteSpace(state.DeviceId)) return state.DeviceId;

        var deviceId = Guid.NewGuid().ToString("D");
        if (state == null)
        {
            state = new CloudAccountState
            {
                DeviceId = deviceId,
                DeviceName = Environment.MachineName,
                IsEnabled = false
            };
            db.CloudAccountStates.Add(state);
        }
        else
        {
            state.DeviceId = deviceId;
            state.DeviceName = Environment.MachineName;
            state.UpdatedAtUtc = DateTime.UtcNow;
        }
        db.SuppressSyncCapture = true;
        await db.SaveChangesAsync(cancellationToken);
        return deviceId;
    }

    private static async Task PreserveCursorAsync(
        AppDbContext db,
        CloudAccountState state,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.TenantId) || string.IsNullOrWhiteSpace(state.CurrentStoreId) ||
            string.IsNullOrWhiteSpace(state.DeviceId)) return;
        var cursor = await db.SyncCursorStates.SingleOrDefaultAsync(value =>
            value.TenantId == state.TenantId && value.StoreId == state.CurrentStoreId &&
            value.DeviceId == state.DeviceId, cancellationToken);
        if (cursor == null)
        {
            db.SyncCursorStates.Add(new SyncCursorState
            {
                TenantId = state.TenantId,
                StoreId = state.CurrentStoreId,
                DeviceId = state.DeviceId,
                Cursor = state.LastServerCursor,
                LastPullAtUtc = state.LastSuccessfulSyncAtUtc
            });
        }
        else
        {
            cursor.Cursor = Math.Max(cursor.Cursor, state.LastServerCursor);
            cursor.LastPullAtUtc ??= state.LastSuccessfulSyncAtUtc;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<long> ReadCursorAsync(
        AppDbContext db,
        string tenantId,
        string storeId,
        string deviceId,
        CancellationToken cancellationToken)
        => await db.SyncCursorStates.AsNoTracking()
            .Where(value => value.TenantId == tenantId && value.StoreId == storeId && value.DeviceId == deviceId)
            .Select(value => (long?)value.Cursor)
            .SingleOrDefaultAsync(cancellationToken) ?? 0;

    private async Task<LocalUserRecordLink?> EnsureLocalUserRecordIdentityAsync(
        string username,
        CancellationToken cancellationToken)
    {
        var account = _session.Account ?? throw new InvalidOperationException("Sign in to manage online users.");
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        db.BypassStoreFilter = true;
        db.SuppressSyncCapture = true;
        var normalized = username.ToLowerInvariant();
        var localUser = await db.Users.SingleOrDefaultAsync(
            value => value.Username.ToLower() == normalized, cancellationToken);
        if (localUser == null) return null;

        var identity = await db.SyncIdentities.SingleOrDefaultAsync(value =>
            value.EntityType == "users" && value.LocalId == localUser.Id, cancellationToken);
        if (identity == null)
        {
            identity = new SyncIdentity
            {
                EntityType = "users",
                LocalId = localUser.Id,
                RecordId = Guid.NewGuid().ToString("D"),
                TenantId = account.TenantId,
                ServerVersion = 0
            };
            db.SyncIdentities.Add(identity);
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (!string.Equals(identity.TenantId, account.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "That local user is already linked to another online organization. Reconcile the database before continuing.");
        }

        return new LocalUserRecordLink(localUser.Id, identity.RecordId);
    }

    private async Task ReconcileLocalUserRecordIdentityAsync(
        LocalUserRecordLink localLink,
        string serverRecordId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(serverRecordId, out _))
            throw new InvalidOperationException("The server returned an invalid synchronized user ID.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        db.BypassStoreFilter = true;
        db.SuppressSyncCapture = true;
        var identity = await db.SyncIdentities.SingleAsync(value =>
            value.EntityType == "users" && value.LocalId == localLink.LocalId, cancellationToken);
        var collision = await db.SyncIdentities.AsNoTracking().AnyAsync(value =>
            value.RecordId == serverRecordId && value.Id != identity.Id, cancellationToken);
        if (collision)
            throw new InvalidOperationException(
                "The online user is already linked to a different local record. Resolve the synchronization conflict first.");

        identity.RecordId = serverRecordId;
        identity.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static object BuildDevice(string deviceId, string deviceName) => new
    {
        id = deviceId,
        name = string.IsNullOrWhiteSpace(deviceName) ? Environment.MachineName : deviceName.Trim(),
        operatingSystem = Environment.OSVersion.VersionString,
        machineName = Environment.MachineName
    };

    private static void ValidateLoginRequest(CloudLoginRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (CloudDeploymentSettings.IsConfigured)
            request.ApiBaseUrl = CloudDeploymentSettings.ApiBaseUrl;
        _ = CloudApiClient.BuildUri(request.ApiBaseUrl, "/api/v1/meta");
        if (string.IsNullOrWhiteSpace(request.UsernameOrEmail) || request.UsernameOrEmail.Length > 255)
            throw new ArgumentException("Username or email is required.");
        ValidateCloudPassword(request.Password);
        DbSeeder.ValidatePin(request.OfflinePin);
    }

    private static void ValidateCloudPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length is < 10 or > 128 ||
            !password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            throw new ArgumentException(
                "The online password must contain 10 to 128 characters and include at least one letter and one number.");
    }

    private sealed class OkEnvelope { public bool Ok { get; set; } }
    private sealed class StoresEnvelope { public IReadOnlyList<CloudStoreDto> Stores { get; set; } = Array.Empty<CloudStoreDto>(); }
    private sealed class SessionsEnvelope { public IReadOnlyList<CloudDeviceSessionDto> Sessions { get; set; } = Array.Empty<CloudDeviceSessionDto>(); }
    private sealed class UsersEnvelope { public IReadOnlyList<CloudUserProfile> Users { get; set; } = Array.Empty<CloudUserProfile>(); }
    private sealed class CreatedEnvelope { public string Id { get; set; } = string.Empty; }
    private sealed record LocalUserRecordLink(int LocalId, string RecordId);
}
