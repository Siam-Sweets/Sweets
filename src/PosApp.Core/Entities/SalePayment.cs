using System.ComponentModel.DataAnnotations;

namespace PosApp.Core.Entities;

/// <summary>
/// A single tender against a sale. A sale may have multiple payments (split tender).
/// </summary>
public class SalePayment : StoreScopedEntity
{
    public int Id { get; set; }

    public int SaleId { get; set; }
    public Sale? Sale { get; set; }

    public PaymentMethod Method { get; set; }

    /// <summary>
    /// Amount applied to the sale. Gross cash received and returned change are
    /// stored on Sale.AmountPaid and Sale.Change so register totals remain net.
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>For card: last 4 digits, for cash: nothing, for wallet: ref id.</summary>
    [MaxLength(64)]
    public string? Reference { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum PaymentMethod
{
    Cash = 0,
    Card = 1,
    MobileWallet = 2,
    BankTransfer = 3,
    StoreCredit = 4,
    Coupon = 5,
    Other = 99
}
