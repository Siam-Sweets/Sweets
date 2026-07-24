using System.ComponentModel.DataAnnotations;

namespace PosApp.Core.Entities;

/// <summary>A store-to-store inventory transfer owned by its source store.</summary>
public sealed class StockTransfer : StoreScopedEntity
{
    public int Id { get; set; }

    [MaxLength(64)]
    public string OperationId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(40)]
    public string TransferNumber { get; set; } = string.Empty;

    public int DestinationStoreId { get; set; }
    public StockTransferStatus Status { get; set; } = StockTransferStatus.Draft;

    [MaxLength(500)]
    public string? Note { get; set; }

    public int? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int? DispatchedByUserId { get; set; }
    public DateTime? DispatchedAt { get; set; }
    public int? ReceivedByUserId { get; set; }
    public DateTime? ReceivedAt { get; set; }
    public int? CancelledByUserId { get; set; }
    public DateTime? CancelledAt { get; set; }

    [MaxLength(500)]
    public string? CancellationReason { get; set; }

    public ICollection<StockTransferItem> Items { get; set; } = new List<StockTransferItem>();
}

/// <summary>Immutable product and quantity facts recorded on a stock transfer.</summary>
public sealed class StockTransferItem : StoreScopedEntity
{
    public int Id { get; set; }
    public int StockTransferId { get; set; }
    public StockTransfer? StockTransfer { get; set; }
    public int ProductId { get; set; }
    public int DestinationProductId { get; set; }

    [MaxLength(100)]
    public string ProductName { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? Sku { get; set; }

    public UnitOfMeasure Unit { get; set; } = UnitOfMeasure.Piece;
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
}

public enum StockTransferStatus
{
    Draft = 0,
    Dispatched = 1,
    Received = 2,
    Cancelled = 3
}
