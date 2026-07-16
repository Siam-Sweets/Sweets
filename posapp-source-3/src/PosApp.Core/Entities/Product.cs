using System.ComponentModel.DataAnnotations;

namespace PosApp.Core.Entities;

/// <summary>
/// A sellable item. Supports both barcode-tracked goods and open/weighted items
/// (e.g. produce sold by kilogram).
/// </summary>
public class Product
{
    public int Id { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>Primary barcode / SKU scanned at the POS.</summary>
    [MaxLength(64)]
    public string? Sku { get; set; }

    /// <summary>Secondary barcode (e.g. inner pack).</summary>
    [MaxLength(64)]
    public string? Barcode { get; set; }

    public int CategoryId { get; set; }
    public Category? Category { get; set; }

    /// <summary>Selling price per unit (or per kg for weighted items).</summary>
    public decimal Price { get; set; }

    /// <summary>Cost price used for profit calculations.</summary>
    public decimal CostPrice { get; set; }

    /// <summary>Tax rate applied to this product (percentage). 0 = tax exempt.</summary>
    public decimal TaxRate { get; set; } = 0m;

    public UnitOfMeasure Unit { get; set; } = UnitOfMeasure.Piece;

    /// <summary>Current stock quantity. Null = non-tracked (service item).</summary>
    public decimal? StockQuantity { get; set; }

    /// <summary>Low-stock threshold for alerts.</summary>
    public decimal? LowStockThreshold { get; set; }

    /// <summary>Path or base64 of product image.</summary>
    [MaxLength(2048)]
    public string? ImagePath { get; set; }

    public bool IsWeighted { get; set; } = false;

    public bool IsActive { get; set; } = true;

    public bool AllowDiscount { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<SaleItem> SaleItems { get; set; } = new List<SaleItem>();
    public ICollection<StockTransaction> StockTransactions { get; set; } = new List<StockTransaction>();
}

public enum UnitOfMeasure
{
    Piece = 0,
    Kilogram = 1,
    Gram = 2,
    Liter = 3,
    Milliliter = 4,
    Meter = 5,
    Pack = 6
}
