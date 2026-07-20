using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Data;

namespace PosApp.Services;

/// <summary>
/// Persists the one-time, entirely local setup state and replaces the seeded
/// administrator credentials with values chosen by the store owner.
/// </summary>
public sealed class SetupService : ISetupService
{
    public const string SetupCompleteKey = "app:setup-complete";
    private const string OnlineSetupPreparedKey = "app:online-setup-prepared";

    private readonly AppDbContext _db;
    private readonly ISettingsService _settings;

    public SetupService(AppDbContext db, ISettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    public async Task<bool> IsSetupCompleteAsync()
    {
        var value = await _settings.GetAsync(SetupCompleteKey);
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

    public async Task CompleteSetupAsync(InitialSetupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.StoreSettings);

        if (await _db.CloudAccountStates.AsNoTracking().AnyAsync(value => value.TenantId != string.Empty))
            throw new InvalidOperationException(
                "This installation is already linked to an online organization. Finish online setup instead of creating a separate local administrator.");

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

            await _db.SaveChangesAsync();
            if (request.IncludeSampleProducts)
                await DbSeeder.SeedSampleProductsAsync(_db);

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        // The completion flag is deliberately written last. If saving either
        // configuration step fails, the wizard safely opens again next start.
        await _settings.SetStoreSettingsAsync(request.StoreSettings);
        await _settings.SetAsync(SetupCompleteKey, "true");
    }

    public async Task<bool> CompleteOnlineSetupAsync(
        CloudAuthenticationResult authentication,
        bool createdOrganization)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        var currentUser = authentication.LocalUser
                          ?? throw new InvalidOperationException("Online sign-in did not create an offline user profile.");
        if (string.IsNullOrWhiteSpace(authentication.Store.Name))
            throw new InvalidOperationException("The online account did not provide an assigned store.");

        _db.BypassStoreFilter = true;
        _db.SuppressSyncCapture = true;
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var prepared = await _db.Settings.SingleOrDefaultAsync(value => value.Key == OnlineSetupPreparedKey);
            var preparation = ReadPreparation(prepared?.Value);
            var resuming = string.Equals(preparation?.OrganizationId, authentication.OrganizationId,
                StringComparison.OrdinalIgnoreCase);
            var requiresInitialMigration = resuming
                ? preparation!.RequiresInitialMigration
                : createdOrganization;
            // Setup blocks the operational UI, so business rows here indicate a
            // restored or manually modified database and must never be discarded
            // as if this were an empty second device.
            if (!resuming)
            {
                var hasBusinessData = await _db.Products.AnyAsync() || await _db.Sales.AnyAsync() ||
                                      await _db.PurchaseDocuments.AnyAsync() || await _db.Customers.AnyAsync() ||
                                      await _db.Suppliers.AnyAsync() || await _db.Expenses.AnyAsync() ||
                                      await _db.CashSessions.AnyAsync() || await _db.CashMovements.AnyAsync();
                if (hasBusinessData)
                    throw new InvalidOperationException(
                        "This unconfigured database already contains business data. Complete local setup first, then use the reviewed initial-migration workflow.");
            }

            // Releases before 2.0 created bootstrap users with known demonstration
            // PINs. Retain only the freshly authenticated cloud user on an
            // unconfigured installation.
            if (!resuming)
            {
                var unusedUsers = await _db.Users.Where(user => user.Id != currentUser.Id).ToListAsync();
                _db.Users.RemoveRange(unusedUsers);
            }

            // A device joining an existing organization must not retain local
            // catalog templates that are absent from that organization. A newly
            // created organization keeps the templates and uploads them under the
            // protected initial-migration lease.
            if (!resuming && !createdOrganization)
            {
                _db.Discounts.RemoveRange(await _db.Discounts.ToListAsync());
                _db.Taxes.RemoveRange(await _db.Taxes.ToListAsync());
                _db.Categories.RemoveRange(await _db.Categories.ToListAsync());
            }

            var storeConfig = await _db.Settings.SingleOrDefaultAsync(value => value.Key == "store:config");
            var settings = storeConfig?.Value == null
                ? new StoreSettings()
                : JsonSerializer.Deserialize<StoreSettings>(storeConfig.Value) ?? new StoreSettings();
            settings.StoreName = authentication.Store.Name;
            if (storeConfig == null)
            {
                storeConfig = new Setting { Key = "store:config" };
                _db.Settings.Add(storeConfig);
            }
            storeConfig.Value = JsonSerializer.Serialize(settings);
            storeConfig.UpdatedAt = DateTime.UtcNow;

            var preparationJson = JsonSerializer.Serialize(new OnlineSetupPreparation(
                authentication.OrganizationId, requiresInitialMigration));
            if (prepared == null)
                _db.Settings.Add(new Setting { Key = OnlineSetupPreparedKey, Value = preparationJson });
            else
            {
                prepared.Value = preparationJson;
                prepared.UpdatedAt = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
            return requiresInitialMigration;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
        finally
        {
            _db.SuppressSyncCapture = false;
        }
    }

    public async Task FinalizeOnlineSetupAsync()
    {
        _db.BypassStoreFilter = true;
        _db.SuppressSyncCapture = true;
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var prepared = await _db.Settings.SingleOrDefaultAsync(value => value.Key == OnlineSetupPreparedKey)
                           ?? throw new InvalidOperationException("Online setup has not been prepared on this computer.");
            var completed = await _db.Settings.SingleOrDefaultAsync(value => value.Key == SetupCompleteKey);
            if (completed == null)
                _db.Settings.Add(new Setting { Key = SetupCompleteKey, Value = "true" });
            else
            {
                completed.Value = "true";
                completed.UpdatedAt = DateTime.UtcNow;
            }
            _db.Settings.Remove(prepared);
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
        finally
        {
            _db.SuppressSyncCapture = false;
        }
    }

    private static OnlineSetupPreparation? ReadPreparation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            return JsonSerializer.Deserialize<OnlineSetupPreparation>(value);
        }
        catch (JsonException)
        {
            // A preparation marker is device-local and may only contain an
            // organization ID if setup was interrupted by an early 2.0 build.
            return new OnlineSetupPreparation(value, RequiresInitialMigration: false);
        }
    }

    private sealed record OnlineSetupPreparation(
        string OrganizationId,
        bool RequiresInitialMigration);
}
