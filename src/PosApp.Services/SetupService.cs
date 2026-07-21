using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Data;

namespace PosApp.Services;

/// <summary>
/// Controls the online-only first-run boundary. A PosApp installation becomes
/// operational only after it is linked to an online organization and either the
/// complete local snapshot has been uploaded or the complete store snapshot has
/// been downloaded successfully.
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
        if (!string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
            return false;

        // Older releases could mark a completely local setup as finished. From
        // 2.0.17 onward that marker is valid only when this database is linked to
        // a real organization and store.
        return await _db.CloudAccountStates.AsNoTracking().AnyAsync(state =>
            state.TenantId != string.Empty && state.CurrentStoreId != string.Empty);
    }

    public async Task<bool> CompleteOnlineSetupAsync(
        CloudAuthenticationResult authentication,
        bool createdOrganization,
        StoreSettings? initialStoreSettings = null,
        bool includeSampleProducts = false)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        var currentUser = authentication.LocalUser
                          ?? throw new InvalidOperationException(
                              "Online sign-in did not create a protected local user profile.");
        if (string.IsNullOrWhiteSpace(authentication.OrganizationId))
            throw new InvalidOperationException("The online account did not provide an organization.");
        if (string.IsNullOrWhiteSpace(authentication.Store.Id) ||
            string.IsNullOrWhiteSpace(authentication.Store.Name))
            throw new InvalidOperationException("The online account did not provide an assigned store.");

        // Online-only onboarding never uploads an old local bootstrap database.
        // Until setup is finalized, every business row is disposable cache state.
        // Clearing it also recovers installations left behind by an interrupted
        // 2.0.17 migration attempt and guarantees that sign-in starts from the
        // authoritative organization snapshot in Turso.
        var previousBypassStoreFilter = _db.BypassStoreFilter;
        var previousSuppressSyncCapture = _db.SuppressSyncCapture;
        _db.BypassStoreFilter = true;
        _db.SuppressSyncCapture = true;
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            await RemoveUnlinkedBootstrapStateAsync(currentUser.Id);

            var prepared = await _db.Settings.IgnoreQueryFilters()
                .SingleOrDefaultAsync(value => value.Key == OnlineSetupPreparedKey);
            var preparationJson = JsonSerializer.Serialize(new OnlineSetupPreparation(
                authentication.OrganizationId,
                RequiresInitialMigration: false));
            if (prepared == null)
                _db.Settings.Add(new Setting { Key = OnlineSetupPreparedKey, Value = preparationJson });
            else
            {
                prepared.Value = preparationJson;
                prepared.UpdatedAt = DateTime.UtcNow;
            }

            var staleOperations = await _db.SyncOutboxOperations.ToListAsync();
            var staleConflicts = await _db.SyncConflicts.ToListAsync();
            var staleCursors = await _db.SyncCursorStates.ToListAsync();
            var staleIdentities = await _db.SyncIdentities
                .Where(identity => identity.EntityType != "users" || identity.LocalId != currentUser.Id)
                .ToListAsync();
            _db.SyncOutboxOperations.RemoveRange(staleOperations);
            _db.SyncConflicts.RemoveRange(staleConflicts);
            _db.SyncCursorStates.RemoveRange(staleCursors);
            _db.SyncIdentities.RemoveRange(staleIdentities);

            var state = await _db.CloudAccountStates.SingleAsync();
            state.ActiveMigrationId = null;
            state.ActiveMigrationStoreId = null;
            state.ActiveMigrationBackupPath = null;
            state.IsMigrationSnapshotQueued = false;
            state.RequiresReconciliation = false;
            state.LastServerCursor = 0;
            state.UpdatedAtUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
            return false;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
        finally
        {
            _db.SuppressSyncCapture = previousSuppressSyncCapture;
            _db.BypassStoreFilter = previousBypassStoreFilter;
        }
    }

    public async Task FinalizeOnlineSetupAsync()
    {
        var previousBypassStoreFilter = _db.BypassStoreFilter;
        var previousSuppressSyncCapture = _db.SuppressSyncCapture;
        _db.BypassStoreFilter = true;
        _db.SuppressSyncCapture = true;
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var prepared = await _db.Settings.IgnoreQueryFilters()
                               .SingleOrDefaultAsync(value => value.Key == OnlineSetupPreparedKey)
                           ?? throw new InvalidOperationException(
                               "Online setup has not been prepared on this computer.");
            var completed = await _db.Settings.IgnoreQueryFilters()
                .SingleOrDefaultAsync(value => value.Key == SetupCompleteKey);
            if (completed == null)
            {
                _db.Settings.Add(new Setting
                {
                    Key = SetupCompleteKey,
                    Value = "true"
                });
            }
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
            _db.SuppressSyncCapture = previousSuppressSyncCapture;
            _db.BypassStoreFilter = previousBypassStoreFilter;
        }
    }

    private async Task<bool> HasExistingLocalStoreDataAsync(int authenticatedUserId)
    {
        if (await _db.Products.IgnoreQueryFilters().AnyAsync() ||
            await _db.Sales.IgnoreQueryFilters().AnyAsync() ||
            await _db.PurchaseDocuments.IgnoreQueryFilters().AnyAsync() ||
            await _db.Customers.IgnoreQueryFilters().AnyAsync() ||
            await _db.Suppliers.IgnoreQueryFilters().AnyAsync() ||
            await _db.Expenses.IgnoreQueryFilters().AnyAsync() ||
            await _db.CashSessions.IgnoreQueryFilters().AnyAsync() ||
            await _db.CashMovements.IgnoreQueryFilters().AnyAsync() ||
            await _db.StockTransactions.IgnoreQueryFilters().AnyAsync() ||
            await _db.Categories.IgnoreQueryFilters().AnyAsync() ||
            await _db.Taxes.IgnoreQueryFilters().AnyAsync() ||
            await _db.Discounts.IgnoreQueryFilters().AnyAsync())
            return true;

        if (await _db.Users.IgnoreQueryFilters().AnyAsync(user => user.Id != authenticatedUserId))
            return true;

        return await _db.Settings.IgnoreQueryFilters().AnyAsync(setting =>
            !setting.Key.StartsWith("app:"));
    }

    private async Task RemoveUnlinkedBootstrapStateAsync(int authenticatedUserId)
    {
        // Delete in dependency order. These rows are an unfinished local cache,
        // not authoritative business data: online-only onboarding always
        // rebuilds them from the selected organization.
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
            "DELETE FROM \"Suppliers\""
        };
        foreach (var statement in deleteStatements)
            await _db.Database.ExecuteSqlRawAsync(statement);

        await _db.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"Settings\" WHERE \"Key\" NOT LIKE 'app:%'");

        var unusedUsers = await _db.Users.IgnoreQueryFilters()
            .Where(user => user.Id != authenticatedUserId)
            .ToListAsync();
        _db.Users.RemoveRange(unusedUsers);
    }


    private async Task UpsertUnlinkedStoreSettingsAsync(StoreSettings settings)
    {
        var existing = await _db.Settings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(value => value.Key == "store:config" &&
                !_db.SyncIdentities.Any(identity =>
                    identity.EntityType == "settings" && identity.LocalId == value.Id));
        var serialized = JsonSerializer.Serialize(settings);
        if (existing == null)
        {
            _db.Settings.Add(new Setting { Key = "store:config", Value = serialized });
        }
        else
        {
            existing.Value = serialized;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
    }

    private async Task<StoreSettings> ReadUnlinkedStoreSettingsAsync()
    {
        var setting = await _db.Settings.IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(value => value.Key == "store:config" &&
                !_db.SyncIdentities.Any(identity =>
                    identity.EntityType == "settings" && identity.LocalId == value.Id));
        if (string.IsNullOrWhiteSpace(setting?.Value))
            return new StoreSettings();

        try
        {
            return JsonSerializer.Deserialize<StoreSettings>(setting.Value) ?? new StoreSettings();
        }
        catch (JsonException)
        {
            return new StoreSettings();
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
            // An early 2.0 build stored only the organization ID. Preserve the
            // safe resume path without treating malformed remote data as trusted.
            return new OnlineSetupPreparation(value, RequiresInitialMigration: false);
        }
    }

    private sealed record OnlineSetupPreparation(
        string OrganizationId,
        bool RequiresInitialMigration);
}
