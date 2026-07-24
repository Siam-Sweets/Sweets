using System.ComponentModel.DataAnnotations;

namespace PosApp.Core.Entities;

/// <summary>
/// A discount definition. Can be percentage or fixed amount, and can be
/// applied either per-line or to the whole cart.
/// </summary>
public class Discount : StoreScopedEntity
{
    public int Id { get; set; }

    [MaxLength(60)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Description { get; set; }

    public DiscountType Type { get; set; }

    /// <summary>If percentage: 0-100. If fixed: currency amount.</summary>
    public decimal Value { get; set; }

    /// <summary>Optional promo code that triggers this discount when typed at POS.</summary>
    [MaxLength(40)]
    public string? Code { get; set; }

    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }

    public int? MaxUses { get; set; }
    public int UsedCount { get; set; }

    /// <summary>Optimistic concurrency token for promotion usage.</summary>
    public long UsageVersion { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum DiscountType
{
    Percentage = 0,
    FixedAmount = 1
}
