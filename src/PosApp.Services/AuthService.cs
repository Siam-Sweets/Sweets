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
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username && u.IsActive);
        if (user == null) return null;
        if (!DbSeeder.VerifyPin(password, user.PasswordHash, user.PasswordSalt))
            return null;
        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return user;
    }

    public Task<bool> ChangePasswordAsync(int userId, string newPassword)
    {
        var (hash, salt) = DbSeeder.HashPin(newPassword);
        var user = _db.Users.Find(userId);
        if (user == null) return Task.FromResult(false);
        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        user.UpdatedAt = DateTime.UtcNow;
        return _db.SaveChangesAsync().ContinueWith(t => t.Result > 0);
    }

    public string HashPassword(string password, out string salt)
    {
        var (hash, s) = DbSeeder.HashPin(password);
        salt = s;
        return hash;
    }

    public bool VerifyPassword(string password, string hash, string salt)
        => DbSeeder.VerifyPin(password, hash, salt);
}
