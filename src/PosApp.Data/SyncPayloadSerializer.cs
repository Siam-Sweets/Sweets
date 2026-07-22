using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;

namespace PosApp.Data;

/// <summary>
/// Produces deterministic scalar-only payloads for snapshots and incremental
/// sync. Local image paths are deliberately excluded. Incremental payloads add
/// permanent SyncId references so foreign keys remain valid across devices even
/// when their local integer IDs differ.
/// </summary>
public static class SyncPayloadSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(object entity)
        => JsonSerializer.Serialize(CreateValues(entity), Options);

    public static string SerializeForSync(object entity, AppDbContext db)
        => JsonSerializer.Serialize(CreateValuesForSync(entity, db), Options);

    public static SortedDictionary<string, object?> CreateValuesForSync(object entity, AppDbContext db)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(db);
        var values = CreateValues(entity);
        AddReferenceSyncIds(values, entity, db);
        return values;
    }

    public static SortedDictionary<string, object?> CreateValues(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var values = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in entity.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
            if (property.GetCustomAttribute<NotMappedAttribute>() != null) continue;
            if (string.Equals(property.Name, "ImagePath", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsScalar(property.PropertyType)) continue;
            values[property.Name] = property.GetValue(entity);
        }
        return values;
    }

    private static void AddReferenceSyncIds(
        IDictionary<string, object?> values,
        object entity,
        AppDbContext db)
    {
        switch (entity)
        {
            case Product x:
                values["CategorySyncId"] = FindSyncId(db.Categories, x.CategoryId);
                break;
            case PurchaseDocument x:
                values["SupplierSyncId"] = FindSyncId(db.Suppliers, x.SupplierId);
                values["UserSyncId"] = FindSyncId(db.Users, x.UserId);
                break;
            case PurchaseItem x:
                values["PurchaseDocumentSyncId"] = FindSyncId(db.PurchaseDocuments, x.PurchaseDocumentId);
                values["ProductSyncId"] = FindSyncId(db.Products, x.ProductId);
                break;
            case Sale x:
                values["CustomerSyncId"] = FindSyncId(db.Customers, x.CustomerId);
                values["UserSyncId"] = FindSyncId(db.Users, x.UserId);
                values["CashSessionSyncId"] = FindSyncId(db.CashSessions, x.CashSessionId);
                values["RefundedSaleSyncId"] = FindSyncId(db.Sales, x.RefundedSaleId);
                break;
            case SaleItem x:
                values["SaleSyncId"] = FindSyncId(db.Sales, x.SaleId);
                values["ProductSyncId"] = FindSyncId(db.Products, x.ProductId);
                values["RefundedSaleItemSyncId"] = FindSyncId(db.SaleItems, x.RefundedSaleItemId);
                break;
            case SalePayment x:
                values["SaleSyncId"] = FindSyncId(db.Sales, x.SaleId);
                break;
            case StockTransfer x:
            {
                var destinationStore = FindStore(db, x.DestinationStoreId);
                values["DestinationStoreSyncId"] = destinationStore?.SyncId;
                values["DestinationStoreCode"] = destinationStore?.Code;
                values["DestinationStoreName"] = destinationStore?.Name;
                values["DestinationStoreAddress"] = destinationStore?.Address;
                values["DestinationStorePhone"] = destinationStore?.Phone;
                values["DestinationStoreIsActive"] = destinationStore?.IsActive;
                values["CreatedByUserSyncId"] = FindSyncId(db.Users, x.CreatedByUserId);
                values["DispatchedByUserSyncId"] = FindSyncId(db.Users, x.DispatchedByUserId);
                values["ReceivedByUserSyncId"] = FindSyncId(db.Users, x.ReceivedByUserId);
                values["CancelledByUserSyncId"] = FindSyncId(db.Users, x.CancelledByUserId);
                break;
            }
            case StockTransferItem x:
            {
                values["StockTransferSyncId"] = FindSyncId(db.StockTransfers, x.StockTransferId);
                values["ProductSyncId"] = FindSyncId(db.Products, x.ProductId);
                values["DestinationProductSyncId"] = FindSyncId(db.Products, x.DestinationProductId);
                var destinationProduct = FindProduct(db, x.DestinationProductId);
                values["DestinationCategorySyncId"] = destinationProduct == null
                    ? null
                    : FindSyncId(db.Categories, destinationProduct.CategoryId);
                break;
            }
            case StockTransaction x:
                values["ProductSyncId"] = FindSyncId(db.Products, x.ProductId);
                values["SaleSyncId"] = FindSyncId(db.Sales, x.SaleId);
                values["SaleItemSyncId"] = FindSyncId(db.SaleItems, x.SaleItemId);
                values["StockTransferSyncId"] = FindSyncId(db.StockTransfers, x.StockTransferId);
                values["StockTransferItemSyncId"] = FindSyncId(db.StockTransferItems, x.StockTransferItemId);
                values["UserSyncId"] = FindSyncId(db.Users, x.UserId);
                break;
            case CashSession x:
                values["OpenedByUserSyncId"] = FindSyncId(db.Users, x.OpenedByUserId);
                values["ClosedByUserSyncId"] = FindSyncId(db.Users, x.ClosedByUserId);
                break;
            case CashMovement x:
                values["CashSessionSyncId"] = FindSyncId(db.CashSessions, x.CashSessionId);
                values["UserSyncId"] = FindSyncId(db.Users, x.UserId);
                break;
        }
    }

    private static Store? FindStore(AppDbContext db, int? id)
    {
        if (!id.HasValue || id.Value <= 0) return null;
        return db.Stores.AsNoTracking().FirstOrDefault(x => x.Id == id.Value);
    }

    private static Product? FindProduct(AppDbContext db, int? id)
    {
        if (!id.HasValue || id.Value <= 0) return null;
        return db.Products.IgnoreQueryFilters().AsNoTracking().FirstOrDefault(x => x.Id == id.Value);
    }

    private static string? FindSyncId<TEntity>(DbSet<TEntity> set, int? id)
        where TEntity : StoreScopedEntity
    {
        if (!id.HasValue || id.Value <= 0) return null;
        return set.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => EF.Property<int>(x, "Id") == id.Value)
            .Select(x => x.SyncId)
            .FirstOrDefault();
    }

    private static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsEnum || type.IsPrimitive || type == typeof(string) ||
               type == typeof(decimal) || type == typeof(DateTime) ||
               type == typeof(DateTimeOffset) || type == typeof(Guid);
    }
}
