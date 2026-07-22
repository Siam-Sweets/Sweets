using System.ComponentModel.DataAnnotations;

namespace PosApp.Core.Entities;

/// <summary>
/// A reusable tax rate (e.g. VAT 15%, Service Charge 5%).
/// A product carries its own TaxRate snapshot, but taxes defined here
/// can be applied globally or per product at config time.
/// </summary>
public class Tax : StoreScopedEntity
{
    public int Id { get; set; }

    [MaxLength(60)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Percentage, e.g. 15 for 15%.</summary>
    public decimal Rate { get; set; }

    public bool IsIncluded { get; set; } = false;

    public bool IsDefault { get; set; } = false;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
