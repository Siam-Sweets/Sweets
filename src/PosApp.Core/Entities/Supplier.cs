using System.ComponentModel.DataAnnotations;

namespace PosApp.Core.Entities;

/// <summary>A local vendor used on purchase documents.</summary>
public class Supplier : StoreScopedEntity
{
    public int Id { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(30)]
    public string? Phone { get; set; }

    [MaxLength(120)]
    public string? Email { get; set; }

    [MaxLength(300)]
    public string? Address { get; set; }

    [MaxLength(40)]
    public string? TaxId { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<PurchaseDocument> Purchases { get; set; } = new List<PurchaseDocument>();
}
