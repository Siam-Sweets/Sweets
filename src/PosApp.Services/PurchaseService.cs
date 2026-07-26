using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Core.Utilities;
using PosApp.Data;

namespace PosApp.Services;

public class PurchaseService : IPurchaseService
{
    private readonly AppDbContext _db;

    public PurchaseService(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<Supplier>> SearchSuppliersAsync(string? query = null, bool includeInactive = false)
    {
        var suppliers = _db.Suppliers.AsNoTracking().AsQueryable();
        if (!includeInactive) suppliers = suppliers.Where(s => s.IsActive);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            suppliers = suppliers.Where(s =>
                EF.Functions.Like(s.Name, $"%{term}%") ||
                (s.Phone != null && EF.Functions.Like(s.Phone, $"%{term}%")) ||
                (s.Email != null && EF.Functions.Like(s.Email, $"%{term}%")));
        }
        return await suppliers.OrderByDescending(s => s.IsActive).ThenBy(s => s.Name).ToListAsync();
    }

    public async Task<Supplier> CreateOrUpdateSupplierAsync(Supplier supplier)
    {
        ArgumentNullException.ThrowIfNull(supplier);
        var name = supplier.Name?.Trim() ?? string.Empty;
        var phone = Normalize(supplier.Phone);
        var email = Normalize(supplier.Email);
        var address = Normalize(supplier.Address);
        var taxId = Normalize(supplier.TaxId);
        var notes = Normalize(supplier.Notes);
        ValidateLength(name, 100, "Supplier name", required: true);
        ValidateLength(phone, 30, "Phone");
        ValidateLength(email, 120, "Email");
        ValidateLength(address, 300, "Address");
        ValidateLength(taxId, 40, "Tax ID");
        ValidateLength(notes, 500, "Notes");

        if (supplier.Id == 0)
        {
            var created = new Supplier
            {
                Name = name, Phone = phone, Email = email, Address = address, TaxId = taxId, Notes = notes,
                IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            };
            _db.Suppliers.Add(created);
            await _db.SaveChangesAsync();
            supplier.Id = created.Id;
            supplier.IsActive = true;
            supplier.CreatedAt = created.CreatedAt;
            supplier.UpdatedAt = created.UpdatedAt;
            return created;
        }

        var tracked = await _db.Suppliers.FindAsync(supplier.Id)
            ?? throw new InvalidOperationException("Supplier not found.");
        tracked.Name = name;
        tracked.Phone = phone;
        tracked.Email = email;
        tracked.Address = address;
        tracked.TaxId = taxId;
        tracked.Notes = notes;
        tracked.IsActive = supplier.IsActive;
        tracked.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return tracked;
    }

    public async Task SetSupplierActiveAsync(int supplierId, bool isActive)
    {
        if (supplierId <= 0) throw new InvalidOperationException("Select a valid supplier.");
        var supplier = await _db.Suppliers.FindAsync(supplierId)
            ?? throw new InvalidOperationException("Supplier not found.");
        supplier.IsActive = isActive;
        supplier.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<PurchaseDocument>> GetPurchasesAsync(DateTime from, DateTime to)
    {
        var (fromUtc, toUtcExclusive) = DateTimeUtilities.InclusiveLocalDateRange(from, to);
        return await _db.PurchaseDocuments.AsNoTracking()
            .Include(p => p.Supplier)
            .Include(p => p.Items)
            .Where(p => p.DocumentDate >= fromUtc && p.DocumentDate < toUtcExclusive)
            .OrderByDescending(p => p.DocumentDate)
            .ToListAsync();
    }

    public async Task<PurchaseDocument> PostPurchaseAsync(PurchaseDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        draft.OperationId = NormalizeOperationId(draft.OperationId);
        var existing = await _db.PurchaseDocuments.AsNoTracking()
            .Include(x => x.Supplier).Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.OperationId == draft.OperationId);
        if (existing != null) return existing;

        var externalReference = Normalize(draft.ExternalReference);
        var note = Normalize(draft.Note);
        ValidateLength(externalReference, 80, "External reference");
        ValidateLength(note, 500, "Purchase note");
        if (draft.Lines == null || draft.Lines.Count == 0)
            throw new InvalidOperationException("Add at least one product to the purchase.");
        if (draft.UserId <= 0)
            throw new InvalidOperationException("A signed-in user is required.");
        if (draft.SupplierId is <= 0)
            throw new InvalidOperationException("Select a valid supplier.");
        if (draft.Lines.Any(line => line.ProductId <= 0 || line.Quantity <= 0m ||
                                    line.UnitCost < 0m || line.TaxRate is < 0m or > 100m))
            throw new InvalidOperationException(
                "Purchase products and quantities must be valid, cost can be 0 but cannot be negative, and tax must be between 0 and 100.");

        using var operation = SyncOperationScope.Begin(draft.OperationId);
        _db.ChangeTracker.Clear();
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            if (!await _db.Users.AsNoTracking().AnyAsync(user => user.Id == draft.UserId && user.IsActive))
                throw new InvalidOperationException(
                    "The signed-in user no longer exists or is inactive. Sign in again.");

            var supplier = await _db.Suppliers.FirstOrDefaultAsync(x => x.Id == draft.SupplierId.Value)
                           ?? throw new InvalidOperationException("Supplier not found.");
            if (!supplier.IsActive)
                throw new InvalidOperationException("The selected supplier is inactive.");

            var now = DateTime.UtcNow;
            var document = new PurchaseDocument
            {
                OperationId = draft.OperationId,
                DocumentNumber = GenerateDocumentNumber(now),
                ExternalReference = externalReference,
                Supplier = supplier,
                UserId = draft.UserId,
                DocumentDate = NormalizeLocalDateToUtc(draft.DocumentDate),
                StockDate = NormalizeLocalDateToUtc(draft.StockDate),
                Status = PurchaseStatus.Posted,
                Subtotal = draft.Subtotal,
                TaxTotal = draft.TaxTotal,
                Total = draft.Total,
                Note = note
            };

            var newestPostedCostProductIds = new HashSet<int>();
            foreach (var line in draft.Lines.GroupBy(line => line.ProductId).Select(group => group.Last()))
            {
                if (await IsLatestPostedPurchaseDateAsync(line.ProductId, document.DocumentDate))
                    newestPostedCostProductIds.Add(line.ProductId);
            }

            var lineNumber = 0;
            foreach (var line in draft.Lines)
            {
                var product = await _db.Products.FirstOrDefaultAsync(x => x.Id == line.ProductId)
                              ?? throw new InvalidOperationException($"Product not found: {line.ProductName}");
                if (!product.IsActive) throw new InvalidOperationException($"{product.Name} is inactive.");
                if (!product.StockQuantity.HasValue)
                    throw new InvalidOperationException($"{product.Name} is not stock-tracked.");

                var oldQuantity = product.StockQuantity.Value;
                var newQuantity = oldQuantity + line.Quantity;
                product.StockQuantity = newQuantity;
                if (newestPostedCostProductIds.Contains(product.Id))
                    product.CostPrice = line.UnitCost;
                product.UpdatedAt = now;

                var purchaseItem = new PurchaseItem
                {
                    PurchaseDocument = document, Product = product,
                    ProductName = product.Name,
                    Quantity = line.Quantity, UnitCost = line.UnitCost, TaxRate = line.TaxRate
                };
                document.Items.Add(purchaseItem);
                _db.StockTransactions.Add(new StockTransaction
                {
                    Product = product,
                    OperationKey = $"{draft.OperationId}:purchase:{++lineNumber}",
                    Type = StockTransactionType.Purchase,
                    Quantity = line.Quantity, BalanceAfter = newQuantity, UnitCost = line.UnitCost,
                    Note = $"Purchase {document.DocumentNumber} - {supplier.Name}",
                    UserId = draft.UserId, CreatedAt = document.StockDate
                });
            }

            _db.PurchaseDocuments.Add(document);
            await _db.SaveChangesAsync();
            await _db.CommitExternalTransactionAsync(transaction);
            _db.ChangeTracker.Clear();
            return await _db.PurchaseDocuments.AsNoTracking()
                .Include(x => x.Supplier).Include(x => x.Items)
                .FirstAsync(x => x.OperationId == draft.OperationId);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            await _db.RollbackExternalTransactionAsync(transaction);
            throw new InvalidOperationException(
                "Inventory changed while this purchase was posting. Reload the products and try again.", ex);
        }
        catch (DbUpdateException)
        {
            await _db.RollbackExternalTransactionAsync(transaction);
            var duplicate = await _db.PurchaseDocuments.AsNoTracking()
                .Include(x => x.Supplier).Include(x => x.Items)
                .FirstOrDefaultAsync(x => x.OperationId == draft.OperationId);
            if (duplicate != null) return duplicate;
            throw;
        }
        catch
        {
            await _db.RollbackExternalTransactionAsync(transaction);
            throw;
        }
    }

    public async Task<PurchaseDocument> UpdatePurchaseItemUnitCostAsync(int purchaseItemId, decimal unitCost)
    {
        if (purchaseItemId <= 0)
            throw new InvalidOperationException("Select a valid purchase line.");
        if (unitCost < 0m)
            throw new InvalidOperationException("Cost cannot be negative.");

        _db.ChangeTracker.Clear();
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var item = await _db.PurchaseItems
                .Include(purchaseItem => purchaseItem.PurchaseDocument)
                    .ThenInclude(document => document!.Items)
                .FirstOrDefaultAsync(purchaseItem => purchaseItem.Id == purchaseItemId)
                ?? throw new InvalidOperationException("Purchase line not found.");
            var document = item.PurchaseDocument
                ?? throw new InvalidOperationException("Purchase document not found.");
            if (document.Status != PurchaseStatus.Posted)
                throw new InvalidOperationException("Only posted purchases can update product cost.");

            item.UnitCost = unitCost;
            document.Subtotal = document.Items.Sum(line => line.LineSubtotal);
            document.TaxTotal = document.Items.Sum(line => line.LineTax);
            document.Total = document.Subtotal + document.TaxTotal;
            document.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();
            await ApplyLatestPurchaseCostAsync(item.ProductId, document.UpdatedAt.Value);
            await _db.SaveChangesAsync();
            await _db.CommitExternalTransactionAsync(transaction);
            _db.ChangeTracker.Clear();

            return await _db.PurchaseDocuments.AsNoTracking()
                .Include(x => x.Supplier).Include(x => x.Items)
                .FirstAsync(x => x.Id == document.Id);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            await _db.RollbackExternalTransactionAsync(transaction);
            throw new InvalidOperationException(
                "Purchase or product cost changed on another screen or device. Reload and try again.", ex);
        }
        catch
        {
            await _db.RollbackExternalTransactionAsync(transaction);
            throw;
        }
    }

    private async Task ApplyLatestPurchaseCostAsync(int productId, DateTime updatedAt)
    {
        var latest = await _db.PurchaseItems.AsNoTracking()
            .Where(item => item.ProductId == productId &&
                           item.PurchaseDocument != null &&
                           item.PurchaseDocument.Status == PurchaseStatus.Posted)
            .OrderByDescending(item => item.PurchaseDocument!.DocumentDate)
            .ThenByDescending(item => item.PurchaseDocumentId)
            .ThenByDescending(item => item.Id)
            .Select(item => new { item.UnitCost })
            .FirstOrDefaultAsync();
        if (latest == null) return;

        var product = await _db.Products.FirstOrDefaultAsync(item => item.Id == productId);
        if (product == null) return;

        product.CostPrice = latest.UnitCost;
        product.UpdatedAt = updatedAt;
    }

    private async Task<bool> IsLatestPostedPurchaseDateAsync(int productId, DateTime documentDate)
    {
        var latestDate = await _db.PurchaseItems.AsNoTracking()
            .Where(item => item.ProductId == productId &&
                           item.PurchaseDocument != null &&
                           item.PurchaseDocument.Status == PurchaseStatus.Posted)
            .OrderByDescending(item => item.PurchaseDocument!.DocumentDate)
            .Select(item => (DateTime?)item.PurchaseDocument!.DocumentDate)
            .FirstOrDefaultAsync();
        return latestDate == null || documentDate >= latestDate.Value;
    }

    private static string NormalizeOperationId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 8 or > 64 || normalized.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("The purchase operation ID is invalid.");
        return normalized;
    }

    private static string GenerateDocumentNumber(DateTime utcNow)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        return $"PUR-{utcNow:yyyyMMdd-HHmmssfff}-{suffix}";
    }

    private static DateTime NormalizeLocalDateToUtc(DateTime value)
    {
        if (value.Kind == DateTimeKind.Utc) return value;
        return DateTimeUtilities.LocalDateStartUtc(value);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ValidateLength(string? value, int max, string field, bool required = false)
    {
        if (required && string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{field} is required.");
        if (value?.Length > max)
            throw new InvalidOperationException($"{field} cannot exceed {max} characters.");
    }
}
