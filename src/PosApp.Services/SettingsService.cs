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
        var setting = await _db.Settings.FirstOrDefaultAsync(x => x.Key == normalizedKey);
        if (setting == null)
        {
            setting = new Setting { Key = normalizedKey, Value = value };
            _db.Settings.Add(setting);
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = DateTime.UtcNow;
        }
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
        await SetAsync("store:config", json);
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
        try { return JsonSerializer.Deserialize<StoreSettings>(json) ?? new StoreSettings(); }
        catch { return new StoreSettings(); }
    }
}
