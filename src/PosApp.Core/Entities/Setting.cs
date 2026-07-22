using System.ComponentModel.DataAnnotations;

namespace PosApp.Core.Entities;

/// <summary>
/// Key-value store for application settings (store info, printer config, etc.).
/// Persisted in SQLite so the user doesn't need a separate config file.
/// </summary>
public class Setting : StoreScopedEntity
{
    public int Id { get; set; }

    [MaxLength(64)]
    public string Key { get; set; } = string.Empty;

    [MaxLength(8192)]
    public string? Value { get; set; }

    [MaxLength(200)]
    public string? Description { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
