using PosApp.Core.Entities;
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
    Task<IReadOnlyList<Product>> SearchProductsAsync(string? query, int? categoryId = null);
    Task<Product?> GetProductBySkuAsync(string sku);
    Task<Product> CreateOrUpdateProductAsync(Product product);
    Task AdjustStockAsync(int productId, decimal delta, StockTransactionType type, string? note = null, int? userId = null, decimal? unitCost = null);
    Task<IReadOnlyList<StockTransaction>> GetStockHistoryAsync(int productId);
    Task<IReadOnlyList<Product>> GetLowStockProductsAsync();
    Task<IReadOnlyList<Category>> ListCategoriesAsync();
    Task<Category> CreateOrUpdateCategoryAsync(Category category);
    Task DeleteCategoryAsync(int id);
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
    Task AddStoreCreditAsync(int customerId, decimal amount, string? note = null);
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

public interface IHardwareService
{
    Task<bool> PrintReceiptAsync(Sale sale);
    Task<bool> OpenCashDrawerAsync();
    Task<bool> IsScaleConnected();
    Task<decimal?> ReadScaleAsync();
    Task<bool> IsScannerConnected();
    Task StartScannerAsync(Action<string> onScan);
    Task StopScannerAsync();
}
