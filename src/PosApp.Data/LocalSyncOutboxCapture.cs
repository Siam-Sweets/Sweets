using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using PosApp.Core.Entities;

namespace PosApp.Data;

internal sealed record TrackedSyncChange(
    object Entity,
    EntityState State,
    SyncEntityDescriptor Descriptor,
    int LocalId = 0);

internal static class LocalSyncOutboxCapture
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static IReadOnlyList<TrackedSyncChange> Snapshot(AppDbContext db)
        => db.ChangeTracker.Entries()
            .Where(entry => entry.State == EntityState.Added ||
                            entry.State == EntityState.Modified ||
                            entry.State == EntityState.Deleted)
            .Where(entry => !IsInventoryBalanceOnly(entry))
            .Where(entry => !IsLocalOnlySetting(entry.Entity))
            .Select(entry => SyncEntityRegistry.TryGet(entry.Entity.GetType(), out var descriptor)
                ? new TrackedSyncChange(entry.Entity, entry.State, descriptor)
                : null)
            .Where(change => change != null)
            .Cast<TrackedSyncChange>()
            .ToArray();

    /// <summary>
    /// Resolves permanent database keys after the operational rows have been
    /// written. Sync metadata must never be created from EF temporary/default
    /// keys because a later edit or delete would be treated as another record.
    /// </summary>
    public static IReadOnlyList<TrackedSyncChange> BindPersistedKeys(
        AppDbContext db,
        IReadOnlyList<TrackedSyncChange> changes)
        => changes.Select(change =>
        {
            var entry = db.Entry(change.Entity);
            var keyProperty = entry.Metadata.FindPrimaryKey()?.Properties.SingleOrDefault()
                              ?? throw new InvalidOperationException(
                                  $"SYNC_LOCAL_KEY_UNAVAILABLE: {change.Descriptor.EntityType} has no primary key.");
            var key = entry.Property(keyProperty.Name);
            var localId = Convert.ToInt32(key.CurrentValue);
            if (key.IsTemporary || localId <= 0)
                throw new InvalidOperationException(
                    $"SYNC_LOCAL_KEY_UNAVAILABLE: {change.Descriptor.EntityType} has no permanent local key after SaveChanges.");
            return change with { LocalId = localId };
        }).ToArray();

    public static async Task CaptureAsync(
        AppDbContext db,
        IReadOnlyList<TrackedSyncChange> changes,
        SyncCaptureSnapshot capture,
        CancellationToken cancellationToken,
        bool isInitialMigration = false)
    {
        if (changes.Count == 0) return;
        var invalidKey = changes.FirstOrDefault(change => change.LocalId <= 0);
        if (invalidKey != null)
            throw new InvalidOperationException(
                $"SYNC_LOCAL_KEY_UNAVAILABLE: {invalidKey.Descriptor.EntityType} has no permanent local key.");

        var capturingUserId = capture.UserId ?? throw new InvalidOperationException(
            "SYNC_USER_REQUIRED: synchronized local changes require an attributed cloud user.");

        var identities = new Dictionary<(string EntityType, int LocalId), SyncIdentity>();
        var changedProductIds = changes
            .Where(change => change.Descriptor.EntityType == "products")
            .Select(change => change.LocalId)
            .ToHashSet();
        foreach (var change in changes)
        {
            var localId = change.LocalId;
            var identity = await FindOrCreateIdentityAsync(
                db, change.Descriptor.EntityType, localId, capture, identities, cancellationToken);
            identities[(change.Descriptor.EntityType, localId)] = identity;
        }

        // Create identities for referenced rows before payloads are serialized.
        foreach (var change in changes)
        {
            foreach (var relationship in change.Descriptor.Relationships)
            {
                var localId = GetNullableInt(change.Entity, relationship.LocalProperty);
                if (localId is not > 0) continue;
                await FindOrCreateIdentityAsync(
                    db, relationship.TargetEntityType, localId.Value, capture, identities, cancellationToken);
            }
        }

        foreach (var change in changes)
        {
            var localId = change.LocalId;
            var identity = identities[(change.Descriptor.EntityType, localId)];
            var operationKind = change.State == EntityState.Deleted
                ? SyncOperationKind.Delete
                : SyncOperationKind.Upsert;
            // Retain relationship metadata locally even for deletes. The wire
            // protocol discards delete payloads, but the sender uses this metadata
            // to keep a recalled sale's draft-line changes ahead of its final
            // header transition when a large transaction spans several batches.
            var (expectedItemCount, expectedPaymentCount) = operationKind == SyncOperationKind.Upsert
                ? await GetCompositionCountsAsync(db, change.Entity, localId, cancellationToken)
                : ((int?)null, (int?)null);
            var (catalogVersion, legacyPriceSnapshot) = operationKind == SyncOperationKind.Upsert
                ? await GetCatalogSnapshotAsync(
                    db, change.Entity, identities, changedProductIds, isInitialMigration, cancellationToken)
                : ((long?)null, false);
            var payload = Serialize(
                change.Entity, change.Descriptor, identities, expectedItemCount, expectedPaymentCount,
                catalogVersion, legacyPriceSnapshot);

            identity.UpdatedAtUtc = DateTime.UtcNow;
            identity.LastModifiedDeviceId = capture.DeviceId;
            identity.StoreId = change.Descriptor.StoreScoped ? capture.StoreId : null;
            identity.DeletedAtUtc = operationKind == SyncOperationKind.Delete ? DateTime.UtcNow : null;

            // Compact unsent edits to the same row even when another signed-in
            // user made the latest edit. The pending operation is re-attributed
            // to that user below, avoiding two writes with the same base version.
            var pending = await db.SyncOutboxOperations.FirstOrDefaultAsync(operation =>
                    operation.EntityType == change.Descriptor.EntityType &&
                    operation.RecordId == identity.RecordId &&
                    operation.Status == SyncOutboxStatus.Pending,
                cancellationToken);

            // A row that was created and deleted before its first upload never
            // existed in the cloud. Cancel the unsent insert instead of emitting a
            // delete with baseVersion 0, which the server must correctly reject as
            // RECORD_NOT_FOUND. The identity is retained as a local tombstone so
            // the UUID cannot accidentally be reused.
            if (pending != null && pending.BaseVersion == 0 &&
                pending.Operation == SyncOperationKind.Upsert &&
                operationKind == SyncOperationKind.Delete)
            {
                db.SyncOutboxOperations.Remove(pending);
                continue;
            }

            if (pending == null)
            {
                db.SyncOutboxOperations.Add(new SyncOutboxOperation
                {
                    EntityType = change.Descriptor.EntityType,
                    RecordId = identity.RecordId,
                    LocalId = localId,
                    StoreId = change.Descriptor.StoreScoped ? capture.StoreId : null,
                    CreatedByUserId = capturingUserId,
                    Operation = operationKind,
                    BaseVersion = identity.ServerVersion,
                    PayloadJson = payload
                });
            }
            else
            {
                pending.Operation = operationKind;
                pending.CreatedByUserId = capturingUserId;
                pending.PayloadJson = payload;
                pending.LastErrorCode = null;
                pending.LastErrorMessage = null;
                pending.NextAttemptAtUtc = null;
            }
        }
    }

    private static async Task<SyncIdentity> FindOrCreateIdentityAsync(
        AppDbContext db,
        string entityType,
        int localId,
        SyncCaptureSnapshot capture,
        IDictionary<(string EntityType, int LocalId), SyncIdentity> cache,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue((entityType, localId), out var cached)) return cached;

        var identity = await db.SyncIdentities.FirstOrDefaultAsync(value =>
                value.EntityType == entityType && value.LocalId == localId,
            cancellationToken);
        if (identity == null)
        {
            var storeScoped = SyncEntityRegistry.TryGet(entityType, out var descriptor) && descriptor.StoreScoped;
            identity = new SyncIdentity
            {
                EntityType = entityType,
                LocalId = localId,
                TenantId = capture.TenantId,
                StoreId = storeScoped ? capture.StoreId : null,
                LastModifiedDeviceId = capture.DeviceId
            };
            db.SyncIdentities.Add(identity);
        }

        cache[(entityType, localId)] = identity;
        return identity;
    }

    private static string Serialize(
        object entity,
        SyncEntityDescriptor descriptor,
        IReadOnlyDictionary<(string EntityType, int LocalId), SyncIdentity> identities,
        int? expectedItemCount,
        int? expectedPaymentCount,
        long? catalogVersion,
        bool legacyPriceSnapshot)
    {
        var relationships = descriptor.Relationships.ToDictionary(
            value => value.LocalProperty, StringComparer.OrdinalIgnoreCase);
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in entity.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0 ||
                string.Equals(property.Name, "Id", StringComparison.OrdinalIgnoreCase) ||
                descriptor.ExcludedProperties.Contains(property.Name) ||
                property.GetCustomAttribute<NotMappedAttribute>() != null)
                continue;

            if (relationships.TryGetValue(property.Name, out var relationship))
            {
                var localId = GetNullableInt(entity, property.Name);
                payload[relationship.PayloadProperty] = localId is > 0 &&
                                                        identities.TryGetValue((relationship.TargetEntityType, localId.Value), out var target)
                    ? target.RecordId
                    : null;
                continue;
            }

            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (!propertyType.IsValueType && propertyType != typeof(string)) continue;
            payload[ToCamelCase(property.Name)] = property.GetValue(entity);
        }

        if (entity is Product product)
            payload["trackInventory"] = product.StockQuantity.HasValue;
        if (entity is Sale)
        {
            payload["expectedItemCount"] = expectedItemCount ?? 0;
            payload["expectedPaymentCount"] = expectedPaymentCount ?? 0;
        }
        if (entity is PurchaseDocument)
            payload["expectedItemCount"] = expectedItemCount ?? 0;
        if (entity is SaleItem)
        {
            payload["catalogVersion"] = catalogVersion ?? 0;
            payload["legacyPriceSnapshot"] = legacyPriceSnapshot;
        }

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static async Task<(int? ItemCount, int? PaymentCount)> GetCompositionCountsAsync(
        AppDbContext db,
        object entity,
        int persistedLocalId,
        CancellationToken cancellationToken)
    {
        // Capture runs after SaveChanges(false). EF has already replaced the
        // temporary key in its tracked property, but the entity CLR Id can still
        // expose its pre-save default value. Query immutable children with the
        // permanent key bound by BindPersistedKeys instead of entity.Id.
        if (entity is Sale)
        {
            var items = await db.SaleItems.IgnoreQueryFilters()
                .CountAsync(value => value.SaleId == persistedLocalId, cancellationToken);
            var payments = await db.SalePayments.IgnoreQueryFilters()
                .CountAsync(value => value.SaleId == persistedLocalId, cancellationToken);
            return (items, payments);
        }
        if (entity is PurchaseDocument)
        {
            var items = await db.PurchaseItems.IgnoreQueryFilters()
                .CountAsync(value => value.PurchaseDocumentId == persistedLocalId, cancellationToken);
            return (items, null);
        }
        return (null, null);
    }

    private static async Task<(long? CatalogVersion, bool LegacyPriceSnapshot)> GetCatalogSnapshotAsync(
        AppDbContext db,
        object entity,
        IReadOnlyDictionary<(string EntityType, int LocalId), SyncIdentity> identities,
        IReadOnlySet<int> changedProductIds,
        bool isInitialMigration,
        CancellationToken cancellationToken)
    {
        if (entity is not SaleItem saleItem) return (null, false);
        if (isInitialMigration) return (0, true);
        if (!identities.TryGetValue(("products", saleItem.ProductId), out var productIdentity))
            return (0, false);

        var trackedPending = db.ChangeTracker.Entries<SyncOutboxOperation>().Any(entry =>
            entry.State != EntityState.Deleted &&
            entry.Entity.EntityType == "products" &&
            entry.Entity.RecordId == productIdentity.RecordId &&
            (entry.Entity.Status == SyncOutboxStatus.Pending ||
             entry.Entity.Status == SyncOutboxStatus.Uploading));
        var storedPending = await db.SyncOutboxOperations.AsNoTracking().AnyAsync(operation =>
                operation.EntityType == "products" &&
                operation.RecordId == productIdentity.RecordId &&
                (operation.Status == SyncOutboxStatus.Pending ||
                 operation.Status == SyncOutboxStatus.Uploading),
            cancellationToken);
        var anticipatedWrite = changedProductIds.Contains(saleItem.ProductId) || trackedPending || storedPending;
        return (productIdentity.ServerVersion + (anticipatedWrite ? 1 : 0), false);
    }

    private static bool IsInventoryBalanceOnly(EntityEntry entry)
    {
        if (entry.Entity is not Product || entry.State != EntityState.Modified) return false;
        var modified = entry.Properties.Where(property => property.IsModified)
            .Select(property => property.Metadata.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (modified.Count == 0 || modified.Any(name => name is not (nameof(Product.StockQuantity) or nameof(Product.UpdatedAt))))
            return false;
        var stock = entry.Property(nameof(Product.StockQuantity));
        return (stock.OriginalValue == null) == (stock.CurrentValue == null);
    }

    private static bool IsLocalOnlySetting(object entity)
        => entity is Setting setting && setting.Key.StartsWith("app:", StringComparison.OrdinalIgnoreCase);

    private static int? GetNullableInt(object entity, string propertyName)
    {
        var value = entity.GetType().GetProperty(propertyName)?.GetValue(entity);
        return value == null ? null : Convert.ToInt32(value);
    }

    private static string ToCamelCase(string value)
        => value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];
}
