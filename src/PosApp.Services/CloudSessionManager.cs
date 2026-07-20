using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Data;

namespace PosApp.Services;

public sealed class CloudSessionManager
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ISecureTokenStore _tokenStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CloudSessionManager(IDbContextFactory<AppDbContext> dbFactory, ISecureTokenStore tokenStore)
    {
        _dbFactory = dbFactory;
        _tokenStore = tokenStore;
    }

    public CloudAccountState? Account { get; private set; }
    public CloudAuthTokens? Tokens { get; private set; }
    public CloudUserProfile? User { get; private set; }
    public bool IsSignedIn => Account?.IsEnabled == true && Tokens != null;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            Account = await db.CloudAccountStates.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            Tokens = await _tokenStore.LoadAsync(cancellationToken);
            if (Account?.IsEnabled == true && Tokens != null && !Account.IsDeviceRevoked)
            {
                SyncCaptureContext.Enable(Account.TenantId, Account.CurrentStoreId, Account.DeviceId,
                    Account.CurrentCloudUserId);
            }
            else
            {
                SyncCaptureContext.Disable();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAuthenticatedAsync(
        CloudAccountState account,
        CloudAuthTokens tokens,
        CloudUserProfile user,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Account = account;
            Tokens = tokens;
            User = user;
            await _tokenStore.SaveAsync(tokens, cancellationToken);
            SyncCaptureContext.Enable(account.TenantId, account.CurrentStoreId, account.DeviceId, user.Id);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateTokensAsync(CloudAuthTokens tokens, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Tokens = tokens;
            await _tokenStore.SaveAsync(tokens, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void UpdateAccount(CloudAccountState account)
    {
        Account = account;
        if (account.IsEnabled && !account.IsDeviceRevoked)
        {
            var capture = SyncCaptureContext.Current;
            var localUserId = capture.Enabled &&
                              string.Equals(capture.TenantId, account.TenantId, StringComparison.OrdinalIgnoreCase)
                ? capture.UserId
                : account.CurrentCloudUserId;
            SyncCaptureContext.Enable(account.TenantId, account.CurrentStoreId, account.DeviceId,
                localUserId);
        }
        else
            SyncCaptureContext.Disable();
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Tokens = null;
            User = null;
            await _tokenStore.ClearAsync(cancellationToken);
            SyncCaptureContext.Disable();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task InvalidateAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var state = await db.CloudAccountStates.SingleOrDefaultAsync(cancellationToken);
            if (state != null)
            {
                state.IsEnabled = false;
                state.IsDeviceRevoked = reason == "DEVICE_REVOKED";
                state.UpdatedAtUtc = DateTime.UtcNow;
                db.SuppressSyncCapture = true;
                if (reason == "USER_DISABLED" && !string.IsNullOrWhiteSpace(state.CurrentCloudUserId))
                {
                    var identity = await db.SyncIdentities.AsNoTracking().SingleOrDefaultAsync(value =>
                        value.EntityType == "users" && value.RecordId == state.CurrentCloudUserId,
                        cancellationToken);
                    if (identity != null)
                    {
                        var localUser = await db.Users.FindAsync(new object[] { identity.LocalId }, cancellationToken);
                        if (localUser != null) localUser.IsActive = false;
                    }
                }
                await db.SaveChangesAsync(cancellationToken);
                Account = state;
            }
            Tokens = null;
            User = null;
            await _tokenStore.ClearAsync(cancellationToken);
            SyncCaptureContext.Disable();
        }
        finally
        {
            _gate.Release();
        }
    }
}
