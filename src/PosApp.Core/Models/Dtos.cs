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
    public List<SalePayment> Payments { get; set; } = new();
    public decimal AmountTendered { get; set; }
    public string? Note { get; set; }
    public string ServiceType { get; set; } = "Retail";
    public int? SuspendedSaleId { get; set; }

    public decimal Subtotal => Lines.Sum(l => l.UnitPrice * l.Quantity);
    public decimal DiscountTotal => Lines.Sum(l => l.DiscountAmount);
    public decimal TaxTotal => Lines.Sum(l =>
        ((l.UnitPrice * l.Quantity) - l.DiscountAmount) * l.TaxRate / 100m);
    public decimal Total => Subtotal - DiscountTotal + TaxTotal;
}

public class SaleDraftLine : INotifyPropertyChanged
{
    private decimal _quantity;
    private decimal _discountAmount;

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
    public decimal DiscountAmount
    {
        get => _discountAmount;
        set
        {
            if (_discountAmount == value) return;
            _discountAmount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LineTotal));
        }
    }
    public string? DiscountReason { get; set; }
    public int? PromotionId { get; set; }
    public decimal CostPrice { get; set; }
    public bool AllowDiscount { get; set; } = true;
    public bool IsWeighted { get; set; }
    public decimal LineTotal => (UnitPrice * Quantity) - DiscountAmount;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// A validated partial or full refund against one completed sale. Quantities
/// always refer to the original sale-item rows so repeated partial refunds can
/// never return more stock than was sold.
/// </summary>
public sealed class RefundDraft
{
    public int SaleId { get; set; }
    public int UserId { get; set; }
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Cash;
    public string? Reason { get; set; }
    public List<RefundDraftLine> Lines { get; set; } = new();
}

public sealed class RefundDraftLine
{
    public int SaleItemId { get; set; }
    public decimal Quantity { get; set; }
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
    public string Country { get; set; } = "Bangladesh";
    public int CurrencyDecimals { get; set; } = 2;
    public string FooterNote { get; set; } = "Thank you for your business!";
    // Retained for backward-compatible settings deserialization. Checkout no
    // longer prints automatically; receipts are printed deliberately from
    // Sales History so PDF printers cannot interrupt a completed transaction.
    public bool PrintReceiptAutomatically { get; set; } = false;
    public string ReceiptPrinterName { get; set; } = "";
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
    public string DefaultServiceType { get; set; } = "Retail";
    public bool RequireOpenRegisterForSales { get; set; } = false;
    public bool ConfirmBeforeVoidingOrder { get; set; } = true;
    public bool EnableVirtualKeyboard { get; set; } = false;
    public int ProductGridRows { get; set; } = 5;
    public int ProductGridColumns { get; set; } = 5;
    public int UiScalePercent { get; set; } = 100;
    public int MessageDurationSeconds { get; set; } = 5;
    public bool ShowCashInOnStartup { get; set; } = false;
    public bool SelectBusinessDayOnStartup { get; set; } = false;
}

/// <summary>Validated metadata for a locally selected PosApp setup package.</summary>
public sealed class SafeUpdatePackageInfo
{
    public string InstallerPath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public string CurrentVersion { get; init; } = string.Empty;
    public string TargetVersion { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public bool IsValid { get; init; }
    public string ValidationMessage { get; init; } = string.Empty;
}

/// <summary>
/// Durable recovery information written before an updater is launched. The
/// pre-update backup is intentionally retained after a successful upgrade.
/// </summary>
public sealed class SafeUpdateRecord
{
    public string State { get; set; } = "Prepared";
    public string FromVersion { get; set; } = string.Empty;
    public string TargetVersion { get; set; } = string.Empty;
    public string? RunningVersion { get; set; }
    public string InstallerPath { get; set; } = string.Empty;
    public string InstallerSha256 { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public string DatabasePath { get; set; } = string.Empty;
    public string? InstallerLogPath { get; set; }
    public int? InstallerProcessId { get; set; }
    public DateTime PreparedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? FailureMessage { get; set; }
}

public sealed class SafeUpdateLaunchResult
{
    public SafeUpdatePackageInfo Package { get; init; } = new();
    public SafeUpdateRecord Record { get; init; } = new();
}

/// <summary>Information collected by the local first-run setup wizard.</summary>
public class InitialSetupRequest
{
    public StoreSettings StoreSettings { get; set; } = new();
    public string AdminFullName { get; set; } = "Administrator";
    public string AdminUsername { get; set; } = "admin";
    public string AdminPin { get; set; } = string.Empty;
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
    public decimal ItemCount { get; set; }
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
    public decimal ItemCount { get; set; }
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
    public decimal QuantitySold { get; set; }
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
    public decimal QuantitySold { get; set; }
    public decimal Revenue { get; set; }
}

public class PaymentBreakdownRow
{
    public PaymentMethod Method { get; set; }
    public int Count { get; set; }
    public decimal Total { get; set; }
}
