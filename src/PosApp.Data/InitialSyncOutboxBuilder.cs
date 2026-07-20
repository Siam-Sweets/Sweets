using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;

namespace PosApp.Data;

/// <summary>Queues a dependency-ordered snapshot without modifying business rows.</summary>
public static class InitialSyncOutboxBuilder
{
    public static async Task<IReadOnlyDictionary<string, int>> QueueAllAsync(
        AppDbContext db,
        string? migrationId = null,
        CancellationToken cancellationToken = default)
    {
        var capture = SyncCaptureContext.Current;
        if (!capture.Enabled)
            throw new InvalidOperationException("An online organization and store must be selected before migration.");

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        db.BypassStoreFilter = true;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.SuppressSyncCapture = true;
        try
        {
            foreach (var descriptor in SyncEntityRegistry.All)
            {
                var clrType = SyncEntityRegistry.GetClrType(descriptor.EntityType);
                if (clrType == null) continue;
                var rows = Query(db, clrType).Cast<object>().ToList();
                rows.RemoveAll(row => row is Setting setting &&
                                      setting.Key.StartsWith("app:", StringComparison.OrdinalIgnoreCase));
                counts[descriptor.EntityType] = rows.Count;
                if (rows.Count == 0) continue;

                var changes = rows
                    .Select(row => new TrackedSyncChange(row, EntityState.Modified, descriptor))
                    .ToArray();
                await LocalSyncOutboxCapture.CaptureAsync(
                    db, changes, capture, cancellationToken, isInitialMigration: true);
                await db.SaveChangesAsync(cancellationToken);
                db.ChangeTracker.Clear();
            }

            if (!string.IsNullOrWhiteSpace(migrationId))
            {
                var state = await db.CloudAccountStates.SingleAsync(cancellationToken);
                state.ActiveMigrationId = migrationId;
                state.ActiveMigrationStoreId = capture.StoreId;
                state.IsMigrationSnapshotQueued = true;
                state.UpdatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            SyncCaptureContext.NotifyOutboxChanged();
            return counts;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            db.SuppressSyncCapture = false;
        }
    }

    public static IReadOnlyDictionary<string, int> CountAll(AppDbContext db)
    {
        db.BypassStoreFilter = true;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in SyncEntityRegistry.All)
        {
            var clrType = SyncEntityRegistry.GetClrType(descriptor.EntityType);
            if (clrType != null)
                counts[descriptor.EntityType] = Query(db, clrType).Cast<object>().ToList().Count(row =>
                    row is not Setting setting ||
                    !setting.Key.StartsWith("app:", StringComparison.OrdinalIgnoreCase));
        }
        return counts;
    }

    /// <summary>
    /// Detects operational rows that have never been assigned a valid cloud
    /// identity for the organization being joined. This is intentionally run
    /// before the first background synchronization of an existing installation:
    /// silently combining those rows with a populated cloud store would make
    /// duplicate receipts, catalog entries, and inventory appear locally.
    /// </summary>
    public static bool HasUnlinkedRecords(AppDbContext db, string tenantId)
    {
        db.BypassStoreFilter = true;
        var linked = db.SyncIdentities.AsNoTracking()
            .Where(value => value.TenantId == tenantId)
            .AsEnumerable()
            .Where(value => Guid.TryParse(value.RecordId, out _))
            .Select(value => (value.EntityType.ToLowerInvariant(), value.LocalId))
            .ToHashSet();

        foreach (var descriptor in SyncEntityRegistry.All)
        {
            var clrType = SyncEntityRegistry.GetClrType(descriptor.EntityType);
            if (clrType == null) continue;
            var idProperty = clrType.GetProperty("Id")
                             ?? throw new InvalidOperationException($"{clrType.Name} has no local Id.");
            foreach (var row in Query(db, clrType).Cast<object>())
            {
                if (row is Setting setting &&
                    setting.Key.StartsWith("app:", StringComparison.OrdinalIgnoreCase))
                    continue;
                var localId = Convert.ToInt32(idProperty.GetValue(row));
                if (!linked.Contains((descriptor.EntityType.ToLowerInvariant(), localId)))
                    return true;
            }
        }
        return false;
    }

    private static IQueryable Query(AppDbContext db, Type clrType)
    {
        var setMethod = typeof(DbContext).GetMethods()
            .Single(method => method.Name == nameof(DbContext.Set) && method.IsGenericMethodDefinition &&
                              method.GetParameters().Length == 0);
        return (IQueryable)(setMethod.MakeGenericMethod(clrType).Invoke(db, null)
                            ?? throw new InvalidOperationException($"Unable to query {clrType.Name}."));
    }
}
