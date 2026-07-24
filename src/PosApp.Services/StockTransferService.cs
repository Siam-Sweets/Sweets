using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Data;

namespace PosApp.Services;

/// <summary>
/// Creates auditable inter-store stock transfers. Every state transition is
/// idempotent, transactionally updates stock, and appends immutable ledger rows.
/// </summary>
public sealed class StockTransferService : IStockTransferService
{
    private readonly AppDbContext _db;
    private readonly IStoreContext _storeContext;
    private readonly IUserSessionContext _session;

    public StockTransferService(
        AppDbContext db,
        IStoreContext storeContext,
        IUserSessionContext session)
    {
        _db = db;
        _storeContext = storeContext;
        _session = session;
    }

    public async Task<IReadOnlyList<StockTransfer>> GetTransfersAsync(
        int? relatedStoreId = null,
        StockTransferStatus? status = null)
    {
        var storeId = ResolveReadableStore(relatedStoreId);
        var query = _db.StockTransfers.IgnoreQueryFilters().AsNoTracking()
            .Include(x => x.Items).AsQueryable();
        if (storeId.HasValue)
            query = query.Where(x => x.StoreId == storeId.Value || x.DestinationStoreId == storeId.Value);
        if (status.HasValue) query = query.Where(x => x.Status == status.Value);
        return await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).ToListAsync();
    }

    public async Task<StockTransfer?> GetTransferAsync(int id)
    {
        var transfer = await _db.StockTransfers.IgnoreQueryFilters().AsNoTracking()
            .Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (transfer == null) return null;
        EnsureReadableTransfer(transfer);
        return transfer;
    }

    public async Task<StockTransfer> CreateDraftAsync(StockTransferDraft draft, int userId)
    {
        ArgumentNullException.ThrowIfNull(draft);
        draft.OperationId = NormalizeOperationId(draft.OperationId);
        var sourceStoreId = _storeContext.StoreId;
        EnsureWritableStore(sourceStoreId);

        var duplicate = await _db.StockTransfers.IgnoreQueryFilters().AsNoTracking()
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.StoreId == sourceStoreId && x.OperationId == draft.OperationId);
        if (duplicate != null) return duplicate;

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

        using var operation = SyncOperationScope.Begin(draft.OperationId);
        _db.ChangeTracker.Clear();
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var transfer = new StockTransfer
            {
                StoreId = sourceStoreId,
                OperationId = draft.OperationId,
                DestinationStoreId = destination.Id,
                TransferNumber = await GenerateNumberAsync(sourceStoreId),
                Status = StockTransferStatus.Draft,
                Note = note,
                CreatedByUserId = user.Id,
                CreatedAt = DateTime.UtcNow
            };
            _db.StockTransfers.Add(transfer);
            await _db.SaveChangesAsync();

            foreach (var line in selected)
            {
                var source = await _db.Products.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == line.ProductId &&
                                              x.StoreId == sourceStoreId && x.IsActive)
                    ?? throw new InvalidOperationException(
                        "One selected product is unavailable in the source store.");
                if (!source.StockQuantity.HasValue)
                    throw new InvalidOperationException($"{source.Name} does not track inventory.");
                var destinationProduct = await EnsureDestinationProductAsync(source, destination.Id);
                _db.StockTransferItems.Add(new StockTransferItem
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
            await _db.CommitExternalTransactionAsync(transaction);
            return (await GetTransferAsync(transfer.Id))!;
        }
        catch (DbUpdateException)
        {
            await _db.RollbackExternalTransactionAsync(transaction);
            var existing = await _db.StockTransfers.IgnoreQueryFilters().AsNoTracking()
                .Include(x => x.Items)
                .FirstOrDefaultAsync(x => x.StoreId == sourceStoreId && x.OperationId == draft.OperationId);
            if (existing != null) return existing;
            throw;
        }
        catch
        {
            await _db.RollbackExternalTransactionAsync(transaction);
            throw;
        }
    }

    public Task DispatchAsync(int transferId, int userId)
        => ChangeStateAsync(transferId, userId, TransferAction.Dispatch, null);

    public Task ReceiveAsync(int transferId, int userId)
        => ChangeStateAsync(transferId, userId, TransferAction.Receive, null);

    public Task CancelAsync(int transferId, int userId, string? reason = null)
        => ChangeStateAsync(
            transferId, userId, TransferAction.Cancel,
            NormalizeOptional(reason, 500, "Cancellation reason"));

    private async Task ChangeStateAsync(
        int transferId,
        int userId,
        TransferAction action,
        string? cancellationReason)
    {
        _db.ChangeTracker.Clear();
        var initial = await LoadTrackedAsync(transferId);
        EnsureWritableStore(action == TransferAction.Receive
            ? initial.DestinationStoreId
            : initial.StoreId);

        if (action == TransferAction.Dispatch && initial.Status is StockTransferStatus.Dispatched or StockTransferStatus.Received)
            return;
        if (action == TransferAction.Receive && initial.Status == StockTransferStatus.Received)
            return;
        if (action == TransferAction.Cancel && initial.Status == StockTransferStatus.Cancelled)
            return;

        var relevantStore = action == TransferAction.Receive
            ? initial.DestinationStoreId
            : initial.StoreId;
        var user = await RequireManagerAsync(userId, relevantStore);
        var operationId = $"{initial.SyncId}:{action.ToString().ToLowerInvariant()}";
        using var operation = SyncOperationScope.Begin(operationId);
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var transfer = initial;
            switch (action)
            {
                case TransferAction.Dispatch:
                    if (transfer.Status != StockTransferStatus.Draft)
                        throw new InvalidOperationException("Only a draft transfer can be dispatched.");
                    await ApplySourceMovementAsync(transfer, user.Id, -1m, "Transfer out", operationId);
                    transfer.Status = StockTransferStatus.Dispatched;
                    transfer.DispatchedByUserId = user.Id;
                    transfer.DispatchedAt = DateTime.UtcNow;
                    break;

                case TransferAction.Receive:
                    if (transfer.Status != StockTransferStatus.Dispatched)
                        throw new InvalidOperationException("Only a dispatched transfer can be received.");
                    await ApplyDestinationReceiptAsync(transfer, user.Id, operationId);
                    transfer.Status = StockTransferStatus.Received;
                    transfer.ReceivedByUserId = user.Id;
                    transfer.ReceivedAt = DateTime.UtcNow;
                    break;

                case TransferAction.Cancel:
                    if (transfer.Status is StockTransferStatus.Received or StockTransferStatus.Cancelled)
                        throw new InvalidOperationException("This transfer can no longer be cancelled.");
                    if (transfer.Status == StockTransferStatus.Dispatched)
                        await ApplySourceMovementAsync(
                            transfer, user.Id, 1m, "Cancelled transfer", operationId);
                    transfer.Status = StockTransferStatus.Cancelled;
                    transfer.CancelledByUserId = user.Id;
                    transfer.CancelledAt = DateTime.UtcNow;
                    transfer.CancellationReason = cancellationReason;
                    break;
            }

            await _db.SaveChangesAsync();
            await _db.CommitExternalTransactionAsync(transaction);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            await _db.RollbackExternalTransactionAsync(transaction);
            throw new InvalidOperationException(
                "The transfer or inventory changed on another device. Reload and try again.", ex);
        }
        catch (DbUpdateException ex) when (IsOperationKeyDuplicate(ex))
        {
            await _db.RollbackExternalTransactionAsync(transaction);
            // A repeated command whose ledger rows already exist is considered complete.
        }
        catch
        {
            await _db.RollbackExternalTransactionAsync(transaction);
            throw;
        }
    }

    private async Task ApplySourceMovementAsync(
        StockTransfer transfer,
        int userId,
        decimal direction,
        string notePrefix,
        string operationId)
    {
        var line = 0;
        foreach (var item in transfer.Items.OrderBy(x => x.Id))
        {
            var product = await _db.Products.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == item.ProductId && x.StoreId == transfer.StoreId)
                ?? throw new InvalidOperationException($"Source product {item.ProductName} no longer exists.");
            if (!product.StockQuantity.HasValue)
                throw new InvalidOperationException($"{item.ProductName} no longer tracks inventory.");
            var delta = direction * item.Quantity;
            var balance = product.StockQuantity.Value + delta;
            if (balance < 0) throw new InvalidOperationException($"Insufficient stock for {item.ProductName}.");
            product.StockQuantity = balance;
            product.UpdatedAt = DateTime.UtcNow;
            _db.StockTransactions.Add(new StockTransaction
            {
                StoreId = transfer.StoreId,
                ProductId = product.Id,
                OperationKey = $"{operationId}:{++line}",
                Type = StockTransactionType.Transfer,
                Quantity = delta,
                BalanceAfter = balance,
                UnitCost = item.UnitCost,
                StockTransferId = transfer.Id,
                StockTransferItemId = item.Id,
                UserId = userId,
                Note = $"{notePrefix} {transfer.TransferNumber}"
            });
        }
    }

    private async Task ApplyDestinationReceiptAsync(
        StockTransfer transfer,
        int userId,
        string operationId)
    {
        var line = 0;
        foreach (var item in transfer.Items.OrderBy(x => x.Id))
        {
            var product = await _db.Products.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == item.DestinationProductId &&
                                          x.StoreId == transfer.DestinationStoreId)
                ?? throw new InvalidOperationException(
                    $"Destination product {item.ProductName} no longer exists.");
            EnsureCompatibleDestination(product, item.Unit, item.ProductName);
            var balance = product.StockQuantity!.Value + item.Quantity;
            product.StockQuantity = balance;
            product.UpdatedAt = DateTime.UtcNow;
            _db.StockTransactions.Add(new StockTransaction
            {
                StoreId = transfer.DestinationStoreId,
                ProductId = product.Id,
                OperationKey = $"{operationId}:{++line}",
                Type = StockTransactionType.Transfer,
                Quantity = item.Quantity,
                BalanceAfter = balance,
                UnitCost = item.UnitCost,
                StockTransferId = transfer.Id,
                StockTransferItemId = item.Id,
                UserId = userId,
                Note = $"Transfer in {transfer.TransferNumber}"
            });
        }
    }

    public async Task<IReadOnlyList<StoreInventoryRow>> GetInventoryAcrossStoresAsync(
        int? storeId = null,
        string? query = null)
    {
        var allowedStore = ResolveReadableStore(storeId);
        var products = _db.Products.IgnoreQueryFilters().AsNoTracking().Where(x => x.IsActive);
        if (allowedStore.HasValue) products = products.Where(x => x.StoreId == allowedStore.Value);
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
        => await _db.StockTransfers.IgnoreQueryFilters()
               .Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == transferId)
           ?? throw new InvalidOperationException("Stock transfer not found.");

    private async Task<User> RequireManagerAsync(int userId, int storeId)
    {
        if (_session.UserId.HasValue && _session.UserId.Value != userId)
            throw new InvalidOperationException("The active user does not match this transfer action.");
        var user = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == userId && x.StoreId == storeId && x.IsActive)
            ?? throw new InvalidOperationException("An active user from the relevant store is required.");
        if (user.Role < UserRole.Manager)
            throw new InvalidOperationException("Manager permission is required for stock transfers.");
        return user;
    }

    private async Task<Product> EnsureDestinationProductAsync(Product source, int destinationStoreId)
    {
        var identifiers = new[] { source.Sku, source.Barcode }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim().ToLowerInvariant()).Distinct().ToList();
        var matches = identifiers.Count == 0
            ? new List<Product>()
            : await _db.Products.IgnoreQueryFilters()
                .Where(x => x.StoreId == destinationStoreId &&
                            ((x.Sku != null && identifiers.Contains(x.Sku.ToLower())) ||
                             (x.Barcode != null && identifiers.Contains(x.Barcode.ToLower()))))
                .ToListAsync();
        if (matches.Count > 1)
            throw new InvalidOperationException(
                $"Destination identifiers for {source.Name} match more than one product.");
        if (matches.Count == 1)
        {
            EnsureCompatibleDestination(matches[0], source.EffectiveUnit, source.Name);
            return matches[0];
        }

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

        var destination = new Product
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

    private static void EnsureCompatibleDestination(
        Product product,
        UnitOfMeasure expectedUnit,
        string productName)
    {
        if (!product.IsActive)
            throw new InvalidOperationException($"Destination product {productName} is inactive.");
        if (!product.StockQuantity.HasValue)
            throw new InvalidOperationException($"Destination product {productName} does not track inventory.");
        if (product.EffectiveUnit != expectedUnit)
            throw new InvalidOperationException($"Destination product {productName} uses a different unit.");
    }

    private int? ResolveReadableStore(int? requestedStore)
    {
        if (_session.IsAdmin) return requestedStore;
        var current = _session.StoreId ?? _storeContext.StoreId;
        if (requestedStore.HasValue && requestedStore.Value != current)
            throw new UnauthorizedAccessException("You cannot view another store's inventory or transfers.");
        return current;
    }

    private void EnsureReadableTransfer(StockTransfer transfer)
    {
        if (_session.IsAdmin) return;
        var current = _session.StoreId ?? _storeContext.StoreId;
        if (transfer.StoreId != current && transfer.DestinationStoreId != current)
            throw new UnauthorizedAccessException("You cannot view this transfer.");
    }

    private void EnsureWritableStore(int storeId)
    {
        if (_session.IsAdmin) return;
        var current = _session.StoreId ?? _storeContext.StoreId;
        if (storeId != current || _storeContext.StoreId != current)
            throw new UnauthorizedAccessException("You cannot change another store's transfer.");
    }

    private async Task<string> GenerateNumberAsync(int sourceStoreId)
    {
        var store = await _db.Stores.AsNoTracking().FirstAsync(x => x.Id == sourceStoreId);
        var compactCode = new string(store.Code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(compactCode)) compactCode = "STORE";
        if (compactCode.Length > 10) compactCode = compactCode[..10];
        return $"TR-{compactCode}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..35]
            .ToUpperInvariant();
    }

    private static string NormalizeOperationId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 8 or > 64 || normalized.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("The transfer operation ID is invalid.");
        return normalized;
    }

    private static bool IsOperationKeyDuplicate(DbUpdateException exception)
        => exception.InnerException?.Message.Contains("OperationKey", StringComparison.OrdinalIgnoreCase) == true ||
           exception.Message.Contains("OperationKey", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeOptional(string? value, int maximum, string field)
    {
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (text?.Length > maximum)
            throw new InvalidOperationException($"{field} cannot exceed {maximum} characters.");
        return text;
    }

    private enum TransferAction { Dispatch, Receive, Cancel }
}
