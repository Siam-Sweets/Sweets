using System.ComponentModel.DataAnnotations;

namespace PosApp.Core.Entities;

/// <summary>
/// Append-only ledger of every stock movement for a product.
/// Positive Quantity = stock in, negative = stock out.
/// </summary>
public class StockTransaction
{
    public int Id { get; set; }

    public int ProductId { get; set; }
    public Product? Product { get; set; }

    public StockTransactionType Type { get; set; }

    /// <summary>Signed quantity delta.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Resulting stock level after this transaction.</summary>
    public decimal BalanceAfter { get; set; }

    /// <summary>Unit cost at the time of the transaction (for purchases).</summary>
    public decimal? UnitCost { get; set; }

    public int? SaleId { get; set; }
    public int? SaleItemId { get; set; }

    [MaxLength(500)]
    public string? Note { get; set; }

    public int? UserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum StockTransactionType
{
    Sale = 0,
    Return = 1,
    Purchase = 2,
    Adjustment = 3,
    InitialStock = 4,
    Wastage = 5,
    Transfer = 6
}
