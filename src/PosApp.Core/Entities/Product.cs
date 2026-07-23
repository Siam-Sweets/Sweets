using System.ComponentModel.DataAnnotations;

namespace PosApp.Core.Entities;

/// <summary>
/// A sellable item. Supports fixed-count goods and variable measured amounts
/// priced by weight, volume, or length.
/// </summary>
public class Product : StoreScopedEntity
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

    /// <summary>Selling price per selected unit of measure.</summary>
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

    /// <summary>
    /// Legacy storage flag for variable-quantity products. Weight, volume, and
    /// length units all use decimal quantities; the exact mode is derived from Unit.
    /// </summary>
    public bool IsWeighted { get; set; } = false;

    public bool RequiresMeasuredQuantity => IsWeighted || Unit.IsMeasuredUnit();

    /// <summary>
    /// Resolves the unit used at checkout. Databases created by early versions
    /// stored only IsWeighted, so those legacy rows are treated as kilograms
    /// until the product is edited and saved with an explicit unit.
    /// </summary>
    public UnitOfMeasure EffectiveUnit => Unit.GetEffectiveUnit(IsWeighted);
    public ProductSaleMode SaleMode => EffectiveUnit.GetSaleMode();
    public string UnitSymbol => EffectiveUnit.ToSymbol();

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

public enum ProductSaleMode
{
    PerItem = 0,
    Weight = 1,
    Volume = 2,
    Length = 3
}

public static class UnitOfMeasureExtensions
{
    public static bool IsMeasuredUnit(this UnitOfMeasure unit) => unit is
        UnitOfMeasure.Kilogram or UnitOfMeasure.Gram or
        UnitOfMeasure.Liter or UnitOfMeasure.Milliliter or UnitOfMeasure.Meter;

    public static ProductSaleMode GetSaleMode(this UnitOfMeasure unit, bool legacyMeasuredFlag = false)
        => unit switch
        {
            UnitOfMeasure.Kilogram or UnitOfMeasure.Gram => ProductSaleMode.Weight,
            UnitOfMeasure.Liter or UnitOfMeasure.Milliliter => ProductSaleMode.Volume,
            UnitOfMeasure.Meter => ProductSaleMode.Length,
            _ when legacyMeasuredFlag => ProductSaleMode.Weight,
            _ => ProductSaleMode.PerItem
        };

    public static UnitOfMeasure GetEffectiveUnit(
        this UnitOfMeasure unit, bool legacyMeasuredFlag = false)
        => legacyMeasuredFlag && !unit.IsMeasuredUnit()
            ? UnitOfMeasure.Kilogram
            : unit;

    public static string ToSymbol(this UnitOfMeasure unit) => unit switch
    {
        UnitOfMeasure.Kilogram => "kg",
        UnitOfMeasure.Gram => "g",
        UnitOfMeasure.Liter => "L",
        UnitOfMeasure.Milliliter => "mL",
        UnitOfMeasure.Meter => "m",
        UnitOfMeasure.Pack => "pack",
        _ => "pc"
    };
}
