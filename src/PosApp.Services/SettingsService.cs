using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Data;

namespace PosApp.Services;

public class SettingsService : ISettingsService
{
    private readonly AppDbContext _db;
    private readonly IStoreContext _storeContext;
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static readonly Dictionary<int, string?> CachedJsonByStore = new();

    public SettingsService(AppDbContext db, IStoreContext storeContext)
    {
        _db = db;
        _storeContext = storeContext;
    }

    public async Task<string?> GetAsync(string key)
    {
        var normalizedKey = NormalizeKey(key);
        var setting = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == normalizedKey);
        return setting?.Value;
    }

    public async Task SetAsync(string key, string? value)
    {
        var normalizedKey = NormalizeKey(key);
        if (value?.Length > 8192)
            throw new InvalidOperationException("A setting value cannot exceed 8192 characters.");
        UpsertSettingValue(
            await FindOrCreateSettingAsync(normalizedKey),
            normalizedKey,
            value);
        await _db.SaveChangesAsync();
        if (normalizedKey == "store:config")
        {
            await CacheGate.WaitAsync();
            try { CachedJsonByStore[_storeContext.StoreId] = value; }
            finally { CacheGate.Release(); }
        }
    }

    public async Task<StoreSettings> GetStoreSettingsAsync()
    {
        await CacheGate.WaitAsync();
        try
        {
            if (!CachedJsonByStore.TryGetValue(_storeContext.StoreId, out var json) || json == null)
            {
                json = await GetAsync("store:config") ?? JsonSerializer.Serialize(new StoreSettings());
                CachedJsonByStore[_storeContext.StoreId] = json;
            }
            return DeserializeClone(json);
        }
        finally
        {
            CacheGate.Release();
        }
    }

    public async Task SetStoreSettingsAsync(StoreSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = DeserializeClone(JsonSerializer.Serialize(settings));
        normalized.StoreName = NormalizeText(normalized.StoreName, 100, "Store name", required: true);
        normalized.Address = NormalizeText(normalized.Address, 500, "Store address");
        normalized.Phone = NormalizeText(normalized.Phone, 30, "Store phone");
        normalized.Email = NormalizeText(normalized.Email, 255, "Store email");
        normalized.TaxId = NormalizeText(normalized.TaxId, 40, "Tax ID");
        normalized.Country = NormalizeText(normalized.Country, 100, "Country");
        normalized.FooterNote = NormalizeText(normalized.FooterNote, 500, "Receipt footer");
        normalized.ReceiptPrinterName = NormalizeText(
            normalized.ReceiptPrinterName, 260, "Printer name");
        normalized.CurrencySymbol = normalized.CurrencySymbol?.Trim() ?? string.Empty;
        normalized.CurrencyCode = normalized.CurrencyCode?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized.CurrencySymbol.Length is < 1 or > 8)
            throw new InvalidOperationException("Currency symbol must contain 1 to 8 characters.");
        if (normalized.CurrencyCode.Length is < 3 or > 8)
            throw new InvalidOperationException("Currency code must contain 3 to 8 characters.");
        normalized.CurrencyDecimals = Math.Clamp(normalized.CurrencyDecimals, 0, 4);
        if (normalized.DefaultTaxRate is < 0m or > 100m)
            throw new InvalidOperationException("Default tax must be between 0 and 100.");
        normalized.ReceiptWidth = Math.Clamp(normalized.ReceiptWidth, 40, 120);
        normalized.Language = string.Equals(normalized.Language, "bn", StringComparison.OrdinalIgnoreCase)
            ? "bn" : "en";
        normalized.Theme = string.Equals(normalized.Theme, "Dark", StringComparison.OrdinalIgnoreCase)
            ? "Dark" : "Light";
        normalized.DefaultServiceType = string.IsNullOrWhiteSpace(normalized.DefaultServiceType)
            ? "Retail"
            : NormalizeText(normalized.DefaultServiceType, 32, "Default service type");
        normalized.ProductGridRows = Math.Clamp(normalized.ProductGridRows, 2, 10);
        normalized.ProductGridColumns = Math.Clamp(normalized.ProductGridColumns, 2, 10);
        normalized.UiScalePercent = Math.Clamp(normalized.UiScalePercent, 90, 125);
        normalized.MessageDurationSeconds = Math.Clamp(normalized.MessageDurationSeconds, 1, 60);
        normalized.BackupRetentionCount = Math.Clamp(normalized.BackupRetentionCount, 1, 365);
        var json = JsonSerializer.Serialize(normalized);
        _db.ChangeTracker.Clear();
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            UpsertSettingValue(
                await FindOrCreateSettingAsync("store:config"),
                "store:config",
                json);

            var store = await _db.Stores.FirstOrDefaultAsync(x => x.Id == _storeContext.StoreId)
                        ?? throw new InvalidOperationException("The selected store no longer exists.");
            store.Name = normalized.StoreName;
            store.Address = normalized.Address;
            store.Phone = normalized.Phone;
            store.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await _db.CommitExternalTransactionAsync(transaction);
        }
        catch
        {
            await _db.RollbackExternalTransactionAsync(transaction);
            throw;
        }
        await CacheGate.WaitAsync();
        try { CachedJsonByStore[_storeContext.StoreId] = json; }
        finally { CacheGate.Release(); }
    }

    private async Task<Setting> FindOrCreateSettingAsync(string normalizedKey)
    {
        var storeId = _storeContext.StoreId;
        var keyLower = normalizedKey.ToLowerInvariant();

        var trackedMatches = _db.ChangeTracker.Entries<Setting>()
            .Where(entry => entry.State != EntityState.Detached &&
                            entry.State != EntityState.Deleted &&
                            string.Equals(entry.Entity.Key, normalizedKey, StringComparison.OrdinalIgnoreCase) &&
                            (entry.Entity.StoreId == storeId ||
                             (entry.State == EntityState.Added && entry.Entity.StoreId <= 0)))
            .ToList();

        var persistedMatches = await _db.Settings
            .IgnoreQueryFilters()
            .Where(setting => setting.StoreId == storeId &&
                              setting.Key.ToLower() == keyLower)
            .OrderBy(setting => setting.Id)
            .ToListAsync();

        var setting = persistedMatches.FirstOrDefault()
                      ?? trackedMatches
                          .Where(entry => entry.State != EntityState.Added)
                          .OrderBy(entry => entry.Entity.Id)
                          .Select(entry => entry.Entity)
                          .FirstOrDefault()
                      ?? trackedMatches
                          .Select(entry => entry.Entity)
                          .FirstOrDefault();

        foreach (var entry in trackedMatches.Where(entry => !ReferenceEquals(entry.Entity, setting)))
        {
            if (entry.State == EntityState.Added)
                entry.State = EntityState.Detached;
            else
                _db.Settings.Remove(entry.Entity);
        }

        foreach (var duplicate in persistedMatches.Skip(1)
                     .Where(duplicate => !ReferenceEquals(duplicate, setting)))
        {
            _db.Settings.Remove(duplicate);
        }

        if (setting != null)
        {
            if (setting.StoreId <= 0)
                setting.StoreId = storeId;
            return setting;
        }

        setting = new Setting
        {
            StoreId = storeId,
            Key = normalizedKey
        };
        _db.Settings.Add(setting);
        return setting;
    }

    private static void UpsertSettingValue(Setting setting, string normalizedKey, string? value)
    {
        if (setting.Id == 0)
            setting.Key = normalizedKey;
        setting.Value = value;
        setting.UpdatedAt = DateTime.UtcNow;
    }


    internal static void InvalidateStoreCache(int storeId)
    {
        CacheGate.Wait();
        try { CachedJsonByStore.Remove(storeId); }
        finally { CacheGate.Release(); }
    }

    private static string NormalizeKey(string? key)
    {
        var normalized = key?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > 64)
            throw new InvalidOperationException("Setting key must contain 1 to 64 characters.");
        return normalized;
    }

    private static string NormalizeText(
        string? value, int maximum, string field, bool required = false)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (required && normalized.Length == 0)
            throw new InvalidOperationException($"{field} is required.");
        if (normalized.Length > maximum)
            throw new InvalidOperationException($"{field} cannot exceed {maximum} characters.");
        return normalized;
    }

    private static StoreSettings DeserializeClone(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<StoreSettings>(json)
                   ?? throw new InvalidOperationException("Store settings are empty or invalid.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Store settings are corrupted. Restore a backup or save corrected settings before continuing.", ex);
        }
    }
}
