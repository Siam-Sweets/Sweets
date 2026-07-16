using System.ComponentModel.DataAnnotations;

namespace PosApp.Core.Entities;

/// <summary>
/// A logical group of products (e.g. Beverages, Snacks, Groceries).
/// </summary>
public class Category
{
    public int Id { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>HTML color used for the product tile accent in the POS grid.</summary>
    [MaxLength(20)]
    public string Color { get; set; } = "#2D7FF9";

    /// <summary>Display ordering (lower comes first).</summary>
    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<Product> Products { get; set; } = new List<Product>();
}
