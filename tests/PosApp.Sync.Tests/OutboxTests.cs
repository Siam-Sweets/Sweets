using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;
using PosApp.Core.Models;
using PosApp.Data;
using PosApp.Services;

namespace PosApp.Sync.Tests;

public sealed class OutboxTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"posapp-sync-{Guid.NewGuid():N}.db");
    private AppDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _db = new AppDbContext($"Data Source={_databasePath}");
        await _db.Database.EnsureCreatedAsync();
        SyncCaptureContext.Enable(
            "00000000-0000-4000-8000-000000000001",
            "00000000-0000-4000-8000-000000000002",
            "00000000-0000-4000-8000-000000000003",
            "00000000-0000-4000-8000-000000000004");
    }

    public async Task DisposeAsync()
    {
        SyncCaptureContext.Disable();
        await _db.DisposeAsync();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
    }

    [Fact]
    public async Task OfflineProductAndRelationshipAreCapturedAtomicallyWithUuids()
    {
        var category = new Category { Name = "Beverages" };
        _db.Categories.Add(category);
        await _db.SaveChangesAsync();
        var product = new Product
        {
            Name = "Tea", Sku = "TEA-1", CategoryId = category.Id,
            Price = 12.5m, CostPrice = 8m, StockQuantity = 5m
        };
        _db.Products.Add(product);
        await _db.SaveChangesAsync();

        var identity = await _db.SyncIdentities.SingleAsync(value =>
            value.EntityType == "products" && value.LocalId == product.Id);
        Assert.True(Guid.TryParse(identity.RecordId, out _));
        var operation = await _db.SyncOutboxOperations.SingleAsync(value =>
            value.EntityType == "products" && value.LocalId == product.Id);
        using var payload = JsonDocument.Parse(operation.PayloadJson);
        Assert.True(Guid.TryParse(payload.RootElement.GetProperty("categoryRecordId").GetString(), out _));
        Assert.True(payload.RootElement.GetProperty("trackInventory").GetBoolean());
        Assert.False(payload.RootElement.TryGetProperty("stockQuantity", out _));
        Assert.Equal(0, operation.BaseVersion);
        Assert.Equal("00000000-0000-4000-8000-000000000004", operation.CreatedByUserId);
        Assert.Equal(SyncOutboxStatus.Pending, operation.Status);
    }

    [Fact]
    public async Task CallerTransactionRollsBackBusinessRowAndOutboxTogether()
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();
        _db.Customers.Add(new Customer { Name = "Rolled back customer" });
        await _db.SaveChangesAsync();

        Assert.Single(await _db.Customers.Where(value => value.Name == "Rolled back customer").ToListAsync());
        Assert.Single(await _db.SyncOutboxOperations.Where(value => value.EntityType == "customers").ToListAsync());

        await transaction.RollbackAsync();
        _db.ChangeTracker.Clear();

        Assert.Empty(await _db.Customers.Where(value => value.Name == "Rolled back customer").ToListAsync());
        Assert.Empty(await _db.SyncOutboxOperations.Where(value => value.EntityType == "customers").ToListAsync());
        Assert.Empty(await _db.SyncIdentities.Where(value => value.EntityType == "customers").ToListAsync());
    }

    [Fact]
    public async Task RapidOfflineEditsCompactIntoOneIdempotentPendingOperation()
    {
        var customer = new Customer { Name = "First name", Phone = "01700000000" };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();
        var original = await _db.SyncOutboxOperations.SingleAsync(value => value.EntityType == "customers");

        customer.Name = "Corrected name";
        customer.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var operations = await _db.SyncOutboxOperations.Where(value => value.EntityType == "customers").ToListAsync();
        Assert.Single(operations);
        Assert.Equal(original.OperationId, operations[0].OperationId);
        Assert.Contains("Corrected name", operations[0].PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OfflineCreateThenDeleteCancelsTheUnsentInsertButRetainsItsIdentity()
    {
        var category = new Category { Name = "Temporary" };
        _db.Categories.Add(category);
        await _db.SaveChangesAsync();
        _db.Categories.Remove(category);
        await _db.SaveChangesAsync();

        var identity = await _db.SyncIdentities.SingleAsync(value => value.EntityType == "categories");
        Assert.Empty(await _db.SyncOutboxOperations.Where(value => value.EntityType == "categories").ToListAsync());
        Assert.NotNull(identity.DeletedAtUtc);
        Assert.True(Guid.TryParse(identity.RecordId, out _));
    }

    [Fact]
    public async Task SyncedDeleteCreatesATombstoneWithTheObservedServerVersion()
    {
        var category = new Category { Name = "Published" };
        _db.Categories.Add(category);
        await _db.SaveChangesAsync();
        var insert = await _db.SyncOutboxOperations.SingleAsync(value => value.EntityType == "categories");
        var identity = await _db.SyncIdentities.SingleAsync(value => value.EntityType == "categories");
        insert.Status = SyncOutboxStatus.Synchronized;
        identity.ServerVersion = 1;
        await _db.SaveChangesAsync();

        _db.Categories.Remove(category);
        await _db.SaveChangesAsync();

        var deletion = await _db.SyncOutboxOperations.SingleAsync(value =>
            value.EntityType == "categories" && value.Status == SyncOutboxStatus.Pending);
        Assert.Equal(SyncOperationKind.Delete, deletion.Operation);
        Assert.Equal(1, deletion.BaseVersion);
        Assert.NotNull(identity.DeletedAtUtc);
    }

    [Fact]
    public async Task InventoryBalanceChangesUseTheLedgerWithoutCreatingProductVersionNoise()
    {
        var product = new Product { Name = "Coffee", Price = 5m, StockQuantity = 10m };
        _db.Products.Add(product);
        await _db.SaveChangesAsync();
        var productOperation = await _db.SyncOutboxOperations.SingleAsync(value => value.EntityType == "products");
        productOperation.Status = SyncOutboxStatus.Synchronized;
        (await _db.SyncIdentities.SingleAsync(value => value.EntityType == "products")).ServerVersion = 1;
        _db.SuppressSyncCapture = true;
        await _db.SaveChangesAsync();
        _db.SuppressSyncCapture = false;

        product.StockQuantity = 9m;
        product.UpdatedAt = DateTime.UtcNow;
        _db.StockTransactions.Add(new StockTransaction
        {
            ProductId = product.Id,
            Type = StockTransactionType.Adjustment,
            Quantity = -1m,
            BalanceAfter = 9m,
        });
        await _db.SaveChangesAsync();

        Assert.Single(await _db.SyncOutboxOperations.Where(value => value.EntityType == "products").ToListAsync());
        Assert.Single(await _db.SyncOutboxOperations.Where(value => value.EntityType == "inventory_movements").ToListAsync());
    }

    [Fact]
    public async Task SaleHeaderDeclaresItsImmutableLineAndPaymentComposition()
    {
        var user = new User { Username = "cashier-1", FullName = "Cashier", IsActive = true };
        var product = new Product { Name = "Tea", Sku = "TEA-COMP", Price = 10m, CostPrice = 6m };
        _db.AddRange(user, product);
        await _db.SaveChangesAsync();
        var sale = new Sale
        {
            ReceiptNumber = "SYNC-COMPOSITION-1", UserId = user.Id, Status = SaleStatus.Completed,
            Subtotal = 10m, AmountPaid = 10m,
            Items = new List<SaleItem>
            {
                new() { ProductId = product.Id, ProductName = product.Name, Quantity = 1m,
                    UnitPrice = 10m, CostPrice = 6m }
            },
            Payments = new List<SalePayment>
            {
                new() { Method = PaymentMethod.Cash, Amount = 10m }
            }
        };
        _db.Sales.Add(sale);
        await _db.SaveChangesAsync();

        var operation = await _db.SyncOutboxOperations.SingleAsync(value =>
            value.EntityType == "sales" && value.LocalId == sale.Id);
        using var payload = JsonDocument.Parse(operation.PayloadJson);
        Assert.Equal(1, payload.RootElement.GetProperty("expectedItemCount").GetInt32());
        Assert.Equal(1, payload.RootElement.GetProperty("expectedPaymentCount").GetInt32());
    }

    [Fact]
    public async Task ExistingDatabaseMigrationQueuesSnapshotInDependencyOrder()
    {
        SyncCaptureContext.Disable();
        var category = new Category { Name = "Legacy category" };
        var customer = new Customer { Name = "Legacy customer", Phone = "01700000003" };
        _db.AddRange(category, customer);
        await _db.SaveChangesAsync();
        _db.Products.Add(new Product
        {
            Name = "Legacy product", Sku = "LEGACY-1", CategoryId = category.Id,
            Price = 20m, CostPrice = 12m, StockQuantity = null
        });
        await _db.SaveChangesAsync();

        const string tenantId = "00000000-0000-4000-8000-000000000001";
        Assert.True(InitialSyncOutboxBuilder.HasUnlinkedRecords(_db, tenantId));
        SyncCaptureContext.Enable(
            tenantId,
            "00000000-0000-4000-8000-000000000002",
            "00000000-0000-4000-8000-000000000003",
            "00000000-0000-4000-8000-000000000004");
        var counts = await InitialSyncOutboxBuilder.QueueAllAsync(_db, cancellationToken: default);

        Assert.Equal(1, counts["categories"]);
        Assert.Equal(1, counts["customers"]);
        Assert.Equal(1, counts["products"]);
        var operations = await _db.SyncOutboxOperations.OrderBy(value => value.Id).ToListAsync();
        Assert.True(operations.Single(value => value.EntityType == "categories").Id <
                    operations.Single(value => value.EntityType == "products").Id);
        Assert.All(operations, value => Assert.Equal(SyncOutboxStatus.Pending, value.Status));
        Assert.False(InitialSyncOutboxBuilder.HasUnlinkedRecords(_db, tenantId));
    }

    [Fact]
    public async Task DeviceLocalSetupStateNeverEntersTheCloudOutboxOrMigrationSnapshot()
    {
        _db.Settings.Add(new Setting { Key = SetupService.SetupCompleteKey, Value = "true" });
        await _db.SaveChangesAsync();

        Assert.Empty(await _db.SyncOutboxOperations.Where(value => value.EntityType == "settings").ToListAsync());
        var counts = await InitialSyncOutboxBuilder.QueueAllAsync(_db, cancellationToken: default);
        Assert.Equal(0, counts["settings"]);
        Assert.Empty(await _db.SyncOutboxOperations.Where(value => value.EntityType == "settings").ToListAsync());
    }

    [Fact]
    public async Task ExistingOrganizationOnboardingRemovesOnlyBootstrapDataAndKnownLegacyUsers()
    {
        _db.SuppressSyncCapture = true;
        await DbSeeder.SeedAsync(_db);
        Assert.Empty(await _db.Users.ToListAsync());
        var current = new User { Username = "online-admin", FullName = "Online Admin", Role = UserRole.Admin };
        var legacy = new User { Username = "cashier", FullName = "Default Cashier", Role = UserRole.Cashier };
        _db.AddRange(current, legacy);
        await _db.SaveChangesAsync();
        _db.SuppressSyncCapture = false;

        var setup = new SetupService(_db, new SettingsService(_db));
        var requiresInitialMigration = await setup.CompleteOnlineSetupAsync(new CloudAuthenticationResult
        {
            OrganizationId = "00000000-0000-4000-8000-000000000010",
            LocalUser = current,
            Store = new CloudStoreDto { Name = "Dhaka Branch" }
        }, createdOrganization: false);
        Assert.False(requiresInitialMigration);
        Assert.False(await setup.IsSetupCompleteAsync());
        await setup.FinalizeOnlineSetupAsync();

        Assert.True(await setup.IsSetupCompleteAsync());
        Assert.Single(await _db.Users.ToListAsync());
        Assert.Equal(current.Id, (await _db.Users.SingleAsync()).Id);
        Assert.Empty(await _db.Categories.ToListAsync());
        Assert.Empty(await _db.Taxes.ToListAsync());
        Assert.Empty(await _db.Discounts.ToListAsync());
        Assert.Equal("Dhaka Branch", (await new SettingsService(_db).GetStoreSettingsAsync()).StoreName);
    }

    [Fact]
    public async Task InterruptedNewOrganizationSetupRetainsItsInitialMigrationRequirement()
    {
        _db.SuppressSyncCapture = true;
        await DbSeeder.SeedAsync(_db);
        var current = new User { Username = "owner", FullName = "Store Owner", Role = UserRole.Admin };
        _db.Users.Add(current);
        await _db.SaveChangesAsync();
        _db.SuppressSyncCapture = false;

        var setup = new SetupService(_db, new SettingsService(_db));
        var authentication = new CloudAuthenticationResult
        {
            OrganizationId = "00000000-0000-4000-8000-000000000011",
            LocalUser = current,
            Store = new CloudStoreDto { Name = "Chattogram Branch" }
        };

        Assert.True(await setup.CompleteOnlineSetupAsync(authentication, createdOrganization: true));
        Assert.False(await setup.IsSetupCompleteAsync());

        // A restart no longer carries the transient `createdOrganization` flag.
        // The device-local preparation marker must preserve the safe upload path.
        Assert.True(await setup.CompleteOnlineSetupAsync(authentication, createdOrganization: false));
        Assert.False(await setup.IsSetupCompleteAsync());

        await setup.FinalizeOnlineSetupAsync();
        Assert.True(await setup.IsSetupCompleteAsync());
    }

    [Fact]
    public async Task DatabaseUpgradeKeepsOneOpenRegisterPerCachedBranch()
    {
        SyncCaptureContext.Disable();
        _db.BypassStoreFilter = true;
        var user = new User { Username = "register-owner", FullName = "Register Owner", Role = UserRole.Admin };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var north = new CashSession { OpenedByUserId = user.Id, OpenedAt = DateTime.UtcNow.AddMinutes(-5) };
        var south = new CashSession { OpenedByUserId = user.Id, OpenedAt = DateTime.UtcNow };
        _db.CashSessions.AddRange(north, south);
        await _db.SaveChangesAsync();
        _db.SyncIdentities.AddRange(
            new SyncIdentity
            {
                EntityType = "register_sessions", LocalId = north.Id,
                TenantId = "00000000-0000-4000-8000-000000000020",
                StoreId = "00000000-0000-4000-8000-000000000021"
            },
            new SyncIdentity
            {
                EntityType = "register_sessions", LocalId = south.Id,
                TenantId = "00000000-0000-4000-8000-000000000020",
                StoreId = "00000000-0000-4000-8000-000000000022"
            });
        await _db.SaveChangesAsync();

        await DbSchemaUpgrader.ApplyAsync(_db);
        _db.ChangeTracker.Clear();

        Assert.Equal(2, await _db.CashSessions.IgnoreQueryFilters()
            .CountAsync(value => value.ClosedAt == null));
    }

    [Fact]
    public async Task StoreSettingsWithTheSameKeyRemainIsolatedPerCachedBranch()
    {
        const string tenantId = "00000000-0000-4000-8000-000000000030";
        const string northStore = "00000000-0000-4000-8000-000000000031";
        const string southStore = "00000000-0000-4000-8000-000000000032";
        const string deviceId = "00000000-0000-4000-8000-000000000003";
        const string userId = "00000000-0000-4000-8000-000000000004";
        var settings = new SettingsService(_db);

        SyncCaptureContext.Enable(tenantId, northStore, deviceId, userId);
        await settings.SetAsync("store:test", "north");
        SyncCaptureContext.Enable(tenantId, southStore, deviceId, userId);
        await settings.SetAsync("store:test", "south");

        Assert.Equal(2, await _db.Settings.IgnoreQueryFilters()
            .CountAsync(value => value.Key == "store:test"));
        Assert.Equal("south", await settings.GetAsync("store:test"));

        SyncCaptureContext.Enable(tenantId, northStore, deviceId, userId);
        Assert.Equal("north", await settings.GetAsync("store:test"));
    }
}
