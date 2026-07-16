using System.ComponentModel.DataAnnotations;

namespace PosApp.Core.Entities;

/// <summary>
/// A customer record. Optional on a sale - walk-ins use the built-in "Guest" customer.
/// </summary>
public class Customer
{
    public int Id { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(20)]
    public string? Phone { get; set; }

    [MaxLength(100)]
    public string? Email { get; set; }

    [MaxLength(300)]
    public string? Address { get; set; }

    [MaxLength(20)]
    public string? TaxId { get; set; }

    /// <summary>Legacy balance retained so existing databases remain compatible.</summary>
    public decimal LoyaltyPoints { get; set; }

    /// <summary>Legacy balance retained so existing databases remain compatible.</summary>
    public decimal StoreCredit { get; set; }

    /// <summary>Legacy earning rate; inactive while loyalty is disabled.</summary>
    public decimal LoyaltyRate { get; set; } = 0m;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<Sale> Sales { get; set; } = new List<Sale>();
}
