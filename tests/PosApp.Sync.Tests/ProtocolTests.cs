using PosApp.Core.Entities;
using PosApp.Core.Models;
using PosApp.Data;
using PosApp.Services;

namespace PosApp.Sync.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void RegistryCoversEveryOperationalEntityInDependencyOrder()
    {
        var names = SyncEntityRegistry.All.Select(value => value.EntityType).ToHashSet();
        Assert.Subset(names, new HashSet<string>
        {
            "categories", "taxes", "discounts", "users", "customers", "suppliers", "settings",
            "products", "register_sessions", "sales", "purchases", "sale_items", "payments",
            "purchase_items", "inventory_movements", "cash_movements", "expenses"
        });
        Assert.True(SyncEntityRegistry.All.First(value => value.EntityType == "categories").DependencyOrder <
                    SyncEntityRegistry.All.First(value => value.EntityType == "products").DependencyOrder);
        Assert.True(SyncEntityRegistry.All.First(value => value.EntityType == "sales").DependencyOrder <
                    SyncEntityRegistry.All.First(value => value.EntityType == "payments").DependencyOrder);
    }

    [Theory]
    [InlineData("sales")]
    [InlineData("payments")]
    [InlineData("purchases")]
    [InlineData("purchase_items")]
    [InlineData("inventory_movements")]
    [InlineData("cash_movements")]
    [InlineData("expenses")]
    public void FinancialEntitiesUseExplicitImmutableConflictPolicy(string entityType)
        => Assert.True(SyncEntityRegistry.All.Single(value => value.EntityType == entityType).IsFinancialOrImmutable);

    [Fact]
    public void RetryPolicyUsesBoundedExponentialBackoffWithJitter()
    {
        var first = SyncBackoffPolicy.ForAttempt(1);
        var fourth = SyncBackoffPolicy.ForAttempt(4);
        var extreme = SyncBackoffPolicy.ForAttempt(100);
        Assert.InRange(first.TotalSeconds, 2, 2.5);
        Assert.InRange(fourth.TotalSeconds, 16, 20);
        Assert.InRange(extreme.TotalSeconds, 256, 300);
    }

    [Fact]
    public void InventoryLedgerCarriesImmutableSaleAndPurchaseSources()
    {
        var descriptor = SyncEntityRegistry.All.Single(value => value.EntityType == "inventory_movements");
        var relationships = descriptor.Relationships.Select(value => value.PayloadProperty).ToHashSet();
        Assert.Contains("saleRecordId", relationships);
        Assert.Contains("saleItemRecordId", relationships);
        Assert.Contains("purchaseDocumentRecordId", relationships);
        Assert.Contains("purchaseItemRecordId", relationships);
        Assert.Contains(nameof(StockTransaction.BalanceAfter), descriptor.ExcludedProperties);
    }

    [Fact]
    public void ProtocolUsesExplicitApiAndSchemaVersionsAndBoundedBatches()
    {
        Assert.Equal(1, CloudProtocol.ApiVersion);
        Assert.Equal(4, CloudProtocol.ClientSchemaVersion);
        Assert.Equal("2.0.16", CloudProtocol.ClientVersion);
        Assert.Equal(2, CloudProtocol.MaxPushBatch);
        Assert.InRange(CloudProtocol.MaxPullBatch, 1, 200);
    }
}
