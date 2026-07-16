using System.ComponentModel.DataAnnotations;

namespace PosApp.Core.Entities;

/// <summary>A local till session bounded by an opening and end-of-day close.</summary>
public class CashSession
{
    public int Id { get; set; }
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public int OpenedByUserId { get; set; }
    public decimal OpeningFloat { get; set; }
    public DateTime? ClosedAt { get; set; }
    public int? ClosedByUserId { get; set; }
    public decimal? ExpectedCash { get; set; }
    public decimal? CountedCash { get; set; }
    public decimal? Variance { get; set; }

    [MaxLength(500)]
    public string? Note { get; set; }

    public bool IsOpen => ClosedAt == null;
    public ICollection<CashMovement> Movements { get; set; } = new List<CashMovement>();
}

public class CashMovement
{
    public int Id { get; set; }
    public int CashSessionId { get; set; }
    public CashSession? CashSession { get; set; }
    public CashMovementType Type { get; set; }
    public decimal Amount { get; set; }

    [MaxLength(300)]
    public string Description { get; set; } = string.Empty;

    public int UserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum CashMovementType
{
    CashIn = 0,
    CashOut = 1
}
