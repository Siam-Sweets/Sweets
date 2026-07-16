using System.ComponentModel.DataAnnotations;

namespace PosApp.Core.Entities;

/// <summary>
/// A point-of-sale user (cashier, manager, admin). PIN-based login.
/// </summary>
public class User
{
    public int Id { get; set; }

    [MaxLength(60)]
    public string Username { get; set; } = string.Empty;

    [MaxLength(100)]
    public string FullName { get; set; } = string.Empty;

    /// <summary>SHA256 hash + salt. PIN or password.</summary>
    [MaxLength(128)]
    public string PasswordHash { get; set; } = string.Empty;

    [MaxLength(32)]
    public string PasswordSalt { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.Cashier;

    public bool IsActive { get; set; } = true;

    [MaxLength(255)]
    public string? Email { get; set; }

    [MaxLength(2048)]
    public string? ImagePath { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    public ICollection<Sale> Sales { get; set; } = new List<Sale>();
}

public enum UserRole
{
    Cashier = 0,
    Manager = 1,
    Admin = 2
}
