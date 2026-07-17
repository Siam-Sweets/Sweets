using PosApp.Core.Entities;
using PosApp.Core.Enums;
using PosApp.Core.Models;

namespace PosApp.Core.Interfaces;

public interface IAuthService
{
    Task<User?> LoginAsync(string username, string password);
    Task<bool> ChangePasswordAsync(int userId, string newPassword);
    string HashPassword(string password, out string salt);
    bool VerifyPassword(string password, string hash, string salt);
}

public interface IInventoryService
{
    Task<IReadOnlyList<Product>> SearchProductsAsync(
        string? query,
        int? categoryId = null,
        ProductSearchField searchField = ProductSearchField.All);
    Task<Product?> GetProductBySkuAsync(string sku);
    Task<Product> CreateOrUpdateProductAsync(Product product);
    Task SetProductWeightedAsync(int productId, bool isWeighted);
    Task AdjustStockAsync(int productId, decimal delta, StockTransactionType type, string? note = null, int? userId = null, decimal? unitCost = null);
    Task<IReadOnlyList<StockTransaction>> GetStockHistoryAsync(int productId);
    Task<IReadOnlyList<Product>> GetLowStockProductsAsync();
    Task<IReadOnlyList<Category>> ListCategoriesAsync();
    Task<Category> CreateOrUpdateCategoryAsync(Category category);
    Task DeleteCategoryAsync(int id);
    Task ApplyInventoryCountAsync(IReadOnlyList<InventoryCountEntry> entries, string? note = null, int? userId = null);
}

public interface IPurchaseService
{
    Task<IReadOnlyList<Supplier>> SearchSuppliersAsync(string? query = null);
    Task<Supplier> CreateOrUpdateSupplierAsync(Supplier supplier);
    Task DeactivateSupplierAsync(int supplierId);
    Task<IReadOnlyList<PurchaseDocument>> GetPurchasesAsync(DateTime from, DateTime to);
    Task<PurchaseDocument> PostPurchaseAsync(PurchaseDraft draft);
}

public interface IRegisterService
{
    Task<CashSession?> GetOpenSessionAsync();
    Task<IReadOnlyList<CashSession>> GetRecentSessionsAsync(int count = 30);
    Task<IReadOnlyList<CashMovement>> GetMovementsAsync(int sessionId);
    Task<CashSession> OpenSessionAsync(decimal openingFloat, int userId, string? note = null);
    Task<CashMovement> AddMovementAsync(CashMovementType type, decimal amount, string description, int userId);
    Task<RegisterSummary> GetSummaryAsync(int sessionId, DateTime? through = null);
    Task<RegisterSummary> CloseSessionAsync(int sessionId, decimal countedCash, int userId, string? note = null);
}

public interface ICatalogTransferService
{
    Task ExportProductsAsync(string filePath);
    Task<CatalogImportResult> ImportProductsAsync(string filePath, ProductImportMode mode, int? userId = null);
}

public interface IBackupService
{
    string BackupFolder { get; }
    Task<string> CreateBackupAsync(string? destinationPath = null, int? retentionCount = null);
    Task ValidateBackupAsync(string backupPath);
    Task StageRestoreAsync(string backupPath);
}

public interface IUpdateService
{
    string CurrentVersion { get; }
    string DataFolder { get; }
    string UpdateBackupFolder { get; }
    Task<SafeUpdateRecord?> EnsurePreMigrationBackupAsync();
    Task<SafeUpdatePackageInfo> InspectInstallerAsync(string installerPath);
    Task<SafeUpdateLaunchResult> PrepareAndLaunchAsync(string installerPath);
    Task<SafeUpdateRecord?> GetPendingUpdateAsync();
    Task<SafeUpdateRecord?> GetLastUpdateAsync();
    Task<SafeUpdateRecord?> MarkStartupSuccessfulAsync();
}

public interface ISaleService
{
    Task<Sale> CheckoutAsync(SaleDraft draft);
    Task<Sale?> GetSaleByIdAsync(int id);
    Task<IReadOnlyList<Sale>> GetSalesAsync(DateTime from, DateTime to, SaleStatus? status = null);
    Task<IReadOnlyList<Sale>> GetSuspendedSalesAsync();
    Task<Sale> SuspendAsync(SaleDraft draft);
    Task<Sale> RecallSuspendedAsync(int saleId);
    Task<Sale> VoidSaleAsync(int saleId, int userId);
    Task<Sale> RefundSaleAsync(int saleId, int userId, string? reason = null);
    Task<string> GenerateReceiptNumberAsync();
}

public interface ICustomerService
{
    Task<IReadOnlyList<Customer>> SearchCustomersAsync(string? query);
    Task<Customer?> GetCustomerAsync(int id);
    Task<Customer> CreateOrUpdateCustomerAsync(Customer customer);
    Task DeleteCustomerAsync(int id);
    Task<IReadOnlyList<Sale>> GetCustomerHistoryAsync(int customerId);
}

public interface IReportService
{
    Task<DailySalesReport> GetDailyReportAsync(DateTime date);
    Task<DateRangeReport> GetRangeReportAsync(DateTime from, DateTime to);
    Task<IReadOnlyList<TopProductRow>> GetTopProductsAsync(DateTime from, DateTime to, int top = 10);
    Task<IReadOnlyList<SalesByHourRow>> GetSalesByHourAsync(DateTime date);
    Task<IReadOnlyList<SalesByCategoryRow>> GetSalesByCategoryAsync(DateTime from, DateTime to);
    Task<IReadOnlyList<PaymentBreakdownRow>> GetPaymentBreakdownAsync(DateTime from, DateTime to);
}

public interface ISettingsService
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string? value);
    Task<StoreSettings> GetStoreSettingsAsync();
    Task SetStoreSettingsAsync(StoreSettings settings);
}

public interface IDiscountService
{
    Task<IReadOnlyList<Discount>> GetAllAsync();
    Task<IReadOnlyList<Discount>> GetActiveAsync(DateTime? at = null);
    Task<Discount> SaveAsync(Discount discount);
    Task DeactivateAsync(int id);
}

public interface ISetupService
{
    Task<bool> IsSetupCompleteAsync();
    Task<InitialSetupRequest> GetSetupDefaultsAsync();
    Task CompleteSetupAsync(InitialSetupRequest request);
}

public interface IHardwareService
{
    Task<bool> PrintReceiptAsync(Sale sale);
    Task<bool> PrintTextAsync(string text);
    Task<bool> OpenCashDrawerAsync();
    Task<bool> IsScaleConnected();
    Task<decimal?> ReadScaleAsync();
    Task<bool> IsScannerConnected();
    Task StartScannerAsync(Action<string> onScan);
    Task StopScannerAsync();
}
