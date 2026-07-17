using System.ComponentModel.DataAnnotations;

namespace PosApp.Core.Entities;

/// <summary>
/// One line item on a sale. Captures price/discount at the moment of sale
/// so that subsequent edits to the product catalog do not retroactively
/// change historical sales.
/// </summary>
public class SaleItem
{
    public int Id { get; set; }

    public int SaleId { get; set; }
    public Sale? Sale { get; set; }

    public int ProductId { get; set; }
    public Product? Product { get; set; }

    /// <summary>Snapshot of product name at sale time.</summary>
    [MaxLength(100)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>Snapshot of SKU at sale time.</summary>
    [MaxLength(64)]
    public string? Sku { get; set; }

    public decimal Quantity { get; set; }

    /// <summary>Unit price at sale time.</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>Cost price snapshot at sale time.</summary>
    public decimal CostPrice { get; set; }

    /// <summary>Effective tax rate at sale time (percentage).</summary>
    public decimal TaxRate { get; set; }

    /// <summary>Per-line discount amount (already computed).</summary>
    public decimal DiscountAmount { get; set; }

    [MaxLength(200)]
    public string? DiscountReason { get; set; }

    public int? PromotionId { get; set; }

    public decimal LineTotal => (UnitPrice * Quantity) - DiscountAmount;

    public decimal LineTax => LineTotal * TaxRate / 100m;

    public bool IsRefunded { get; set; } = false;
}
