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
    private static StoreSettings? _cache;

    public SettingsService(AppDbContext db) => _db = db;

    public async Task<string?> GetAsync(string key)
    {
        var s = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key);
        return s?.Value;
    }

    public async Task SetAsync(string key, string? value)
    {
        var s = await _db.Settings.FirstOrDefaultAsync(x => x.Key == key);
        if (s == null)
        {
            s = new Setting { Key = key, Value = value };
            _db.Settings.Add(s);
        }
        else
        {
            s.Value = value;
            s.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        _cache = null;
    }

    public async Task<StoreSettings> GetStoreSettingsAsync()
    {
        if (_cache != null) return _cache;
        var raw = await GetAsync("store:config");
        if (string.IsNullOrEmpty(raw))
        {
            _cache = new StoreSettings();
            return _cache;
        }
        try
        {
            _cache = JsonSerializer.Deserialize<StoreSettings>(raw) ?? new StoreSettings();
        }
        catch
        {
            _cache = new StoreSettings();
        }
        return _cache;
    }

    public async Task SetStoreSettingsAsync(StoreSettings settings)
    {
        await SetAsync("store:config", JsonSerializer.Serialize(settings));
        _cache = settings;
    }
}
