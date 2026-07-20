using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Models;
using PosApp.Data;

namespace PosApp.Sync.Tests;

public sealed class PullApplierTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"posapp-pull-{Guid.NewGuid():N}.db");
    private TestDbFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new TestDbFactory(_databasePath);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        SyncCaptureContext.Disable();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task IncrementalPullAppliesProductsCustomersAndRelationshipsInDependencyOrder()
    {
        var tenantId = Guid.NewGuid().ToString();
        var storeId = Guid.NewGuid().ToString();
        var serverDeviceId = Guid.NewGuid().ToString();
        var categoryRecordId = Guid.NewGuid().ToString();
        var productRecordId = Guid.NewGuid().ToString();
        var customerRecordId = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;
        var changes = new[]
        {
            Change(3, "products", productRecordId, storeId, serverDeviceId, now,
                new { name = "Tea", sku = "TEA-1", categoryRecordId, price = 12.5m,
                    costPrice = 8m, taxRate = 0m, unit = 0, trackInventory = true,
                    isWeighted = false, isActive = true, allowDiscount = true, createdAt = now }),
            Change(1, "categories", categoryRecordId, storeId, serverDeviceId, now,
                new { name = "Beverages", color = "#2D7FF9", sortOrder = 1,
                    isActive = true, createdAt = now }),
            Change(2, "customers", customerRecordId, storeId, serverDeviceId, now,
                new { name = "Amina", phone = "01700000000", loyaltyPoints = 0m,
                    storeCredit = 0m, loyaltyRate = 0m, isActive = true, createdAt = now })
        };

        var applied = await new SyncRecordApplier(_factory)
            .ApplyAsync(changes, tenantId, Guid.NewGuid().ToString());

        Assert.Equal(3, applied);
        await using var db = await _factory.CreateDbContextAsync();
        var product = await db.Products.Include(value => value.Category).SingleAsync();
        Assert.Equal("Tea", product.Name);
        Assert.Equal("Beverages", product.Category!.Name);
        Assert.Equal(0m, product.StockQuantity);
        Assert.Equal("Amina", (await db.Customers.SingleAsync()).Name);
        Assert.Equal(3, await db.SyncIdentities.CountAsync());
    }

    [Fact]
    public async Task AnotherDeviceChangeCreatesDiagnosableConflictWithoutOverwritingLocalEdit()
    {
        var tenantId = Guid.NewGuid().ToString();
        var storeId = Guid.NewGuid().ToString();
        var recordId = Guid.NewGuid().ToString();
        var firstDeviceId = Guid.NewGuid().ToString();
        var localDeviceId = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;
        var applier = new SyncRecordApplier(_factory);
        await applier.ApplyAsync(new[]
        {
            Change(1, "customers", recordId, storeId, firstDeviceId, now,
                new { name = "Cloud original", phone = "01700000001", loyaltyPoints = 0m,
                    storeCredit = 0m, loyaltyRate = 0m, isActive = true, createdAt = now })
        }, tenantId, localDeviceId);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            SyncCaptureContext.Enable(tenantId, storeId, localDeviceId, Guid.NewGuid().ToString());
            var customer = await db.Customers.SingleAsync();
            customer.Name = "Offline local edit";
            customer.UpdatedAt = now.AddMinutes(1);
            await db.SaveChangesAsync();
            SyncCaptureContext.Disable();
        }

        var remoteDeviceId = Guid.NewGuid().ToString();
        var remoteUpdatedAt = now.AddMinutes(2);
        var applied = await applier.ApplyAsync(new[]
        {
            Change(2, "customers", recordId, storeId, remoteDeviceId, remoteUpdatedAt,
                new { name = "Other terminal edit", phone = "01700000001", loyaltyPoints = 0m,
                    storeCredit = 0m, loyaltyRate = 0m, isActive = true, createdAt = now,
                    updatedAt = remoteUpdatedAt })
        }, tenantId, localDeviceId);

        Assert.Equal(0, applied);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal("Offline local edit", (await verify.Customers.SingleAsync()).Name);
        var conflict = await verify.SyncConflicts.SingleAsync();
        Assert.Equal(2, conflict.ServerVersion);
        Assert.Equal(storeId, conflict.ServerStoreId);
        Assert.Equal(remoteUpdatedAt, conflict.ServerUpdatedAtUtc);
        Assert.Equal(remoteDeviceId, conflict.ServerLastModifiedDeviceId);
        Assert.Contains("Other terminal edit", conflict.ServerPayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletedRecordPullRetainsIdentityAndAppliesTombstone()
    {
        var tenantId = Guid.NewGuid().ToString();
        var storeId = Guid.NewGuid().ToString();
        var recordId = Guid.NewGuid().ToString();
        var deviceId = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;
        var applier = new SyncRecordApplier(_factory);
        await applier.ApplyAsync(new[]
        {
            Change(1, "customers", recordId, storeId, deviceId, now,
                new { name = "Deleted later", phone = "01700000002", loyaltyPoints = 0m,
                    storeCredit = 0m, loyaltyRate = 0m, isActive = true, createdAt = now })
        }, tenantId, Guid.NewGuid().ToString());

        var deletedAt = now.AddMinutes(1);
        await applier.ApplyAsync(new[]
        {
            Change(2, "customers", recordId, storeId, deviceId, deletedAt,
                new { name = "Deleted later" }, deletedAt)
        }, tenantId, Guid.NewGuid().ToString());

        await using var db = await _factory.CreateDbContextAsync();
        Assert.False((await db.Customers.SingleAsync()).IsActive);
        var identity = await db.SyncIdentities.SingleAsync();
        Assert.Equal(2, identity.ServerVersion);
        Assert.Equal(deletedAt, identity.DeletedAtUtc);
        Assert.Equal(recordId, identity.RecordId);
    }

    [Fact]
    public async Task IdenticalCatalogKeysFromDifferentStoresRemainSeparateLocalWorkingCopies()
    {
        var tenantId = Guid.NewGuid().ToString();
        var firstStoreId = Guid.NewGuid().ToString();
        var secondStoreId = Guid.NewGuid().ToString();
        var deviceId = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;
        var firstCategoryId = Guid.NewGuid().ToString();
        var secondCategoryId = Guid.NewGuid().ToString();
        var applier = new SyncRecordApplier(_factory);

        await applier.ApplyAsync(new[]
        {
            Change(1, "categories", firstCategoryId, firstStoreId, deviceId, now,
                new { name = "Beverages", color = "#2D7FF9", sortOrder = 1, isActive = true, createdAt = now }),
            Change(2, "settings", Guid.NewGuid().ToString(), firstStoreId, deviceId, now,
                new { key = "store:config", value = "{\"storeName\":\"North\"}", createdAt = now }),
            Change(3, "products", Guid.NewGuid().ToString(), firstStoreId, deviceId, now,
                new { name = "Tea North", sku = "TEA-1", categoryRecordId = firstCategoryId,
                    price = 10m, costPrice = 6m, taxRate = 0m, unit = 0, trackInventory = true,
                    isWeighted = false, isActive = true, allowDiscount = true, createdAt = now })
        }, tenantId, Guid.NewGuid().ToString());

        await applier.ApplyAsync(new[]
        {
            Change(4, "categories", secondCategoryId, secondStoreId, deviceId, now,
                new { name = "Beverages", color = "#2D7FF9", sortOrder = 1, isActive = true, createdAt = now }),
            Change(5, "settings", Guid.NewGuid().ToString(), secondStoreId, deviceId, now,
                new { key = "store:config", value = "{\"storeName\":\"South\"}", createdAt = now }),
            Change(6, "products", Guid.NewGuid().ToString(), secondStoreId, deviceId, now,
                new { name = "Tea South", sku = "TEA-1", categoryRecordId = secondCategoryId,
                    price = 11m, costPrice = 7m, taxRate = 0m, unit = 0, trackInventory = true,
                    isWeighted = false, isActive = true, allowDiscount = true, createdAt = now })
        }, tenantId, Guid.NewGuid().ToString());

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await db.Categories.CountAsync());
        Assert.Equal(2, await db.Products.CountAsync(value => value.Sku == "TEA-1"));
        Assert.Equal(2, await db.Settings.CountAsync(value => value.Key == "store:config"));
        Assert.Contains(await db.SyncIdentities.ToListAsync(), value => value.StoreId == firstStoreId);
        Assert.Contains(await db.SyncIdentities.ToListAsync(), value => value.StoreId == secondStoreId);
    }

    private static SyncChangeDto Change(
        long version,
        string entityType,
        string recordId,
        string storeId,
        string deviceId,
        DateTime updatedAt,
        object payload,
        DateTime? deletedAt = null) => new()
        {
            Cursor = version,
            EntityType = entityType,
            RecordId = recordId,
            StoreId = storeId,
            Version = version,
            UpdatedAtUtc = updatedAt,
            DeletedAtUtc = deletedAt,
            LastModifiedDeviceId = deviceId,
            Payload = payload
        };

    private sealed class TestDbFactory(string databasePath) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new($"Data Source={databasePath}");

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
