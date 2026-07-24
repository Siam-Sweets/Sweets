using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Core.Utilities;
using PosApp.Data;

namespace PosApp.Services;

/// <summary>
/// Prepares the local offline cache for an online organization and writes the
/// device-only completion marker only after the initial cloud upload succeeds.
/// </summary>
public sealed class SetupService : ISetupService
{
    private readonly AppDbContext _db;
    private readonly ISettingsService _settings;
    private readonly IStoreContext _storeContext;

    public SetupService(
        AppDbContext db,
        ISettingsService settings,
        IStoreContext storeContext)
    {
        _db = db;
        _settings = settings;
        _storeContext = storeContext;
    }

    public async Task<bool> IsSetupCompleteAsync()
    {
        var value = await _settings.GetAsync(SettingSyncPolicy.SetupCompleteKey);
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<InitialSetupRequest> GetSetupDefaultsAsync()
    {
        var administrator = await _db.Users.AsNoTracking()
            .Where(user => user.Role == UserRole.Admin)
            .OrderBy(user => user.Id)
            .FirstOrDefaultAsync();

        return new InitialSetupRequest
        {
            StoreSettings = await _settings.GetStoreSettingsAsync(),
            AdminFullName = administrator?.FullName ?? "Administrator",
            AdminUsername = administrator?.Username ?? "admin"
        };
    }

    public Task<string?> GetPreparedOnlineAccountEmailAsync()
        => _settings.GetAsync(SettingSyncPolicy.SetupAccountEmailKey);

    public async Task PrepareOnlineSetupAsync(
        InitialSetupRequest request,
        string accountEmail)
    {
        var normalizedEmail = accountEmail?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalizedEmail.Length is < 3 or > 254 || !normalizedEmail.Contains('@'))
            throw new InvalidOperationException("Enter a valid email address.");

        await PrepareSetupCoreAsync(request);
        using (_storeContext.SuppressCloudCapture())
        {
            await _settings.SetAsync(SettingSyncPolicy.SetupAccountEmailKey, normalizedEmail);
            await _settings.SetAsync(SettingSyncPolicy.SetupPreparedKey, "true");
        }
    }

    public async Task FinalizeOnlineSetupAsync()
    {
        using (_storeContext.SuppressCloudCapture())
        {
            // Completion is deliberately written only after authentication and
            // the full initial cloud snapshot have succeeded.
            await _settings.SetAsync(SettingSyncPolicy.SetupCompleteKey, "true");
            await _settings.SetAsync(SettingSyncPolicy.SetupPreparedKey, "false");
        }
    }

    public async Task CompleteSetupAsync(InitialSetupRequest request)
    {
        // Kept for callers upgrading from the older local wizard contract.
        await PrepareSetupCoreAsync(request);
        await FinalizeOnlineSetupAsync();
    }

    private async Task PrepareSetupCoreAsync(InitialSetupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.StoreSettings);

        var storeName = request.StoreSettings.StoreName?.Trim() ?? string.Empty;
        var fullName = request.AdminFullName?.Trim() ?? string.Empty;
        var username = request.AdminUsername?.Trim() ?? string.Empty;
        var pin = request.AdminPin?.Trim() ?? string.Empty;

        if (storeName.Length is < 2 or > 100)
            throw new InvalidOperationException("Store name must contain 2 to 100 characters.");
        if (fullName.Length is < 2 or > 100)
            throw new InvalidOperationException("Administrator name must contain 2 to 100 characters.");
        if (username.Length is < 3 or > 60 ||
            username.Any(character => !char.IsLetterOrDigit(character) &&
                                      character != '_' && character != '-' && character != '.'))
            throw new InvalidOperationException("Username must contain 3 to 60 letters, numbers, dots, dashes, or underscores.");
        if (pin.Length is < 4 or > 12 || pin.Any(character => !char.IsDigit(character)))
            throw new InvalidOperationException("Administrator PIN must contain 4 to 12 digits.");
        if (string.IsNullOrWhiteSpace(request.StoreSettings.CurrencySymbol) ||
            request.StoreSettings.CurrencySymbol.Trim().Length > 8)
            throw new InvalidOperationException("Currency symbol is required and cannot exceed 8 characters.");

        request.StoreSettings.StoreName = storeName;
        request.StoreSettings.Phone = request.StoreSettings.Phone?.Trim() ?? string.Empty;
        request.StoreSettings.Address = request.StoreSettings.Address?.Trim() ?? string.Empty;
        request.StoreSettings.CurrencySymbol = request.StoreSettings.CurrencySymbol?.Trim() ?? string.Empty;
        request.StoreSettings.FooterNote = request.StoreSettings.FooterNote?.Trim() ?? string.Empty;

        using (_storeContext.SuppressCloudCapture())
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();
            try
            {
                var administrator = await _db.Users
                    .Where(user => user.Role == UserRole.Admin)
                    .OrderBy(user => user.Id)
                    .FirstOrDefaultAsync();

                var normalizedUsername = username.ToLower();
                var administratorId = administrator?.Id ?? 0;
                var usernameInUse = await _db.Users.AnyAsync(user =>
                    user.Id != administratorId && user.Username.ToLower() == normalizedUsername);
                if (usernameInUse)
                    throw new InvalidOperationException("That username is already used by another account.");

                var (hash, salt) = DbSeeder.HashPin(pin);
                if (administrator == null)
                {
                    administrator = new User
                    {
                        Username = username,
                        FullName = fullName,
                        PasswordHash = hash,
                        PasswordSalt = salt,
                        Role = UserRole.Admin,
                        IsActive = true
                    };
                    _db.Users.Add(administrator);
                }
                else
                {
                    administrator.Username = username;
                    administrator.FullName = fullName;
                    administrator.PasswordHash = hash;
                    administrator.PasswordSalt = salt;
                    administrator.IsActive = true;
                    administrator.UpdatedAt = DateTime.UtcNow;
                }

                // The old offline wizard seeded a public cashier/1111 account.
                // Online-first onboarding must never upload that shared default
                // credential into a newly created organization.
                var starterCashier = await _db.Users.FirstOrDefaultAsync(user =>
                    user.Username == "cashier" &&
                    user.FullName == "Default Cashier" &&
                    user.Role == UserRole.Cashier &&
                    user.LastLoginAt == null);
                if (starterCashier != null &&
                    !await _db.Sales.AnyAsync(sale => sale.UserId == starterCashier.Id) &&
                    !await _db.PurchaseDocuments.AnyAsync(
                        purchase => purchase.UserId == starterCashier.Id))
                {
                    _db.Users.Remove(starterCashier);
                }

                var currentStore = await _db.Stores.FirstOrDefaultAsync(
                    store => store.Id == _db.CurrentStoreId);
                if (currentStore != null)
                {
                    currentStore.Name = storeName;
                    currentStore.Address = request.StoreSettings.Address;
                    currentStore.Phone = request.StoreSettings.Phone;
                    currentStore.UpdatedAt = DateTime.UtcNow;
                }

                await _db.SaveChangesAsync();
                if (request.IncludeSampleProducts)
                    await DbSeeder.SeedSampleProductsAsync(_db);

                await _db.CommitExternalTransactionAsync(transaction);
            }
            catch
            {
                await _db.RollbackExternalTransactionAsync(transaction);
                throw;
            }

            await _settings.SetStoreSettingsAsync(request.StoreSettings);
        }
    }
}
