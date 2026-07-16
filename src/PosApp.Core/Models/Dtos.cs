using System.ComponentModel;
using System.Runtime.CompilerServices;
using PosApp.Core.Entities;

namespace PosApp.Core.Models;

/// <summary>
/// In-memory draft of a sale being built at the POS before checkout.
/// Not persisted until Suspend or Checkout is called.
/// </summary>
public class SaleDraft
{
    public int? CustomerId { get; set; }
    public int UserId { get; set; }
    public List<SaleDraftLine> Lines { get; set; } = new();
    public List<Discount> CartDiscounts { get; set; } = new();
    public List<SalePayment> Payments { get; set; } = new();
    public decimal AmountTendered { get; set; }
    public string? Note { get; set; }
    public int? SuspendedSaleId { get; set; }

    public decimal Subtotal => Lines.Sum(l => l.UnitPrice * l.Quantity);
    public decimal DiscountTotal =>
        Lines.Sum(l => l.DiscountAmount) +
        CartDiscounts.Sum(d => d.Type == DiscountType.Percentage
            ? (Subtotal - Lines.Sum(l => l.DiscountAmount)) * d.Value / 100m
            : d.Value);
    public decimal TaxTotal => Lines.Sum(l =>
        ((l.UnitPrice * l.Quantity) - l.DiscountAmount) * l.TaxRate / 100m);
    public decimal Total => Subtotal - DiscountTotal + TaxTotal;
}

public class SaleDraftLine : INotifyPropertyChanged
{
    private decimal _quantity;

    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public decimal Quantity
    {
        get => _quantity;
        set
        {
            if (_quantity == value) return;
            _quantity = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LineTotal));
        }
    }
    public decimal UnitPrice { get; set; }
    public decimal TaxRate { get; set; }
    public decimal DiscountAmount { get; set; }
    public string? DiscountReason { get; set; }
    public bool IsWeighted { get; set; }
    public decimal LineTotal => (UnitPrice * Quantity) - DiscountAmount;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class StoreSettings
{
    public string StoreName { get; set; } = "My Store";
    public string Address { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string TaxId { get; set; } = "";
    public string CurrencySymbol { get; set; } = "৳";
    public string CurrencyCode { get; set; } = "BDT";
    public int CurrencyDecimals { get; set; } = 2;
    public string FooterNote { get; set; } = "Thank you for your business!";
    public bool PrintReceiptAutomatically { get; set; } = true;
    public bool OpenDrawerOnCashSale { get; set; } = true;
    public string ReceiptPrinterName { get; set; } = "";
    public string CashDrawerPort { get; set; } = "COM1";
    public string ScalePort { get; set; } = "COM3";
    // Retained for backward-compatible settings deserialization. Loyalty is
    // intentionally disabled until the feature is reintroduced end-to-end.
    public bool EnableLoyalty { get; set; } = false;
    public decimal DefaultTaxRate { get; set; } = 0m;
    public int ReceiptWidth { get; set; } = 80; // mm
    public string Language { get; set; } = "en";
    public string Theme { get; set; } = "Light";
    public bool AutomaticBackupEnabled { get; set; } = true;
    public bool BackupOnStartup { get; set; } = true;
    public bool BackupOnExit { get; set; } = true;
    public int BackupRetentionCount { get; set; } = 20;
}

public class PurchaseDraft
{
    public int? SupplierId { get; set; }
    public int UserId { get; set; }
    public string? ExternalReference { get; set; }
    public DateTime DocumentDate { get; set; } = DateTime.UtcNow;
    public DateTime StockDate { get; set; } = DateTime.UtcNow;
    public string? Note { get; set; }
    public List<PurchaseDraftLine> Lines { get; set; } = new();
    public decimal Subtotal => Lines.Sum(line => line.LineSubtotal);
    public decimal TaxTotal => Lines.Sum(line => line.LineTax);
    public decimal Total => Subtotal + TaxTotal;
}

public class PurchaseDraftLine
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal TaxRate { get; set; }
    public decimal LineSubtotal => Quantity * UnitCost;
    public decimal LineTax => LineSubtotal * TaxRate / 100m;
    public decimal LineTotal => LineSubtotal + LineTax;
}

public class InventoryCountEntry
{
    public int ProductId { get; set; }
    public decimal CountedQuantity { get; set; }
}

public class RegisterSummary
{
    public int SessionId { get; set; }
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public decimal OpeningFloat { get; set; }
    public decimal CashSales { get; set; }
    public decimal CashIn { get; set; }
    public decimal CashOut { get; set; }
    public decimal GrossSales { get; set; }
    public int TransactionCount { get; set; }
    public decimal ExpectedCash { get; set; }
    public decimal? CountedCash { get; set; }
    public decimal? Variance { get; set; }
    public Dictionary<PaymentMethod, decimal> ByPaymentMethod { get; set; } = new();
}

public enum ProductImportMode
{
    CatalogOnly = 0,
    InventoryCount = 1,
    Purchase = 2
}

public class CatalogImportResult
{
    public int Created { get; set; }
    public int Updated { get; set; }
    public int StockAdjusted { get; set; }
    public List<string> Warnings { get; set; } = new();
}

public class DailySalesReport
{
    public DateTime Date { get; set; }
    public int TransactionCount { get; set; }
    public int ItemCount { get; set; }
    public decimal GrossSales { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal NetSales { get; set; }
    public decimal CostOfGoods { get; set; }
    public decimal GrossProfit => NetSales - CostOfGoods;
    public Dictionary<PaymentMethod, decimal> ByPaymentMethod { get; set; } = new();
    public int RefundCount { get; set; }
    public decimal RefundTotal { get; set; }
}

public class DateRangeReport
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public int TransactionCount { get; set; }
    public int ItemCount { get; set; }
    public decimal GrossSales { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal NetSales { get; set; }
    public decimal CostOfGoods { get; set; }
    public decimal GrossProfit => NetSales - CostOfGoods;
    public List<DailySalesReport> Daily { get; set; } = new();
}

public class TopProductRow
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public int QuantitySold { get; set; }
    public decimal Revenue { get; set; }
    public decimal Profit { get; set; }
}

public class SalesByHourRow
{
    public int Hour { get; set; }
    public int TransactionCount { get; set; }
    public decimal Revenue { get; set; }
}

public class SalesByCategoryRow
{
    public string CategoryName { get; set; } = string.Empty;
    public int QuantitySold { get; set; }
    public decimal Revenue { get; set; }
}

public class PaymentBreakdownRow
{
    public PaymentMethod Method { get; set; }
    public int Count { get; set; }
    public decimal Total { get; set; }
}
