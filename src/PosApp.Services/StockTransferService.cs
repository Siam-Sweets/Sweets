using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Data;

namespace PosApp.Services;

/// <summary>
/// Creates auditable inter-store stock transfers. Dispatch, receipt and
/// cancellation use one local transaction and append inventory ledger rows.
/// </summary>
public sealed class StockTransferService : IStockTransferService
{
    private readonly AppDbContext _db;
    private readonly IStoreContext _storeContext;

    public StockTransferService(AppDbContext db, IStoreContext storeContext)
    {
        _db = db;
        _storeContext = storeContext;
    }

    public async Task<IReadOnlyList<StockTransfer>> GetTransfersAsync(
        int? relatedStoreId = null,
        StockTransferStatus? status = null)
    {
        var storeId = relatedStoreId ?? _storeContext.StoreId;
        var query = _db.Set<StockTransfer>().IgnoreQueryFilters().AsNoTracking().Include(x => x.Items).AsQueryable();
        if (storeId > 0) query = query.Where(x => x.StoreId == storeId || x.DestinationStoreId == storeId);
        if (status.HasValue) query = query.Where(x => x.Status == status.Value);
        return await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).ToListAsync();
    }

    public async Task<StockTransfer?> GetTransferAsync(int id)
        => await _db.Set<StockTransfer>().IgnoreQueryFilters().AsNoTracking()
            .Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);

    public async Task<StockTransfer> CreateDraftAsync(StockTransferDraft draft, int userId)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var sourceStoreId = _storeContext.StoreId;
        if (draft.DestinationStoreId <= 0 || draft.DestinationStoreId == sourceStoreId)
            throw new InvalidOperationException("Select a different destination store.");
        var destination = await _db.Stores.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == draft.DestinationStoreId && x.IsActive)
            ?? throw new InvalidOperationException("The destination store is unavailable.");
        var user = await RequireManagerAsync(userId, sourceStoreId);
        var selected = draft.Items.Where(x => x.ProductId > 0 && x.Quantity > 0m).ToList();
        if (selected.Count == 0) throw new InvalidOperationException("Select at least one product and quantity.");
        if (selected.GroupBy(x => x.ProductId).Any(x => x.Count() > 1))
            throw new InvalidOperationException("A product can appear only once in a transfer.");
        var note = NormalizeOptional(draft.Note, 500, "Transfer note");

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var transfer = new StockTransfer
            {
                StoreId = sourceStoreId,
                DestinationStoreId = destination.Id,
                TransferNumber = await GenerateNumberAsync(sourceStoreId),
                Status = StockTransferStatus.Draft,
                Note = note,
                CreatedByUserId = user.Id,
                CreatedAt = DateTime.UtcNow
            };
            _db.Set<StockTransfer>().Add(transfer);
            await _db.SaveChangesAsync();

            foreach (var line in selected)
            {
                var source = await _db.Products.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == line.ProductId && x.StoreId == sourceStoreId && x.IsActive)
                    ?? throw new InvalidOperationException("One selected product is unavailable in the source store.");
                if (!source.StockQuantity.HasValue)
                    throw new InvalidOperationException($"{source.Name} does not track inventory.");
                var destinationProduct = await EnsureDestinationProductAsync(source, destination.Id);
                _db.Set<StockTransferItem>().Add(new StockTransferItem
                {
                    StoreId = sourceStoreId,
                    StockTransferId = transfer.Id,
                    ProductId = source.Id,
                    DestinationProductId = destinationProduct.Id,
                    ProductName = source.Name,
                    Sku = source.Sku,
                    Unit = source.EffectiveUnit,
                    Quantity = line.Quantity,
                    UnitCost = source.CostPrice
                });
            }
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
            return (await GetTransferAsync(transfer.Id))!;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task DispatchAsync(int transferId, int userId)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var transfer = await LoadTrackedAsync(transferId);
            if (transfer.Status != StockTransferStatus.Draft)
                throw new InvalidOperationException("Only a draft transfer can be dispatched.");
            var user = await RequireManagerAsync(userId, transfer.StoreId);

            foreach (var item in transfer.Items)
            {
                var product = await _db.Products.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == item.ProductId && x.StoreId == transfer.StoreId)
                    ?? throw new InvalidOperationException($"Source product {item.ProductName} no longer exists.");
                if (!product.StockQuantity.HasValue || product.StockQuantity.Value < item.Quantity)
                    throw new InvalidOperationException($"Insufficient stock for {item.ProductName}.");
                product.StockQuantity -= item.Quantity;
                product.UpdatedAt = DateTime.UtcNow;
                _db.StockTransactions.Add(new StockTransaction
                {
                    StoreId = transfer.StoreId,
                    ProductId = product.Id,
                    Type = StockTransactionType.Transfer,
                    Quantity = -item.Quantity,
                    BalanceAfter = product.StockQuantity.Value,
                    UnitCost = item.UnitCost,
                    StockTransferId = transfer.Id,
                    StockTransferItemId = item.Id,
                    UserId = user.Id,
                    Note = $"Transfer out {transfer.TransferNumber}"
                });
            }
            transfer.Status = StockTransferStatus.Dispatched;
            transfer.DispatchedByUserId = user.Id;
            transfer.DispatchedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task ReceiveAsync(int transferId, int userId)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var transfer = await LoadTrackedAsync(transferId);
            if (transfer.Status != StockTransferStatus.Dispatched)
                throw new InvalidOperationException("Only a dispatched transfer can be received.");
            var user = await RequireManagerAsync(userId, transfer.DestinationStoreId);

            foreach (var item in transfer.Items)
            {
                var product = await _db.Products.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == item.DestinationProductId && x.StoreId == transfer.DestinationStoreId)
                    ?? throw new InvalidOperationException($"Destination product {item.ProductName} no longer exists.");
                product.StockQuantity = (product.StockQuantity ?? 0m) + item.Quantity;
                product.UpdatedAt = DateTime.UtcNow;
                _db.StockTransactions.Add(new StockTransaction
                {
                    StoreId = transfer.DestinationStoreId,
                    ProductId = product.Id,
                    Type = StockTransactionType.Transfer,
                    Quantity = item.Quantity,
                    BalanceAfter = product.StockQuantity.Value,
                    UnitCost = item.UnitCost,
                    StockTransferId = transfer.Id,
                    StockTransferItemId = item.Id,
                    UserId = user.Id,
                    Note = $"Transfer in {transfer.TransferNumber}"
                });
            }
            transfer.Status = StockTransferStatus.Received;
            transfer.ReceivedByUserId = user.Id;
            transfer.ReceivedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task CancelAsync(int transferId, int userId, string? reason = null)
    {
        reason = NormalizeOptional(reason, 500, "Cancellation reason");
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var transfer = await LoadTrackedAsync(transferId);
            if (transfer.Status is StockTransferStatus.Received or StockTransferStatus.Cancelled)
                throw new InvalidOperationException("This transfer can no longer be cancelled.");
            var user = await RequireManagerAsync(userId, transfer.StoreId);

            if (transfer.Status == StockTransferStatus.Dispatched)
            {
                foreach (var item in transfer.Items)
                {
                    var product = await _db.Products.IgnoreQueryFilters()
                        .FirstOrDefaultAsync(x => x.Id == item.ProductId && x.StoreId == transfer.StoreId)
                        ?? throw new InvalidOperationException($"Source product {item.ProductName} no longer exists.");
                    product.StockQuantity = (product.StockQuantity ?? 0m) + item.Quantity;
                    product.UpdatedAt = DateTime.UtcNow;
                    _db.StockTransactions.Add(new StockTransaction
                    {
                        StoreId = transfer.StoreId,
                        ProductId = product.Id,
                        Type = StockTransactionType.Transfer,
                        Quantity = item.Quantity,
                        BalanceAfter = product.StockQuantity.Value,
                        UnitCost = item.UnitCost,
                        StockTransferId = transfer.Id,
                        StockTransferItemId = item.Id,
                        UserId = user.Id,
                        Note = $"Cancelled transfer {transfer.TransferNumber}"
                    });
                }
            }
            transfer.Status = StockTransferStatus.Cancelled;
            transfer.CancelledByUserId = user.Id;
            transfer.CancelledAt = DateTime.UtcNow;
            transfer.CancellationReason = reason;
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<IReadOnlyList<StoreInventoryRow>> GetInventoryAcrossStoresAsync(
        int? storeId = null,
        string? query = null)
    {
        var products = _db.Products.IgnoreQueryFilters().AsNoTracking().Where(x => x.IsActive);
        if (storeId.HasValue) products = products.Where(x => x.StoreId == storeId.Value);
        var text = query?.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            var lowered = text.ToLower();
            products = products.Where(x => x.Name.ToLower().Contains(lowered) ||
                                           (x.Sku != null && x.Sku.ToLower().Contains(lowered)) ||
                                           (x.Barcode != null && x.Barcode.ToLower().Contains(lowered)));
        }
        var rows = await products.Select(x => new
        {
            x.StoreId, x.Id, x.Name, x.Sku, x.Unit, x.StockQuantity, x.LowStockThreshold
        }).ToListAsync();
        var stores = await _db.Stores.AsNoTracking().ToDictionaryAsync(x => x.Id);
        return rows.Select(x => new StoreInventoryRow
        {
            StoreId = x.StoreId,
            StoreCode = stores.GetValueOrDefault(x.StoreId)?.Code ?? $"#{x.StoreId}",
            StoreName = stores.GetValueOrDefault(x.StoreId)?.Name ?? $"Store {x.StoreId}",
            ProductId = x.Id,
            ProductName = x.Name,
            Sku = x.Sku,
            Unit = x.Unit,
            StockQuantity = x.StockQuantity,
            LowStockThreshold = x.LowStockThreshold
        }).OrderBy(x => x.StoreName).ThenBy(x => x.ProductName).ToList();
    }

    private async Task<StockTransfer> LoadTrackedAsync(int transferId)
        => await _db.Set<StockTransfer>().IgnoreQueryFilters()
               .Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == transferId)
           ?? throw new InvalidOperationException("Stock transfer not found.");

    private async Task<User> RequireManagerAsync(int userId, int storeId)
    {
        var user = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == userId && x.StoreId == storeId && x.IsActive)
            ?? throw new InvalidOperationException("An active user from the relevant store is required.");
        if (user.Role < UserRole.Manager)
            throw new InvalidOperationException("Manager permission is required for stock transfers.");
        return user;
    }

    private async Task<Product> EnsureDestinationProductAsync(Product source, int destinationStoreId)
    {
        var products = _db.Products.IgnoreQueryFilters();
        Product? destination = null;
        if (!string.IsNullOrWhiteSpace(source.Sku))
            destination = await products.FirstOrDefaultAsync(x => x.StoreId == destinationStoreId && x.Sku == source.Sku);
        if (destination == null && !string.IsNullOrWhiteSpace(source.Barcode))
            destination = await products.FirstOrDefaultAsync(x => x.StoreId == destinationStoreId && x.Barcode == source.Barcode);
        if (destination != null) return destination;

        var sourceCategory = await _db.Categories.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(x => x.Id == source.CategoryId && x.StoreId == source.StoreId);
        var destinationCategory = await _db.Categories.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.StoreId == destinationStoreId && x.Name == sourceCategory.Name);
        if (destinationCategory == null)
        {
            destinationCategory = new Category
            {
                StoreId = destinationStoreId,
                Name = sourceCategory.Name,
                Description = sourceCategory.Description,
                Color = sourceCategory.Color,
                SortOrder = sourceCategory.SortOrder,
                IsActive = true
            };
            _db.Categories.Add(destinationCategory);
            await _db.SaveChangesAsync();
        }

        destination = new Product
        {
            StoreId = destinationStoreId,
            Name = source.Name,
            Description = source.Description,
            Sku = source.Sku,
            Barcode = source.Barcode,
            CategoryId = destinationCategory.Id,
            Price = source.Price,
            CostPrice = source.CostPrice,
            TaxRate = source.TaxRate,
            Unit = source.EffectiveUnit,
            StockQuantity = 0m,
            LowStockThreshold = source.LowStockThreshold,
            ImagePath = null,
            IsWeighted = source.IsWeighted,
            IsActive = true,
            AllowDiscount = source.AllowDiscount
        };
        _db.Products.Add(destination);
        await _db.SaveChangesAsync();
        return destination;
    }

    private async Task<string> GenerateNumberAsync(int sourceStoreId)
    {
        var store = await _db.Stores.AsNoTracking().FirstAsync(x => x.Id == sourceStoreId);
        var compactCode = new string(store.Code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(compactCode)) compactCode = "STORE";
        if (compactCode.Length > 10) compactCode = compactCode[..10];
        // Timestamp plus a random suffix prevents two offline devices from
        // generating the same number before either one has synchronized.
        return $"TR-{compactCode}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..35].ToUpperInvariant();
    }

    private static string? NormalizeOptional(string? value, int maximum, string field)
    {
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (text?.Length > maximum) throw new InvalidOperationException($"{field} cannot exceed {maximum} characters.");
        return text;
    }
}
