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
}
