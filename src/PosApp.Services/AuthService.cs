using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Data;

namespace PosApp.Services;

public class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    public AuthService(AppDbContext db) => _db = db;

    public async Task<User?> LoginAsync(string username, string password)
    {
        var normalized = username.Trim();
        var user = await _db.Users.FirstOrDefaultAsync(u =>
            u.Username.ToLower() == normalized.ToLower() && u.IsActive);
        if (user == null || !DbSeeder.VerifyPin(password, user.PasswordHash, user.PasswordSalt))
            return null;

        if (DbSeeder.IsLegacyHash(user.PasswordHash))
        {
            var (newHash, newSalt) = DbSeeder.HashPin(password);
            user.PasswordHash = newHash;
            user.PasswordSalt = newSalt;
            user.UpdatedAt = DateTime.UtcNow;
        }
        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return user;
    }

    public async Task<bool> ChangePasswordAsync(int userId, string newPassword)
    {
        DbSeeder.ValidatePin(newPassword);
        var user = await _db.Users.FindAsync(userId);
        if (user == null) return false;
        var (hash, salt) = DbSeeder.HashPin(newPassword);
        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        user.UpdatedAt = DateTime.UtcNow;
        return await _db.SaveChangesAsync() > 0;
    }

    public string HashPassword(string password, out string salt)
    {
        var (hash, generatedSalt) = DbSeeder.HashPin(password);
        salt = generatedSalt;
        return hash;
    }

    public bool VerifyPassword(string password, string hash, string salt)
        => DbSeeder.VerifyPin(password, hash, salt);
}
