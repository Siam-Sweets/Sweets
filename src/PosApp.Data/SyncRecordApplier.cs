using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Models;

namespace PosApp.Data;

/// <summary>Applies server changes without echoing them back into the outbox.</summary>
public sealed class SyncRecordApplier
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public SyncRecordApplier(IDbContextFactory<AppDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<int> ApplyAsync(
        IReadOnlyList<SyncChangeDto> changes,
        string tenantId,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (changes.Count == 0) return 0;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        db.SuppressSyncCapture = true;
        db.BypassStoreFilter = true;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var applied = 0;
        var inventoryProductRecordIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var ordered = changes.OrderBy(change =>
                    SyncEntityRegistry.TryGet(change.EntityType, out var descriptor)
                        ? descriptor.DependencyOrder
                        : int.MaxValue)
                .ThenBy(change => change.Cursor);

            foreach (var change in ordered)
            {
                if (!SyncEntityRegistry.TryGet(change.EntityType, out var descriptor))
                    throw new InvalidOperationException(
                        $"CLIENT_VERSION_INCOMPATIBLE: unsupported synchronized entity {change.EntityType}");

                var pending = await db.SyncOutboxOperations.AsNoTracking().AnyAsync(operation =>
                        operation.RecordId == change.RecordId &&
                        (operation.Status == SyncOutboxStatus.Pending ||
                         operation.Status == SyncOutboxStatus.Uploading),
                    cancellationToken);
                var identity = await db.SyncIdentities.SingleOrDefaultAsync(
                    value => value.RecordId == change.RecordId, cancellationToken);

                if (pending && identity != null && change.Version > identity.ServerVersion &&
                    !string.Equals(change.LastModifiedDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    await AddConflictAsync(db, change, identity, cancellationToken);
                    continue;
                }

                if (change.DeletedAtUtc != null)
                {
                    if (identity != null)
                    {
                        await ApplyTombstoneAsync(db, descriptor, identity, cancellationToken);
                        identity.ServerVersion = change.Version;
                        identity.DeletedAtUtc = change.DeletedAtUtc;
                        identity.UpdatedAtUtc = change.UpdatedAtUtc;
                        identity.LastModifiedDeviceId = change.LastModifiedDeviceId;
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    applied++;
                    continue;
                }

                var payload = ToJsonElement(change.Payload);
                if (change.EntityType == "inventory_movements" &&
                    payload.TryGetProperty("productRecordId", out var productRecordId) &&
                    productRecordId.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(productRecordId.GetString()))
                    inventoryProductRecordIds.Add(productRecordId.GetString()!);
                var entity = identity == null
                    ? await FindNaturalMatchAsync(db, descriptor.EntityType, payload, cancellationToken)
                    : await FindByLocalIdAsync(db, descriptor.EntityType, identity.LocalId, cancellationToken);
                var isNew = entity == null;
                entity ??= Activator.CreateInstance(SyncEntityRegistry.GetClrType(descriptor.EntityType)!)
                           ?? throw new InvalidOperationException($"Unable to create {descriptor.EntityType}.");

                ApplyScalarProperties(entity, descriptor, payload);
                await ApplyRelationshipsAsync(db, entity, descriptor, payload, cancellationToken);
                if (isNew) db.Add(entity);
                await db.SaveChangesAsync(cancellationToken);

                var localId = Convert.ToInt32(entity.GetType().GetProperty("Id")!.GetValue(entity));
                if (identity == null)
                {
                    identity = new SyncIdentity
                    {
                        EntityType = descriptor.EntityType,
                        LocalId = localId,
                        RecordId = change.RecordId,
                        TenantId = tenantId,
                        StoreId = descriptor.StoreScoped ? change.StoreId : null,
                        CreatedAtUtc = change.UpdatedAtUtc
                    };
                    db.SyncIdentities.Add(identity);
                }
                identity.ServerVersion = change.Version;
                identity.UpdatedAtUtc = change.UpdatedAtUtc;
                identity.DeletedAtUtc = null;
                identity.LastModifiedDeviceId = change.LastModifiedDeviceId;
                await db.SaveChangesAsync(cancellationToken);
                applied++;
            }

            foreach (var productRecordId in inventoryProductRecordIds)
                await RecalculateInventoryAsync(db, productRecordId, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return applied;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task RecalculateInventoryAsync(
        AppDbContext db,
        string productRecordId,
        CancellationToken cancellationToken)
    {
        var productIdentity = await db.SyncIdentities.AsNoTracking().SingleOrDefaultAsync(value =>
            value.EntityType == "products" && value.RecordId == productRecordId, cancellationToken);
        if (productIdentity == null) return;
        var product = await db.Products.FindAsync(new object[] { productIdentity.LocalId }, cancellationToken);
        if (product == null) return;
        var movements = await db.StockTransactions.Where(value => value.ProductId == product.Id)
            .OrderBy(value => value.CreatedAt).ThenBy(value => value.Id).ToListAsync(cancellationToken);
        decimal balance = 0;
        foreach (var movement in movements)
        {
            balance += movement.Quantity;
            movement.BalanceAfter = balance;
        }
        product.StockQuantity = balance;
        product.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task AddConflictAsync(
        AppDbContext db,
        SyncChangeDto change,
        SyncIdentity identity,
        CancellationToken cancellationToken)
    {
        if (await db.SyncConflicts.AnyAsync(value =>
                value.RecordId == change.RecordId && value.ServerVersion == change.Version &&
                value.Status == SyncConflictStatus.Unresolved, cancellationToken)) return;
        var pending = await db.SyncOutboxOperations.AsNoTracking().FirstOrDefaultAsync(value =>
            value.RecordId == change.RecordId && value.Status == SyncOutboxStatus.Pending, cancellationToken);
        db.SyncConflicts.Add(new SyncConflict
        {
            EntityType = change.EntityType,
            RecordId = change.RecordId,
            OperationId = pending?.OperationId,
            LocalBaseVersion = identity.ServerVersion,
            ServerVersion = change.Version,
            LocalPayloadJson = pending?.PayloadJson ?? "{}",
            ServerPayloadJson = ToJsonElement(change.Payload).GetRawText(),
            ServerStoreId = change.StoreId,
            ServerUpdatedAtUtc = change.UpdatedAtUtc,
            ServerDeletedAtUtc = change.DeletedAtUtc,
            ServerLastModifiedDeviceId = change.LastModifiedDeviceId
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task ApplyTombstoneAsync(
        AppDbContext db,
        SyncEntityDescriptor descriptor,
        SyncIdentity identity,
        CancellationToken cancellationToken)
    {
        var entity = await FindByLocalIdAsync(db, descriptor.EntityType, identity.LocalId, cancellationToken);
        if (entity == null) return;
        var activeProperty = entity.GetType().GetProperty("IsActive");
        if (activeProperty?.CanWrite == true && activeProperty.PropertyType == typeof(bool))
            activeProperty.SetValue(entity, false);
        // Financial history and settings are retained locally. The identity's
        // tombstone prevents them from being shown by sync-aware views and keeps
        // the deletion available for other devices.
    }

    private static void ApplyScalarProperties(object entity, SyncEntityDescriptor descriptor, JsonElement payload)
    {
        if (entity is Product product && payload.TryGetProperty("trackInventory", out var trackInventory) &&
            trackInventory.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            if (!trackInventory.GetBoolean()) product.StockQuantity = null;
            else if (!product.StockQuantity.HasValue) product.StockQuantity = 0m;
        }
        var relationshipNames = descriptor.Relationships
            .Select(value => value.LocalProperty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var property in entity.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || property.Name == "Id" || relationshipNames.Contains(property.Name) ||
                descriptor.ExcludedProperties.Contains(property.Name) ||
                property.GetCustomAttribute<NotMappedAttribute>() != null)
                continue;
            var jsonName = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            if (!payload.TryGetProperty(jsonName, out var value)) continue;
            property.SetValue(entity, ConvertValue(value, property.PropertyType));
        }
    }

    private static async Task ApplyRelationshipsAsync(
        AppDbContext db,
        object entity,
        SyncEntityDescriptor descriptor,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        foreach (var relationship in descriptor.Relationships)
        {
            var property = entity.GetType().GetProperty(relationship.LocalProperty);
            if (property?.CanWrite != true || !payload.TryGetProperty(relationship.PayloadProperty, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Null)
            {
                if (Nullable.GetUnderlyingType(property.PropertyType) != null) property.SetValue(entity, null);
                continue;
            }
            var recordId = value.GetString();
            var identity = string.IsNullOrWhiteSpace(recordId)
                ? null
                : await db.SyncIdentities.AsNoTracking().SingleOrDefaultAsync(item =>
                    item.RecordId == recordId && item.EntityType == relationship.TargetEntityType,
                    cancellationToken);
            if (identity == null)
            {
                if (Nullable.GetUnderlyingType(property.PropertyType) != null) continue;
                throw new InvalidOperationException(
                    $"SYNC_DEPENDENCY_MISSING: {descriptor.EntityType}.{relationship.LocalProperty}");
            }
            property.SetValue(entity, identity.LocalId);
        }
    }

    private static async Task<object?> FindByLocalIdAsync(
        AppDbContext db,
        string entityType,
        int localId,
        CancellationToken cancellationToken)
    {
        var clrType = SyncEntityRegistry.GetClrType(entityType);
        return clrType == null ? null : await db.FindAsync(clrType, new object[] { localId }, cancellationToken);
    }

    private static async Task<object?> FindNaturalMatchAsync(
        AppDbContext db,
        string entityType,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        // Read JSON before constructing EF queries. Expression trees cannot
        // contain calls to a local function, while captured scalar values are
        // translated safely into SQLite parameters.
        var name = PayloadText(payload, "name");
        var code = PayloadText(payload, "code");
        var username = PayloadText(payload, "username");
        var key = PayloadText(payload, "key");
        var sku = PayloadText(payload, "sku");
        var barcode = PayloadText(payload, "barcode");
        var email = PayloadText(payload, "email");
        var phone = PayloadText(payload, "phone");
        var hasCode = !string.IsNullOrEmpty(code);
        var hasSku = !string.IsNullOrEmpty(sku);
        var hasBarcode = !string.IsNullOrEmpty(barcode);
        var hasEmail = !string.IsNullOrEmpty(email);
        var hasPhone = !string.IsNullOrEmpty(phone);

        return entityType switch
        {
            "categories" => await db.Categories.FirstOrDefaultAsync(value =>
                value.Name == name && !db.SyncIdentities.Any(identity =>
                    identity.EntityType == "categories" && identity.LocalId == value.Id), cancellationToken),
            "taxes" => await db.Taxes.FirstOrDefaultAsync(value =>
                value.Name == name && !db.SyncIdentities.Any(identity =>
                    identity.EntityType == "taxes" && identity.LocalId == value.Id), cancellationToken),
            "discounts" => await db.Discounts.FirstOrDefaultAsync(value =>
                ((hasCode && value.Code == code) || value.Name == name) &&
                !db.SyncIdentities.Any(identity => identity.EntityType == "discounts" && identity.LocalId == value.Id),
                cancellationToken),
            "users" => await db.Users.FirstOrDefaultAsync(value => value.Username == username &&
                !db.SyncIdentities.Any(identity => identity.EntityType == "users" && identity.LocalId == value.Id),
                cancellationToken),
            "settings" => await db.Settings.FirstOrDefaultAsync(value => value.Key == key &&
                !db.SyncIdentities.Any(identity => identity.EntityType == "settings" && identity.LocalId == value.Id),
                cancellationToken),
            "products" => await db.Products.FirstOrDefaultAsync(value =>
                ((hasSku && value.Sku == sku) || (hasBarcode && value.Barcode == barcode)) &&
                !db.SyncIdentities.Any(identity => identity.EntityType == "products" && identity.LocalId == value.Id),
                cancellationToken),
            "customers" => await db.Customers.FirstOrDefaultAsync(value =>
                ((hasEmail && value.Email == email) || (hasPhone && value.Phone == phone)) &&
                !db.SyncIdentities.Any(identity => identity.EntityType == "customers" && identity.LocalId == value.Id),
                cancellationToken),
            "suppliers" => await db.Suppliers.FirstOrDefaultAsync(value =>
                ((hasEmail && value.Email == email) || (hasPhone && value.Phone == phone)) &&
                !db.SyncIdentities.Any(identity => identity.EntityType == "suppliers" && identity.LocalId == value.Id),
                cancellationToken),
            _ => null
        };
    }

    private static string? PayloadText(JsonElement payload, string name)
        => payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static object? ConvertValue(JsonElement value, Type targetType)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        var actualType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (actualType == typeof(string)) return value.GetString();
        if (actualType == typeof(bool)) return value.GetBoolean();
        if (actualType == typeof(int)) return value.GetInt32();
        if (actualType == typeof(long)) return value.GetInt64();
        if (actualType == typeof(decimal)) return value.GetDecimal();
        if (actualType == typeof(double)) return value.GetDouble();
        if (actualType == typeof(DateTime)) return value.GetDateTime().ToUniversalTime();
        if (actualType == typeof(Guid)) return value.GetGuid();
        if (actualType.IsEnum)
            return value.ValueKind == JsonValueKind.String
                ? Enum.Parse(actualType, value.GetString()!, true)
                : Enum.ToObject(actualType, value.GetInt32());
        return JsonSerializer.Deserialize(value.GetRawText(), targetType);
    }

    private static JsonElement ToJsonElement(object value)
        => value is JsonElement element
            ? element
            : JsonSerializer.SerializeToElement(value, value.GetType());
}
