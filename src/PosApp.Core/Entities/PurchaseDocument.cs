using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PosApp.Core.Utilities;

namespace PosApp.Core.Entities;

/// <summary>A posted supplier purchase that increases local stock.</summary>
public class PurchaseDocument
{
    public int Id { get; set; }

    [MaxLength(32)]
    public string DocumentNumber { get; set; } = string.Empty;

    [MaxLength(80)]
    public string? ExternalReference { get; set; }

    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    public int UserId { get; set; }
    public DateTime DocumentDate { get; set; } = DateTime.UtcNow;
    [NotMapped]
    public DateTime DocumentDateLocal => DateTimeUtilities.ToLocal(DocumentDate);
    public DateTime StockDate { get; set; } = DateTime.UtcNow;
    public PurchaseStatus Status { get; set; } = PurchaseStatus.Posted;

    public decimal Subtotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }

    [MaxLength(500)]
    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<PurchaseItem> Items { get; set; } = new List<PurchaseItem>();
}

public class PurchaseItem
{
    public int Id { get; set; }
    public int PurchaseDocumentId { get; set; }
    public PurchaseDocument? PurchaseDocument { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }

    [MaxLength(100)]
    public string ProductName { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? Sku { get; set; }

    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal TaxRate { get; set; }

    public decimal LineSubtotal => Quantity * UnitCost;
    public decimal LineTax => LineSubtotal * TaxRate / 100m;
    public decimal LineTotal => LineSubtotal + LineTax;
}

public enum PurchaseStatus
{
    Posted = 0,
    Voided = 1
}
