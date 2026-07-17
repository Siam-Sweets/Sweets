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
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static string? _cachedJson;

    public SettingsService(AppDbContext db) => _db = db;

    public async Task<string?> GetAsync(string key)
    {
        var setting = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key);
        return setting?.Value;
    }

    public async Task SetAsync(string key, string? value)
    {
        var setting = await _db.Settings.FirstOrDefaultAsync(x => x.Key == key);
        if (setting == null)
        {
            setting = new Setting { Key = key, Value = value };
            _db.Settings.Add(setting);
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        if (key == "store:config")
        {
            await CacheGate.WaitAsync();
            try { _cachedJson = value; }
            finally { CacheGate.Release(); }
        }
    }

    public async Task<StoreSettings> GetStoreSettingsAsync()
    {
        await CacheGate.WaitAsync();
        try
        {
            _cachedJson ??= await GetAsync("store:config") ?? JsonSerializer.Serialize(new StoreSettings());
            return DeserializeClone(_cachedJson);
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
        normalized.StoreName = normalized.StoreName?.Trim() ?? string.Empty;
        normalized.CurrencySymbol = normalized.CurrencySymbol?.Trim() ?? string.Empty;
        normalized.CurrencyCode = normalized.CurrencyCode?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized.StoreName.Length == 0)
            throw new InvalidOperationException("Store name is required.");
        if (normalized.CurrencySymbol.Length is < 1 or > 8)
            throw new InvalidOperationException("Currency symbol must contain 1 to 8 characters.");
        if (normalized.CurrencyCode.Length is < 3 or > 8)
            throw new InvalidOperationException("Currency code must contain 3 to 8 characters.");
        normalized.CurrencyDecimals = Math.Clamp(normalized.CurrencyDecimals, 0, 4);
        normalized.ProductGridRows = Math.Clamp(normalized.ProductGridRows, 2, 10);
        normalized.ProductGridColumns = Math.Clamp(normalized.ProductGridColumns, 2, 10);
        normalized.UiScalePercent = Math.Clamp(normalized.UiScalePercent, 90, 125);
        normalized.MessageDurationSeconds = Math.Clamp(normalized.MessageDurationSeconds, 1, 60);
        normalized.BackupRetentionCount = Math.Clamp(normalized.BackupRetentionCount, 1, 365);
        var json = JsonSerializer.Serialize(normalized);
        await SetAsync("store:config", json);
    }

    private static StoreSettings DeserializeClone(string json)
    {
        try { return JsonSerializer.Deserialize<StoreSettings>(json) ?? new StoreSettings(); }
        catch { return new StoreSettings(); }
    }
}
