using System.ComponentModel.DataAnnotations;

namespace PosApp.Core.Entities;

/// <summary>
/// A completed or suspended sales transaction. Suspend/recall supported
/// via the <see cref="Status"/> field.
/// </summary>
public class Sale
{
    public int Id { get; set; }

    /// <summary>Human-readable receipt number, e.g. 20260716-0001.</summary>
    [MaxLength(32)]
    public string ReceiptNumber { get; set; } = string.Empty;

    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    public SaleStatus Status { get; set; } = SaleStatus.Completed;

    public DateTime SaleDate { get; set; } = DateTime.UtcNow;

    /// <summary>Subtotal before discounts and taxes.</summary>
    public decimal Subtotal { get; set; }

    /// <summary>Sum of all item-level and cart-level discounts.</summary>
    public decimal DiscountTotal { get; set; }

    /// <summary>Sum of all taxes applied.</summary>
    public decimal TaxTotal { get; set; }

    /// <summary>Rounding adjustment (e.g. half-cent rounding for cash).</summary>
    public decimal Rounding { get; set; }

    public decimal Total => Subtotal - DiscountTotal + TaxTotal + Rounding;

    /// <summary>Gross amount received from the customer before returning change.</summary>
    public decimal AmountPaid { get; set; }

    /// <summary>Change given back to the customer.</summary>
    public decimal Change { get; set; }

    public ICollection<SaleItem> Items { get; set; } = new List<SaleItem>();
    public ICollection<SalePayment> Payments { get; set; } = new List<SalePayment>();

    [MaxLength(500)]
    public string? Note { get; set; }

    /// <summary>If this sale is a refund, references the original sale.</summary>
    public int? RefundedSaleId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

public enum SaleStatus
{
    Completed = 0,
    Suspended = 1,
    Voided = 2,
    Refunded = 3
}
