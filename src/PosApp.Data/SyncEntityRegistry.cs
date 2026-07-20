using PosApp.Core.Entities;

namespace PosApp.Data;

/// <summary>Single source of truth for local/cloud entity names and dependency order.</summary>
public static class SyncEntityRegistry
{
    private static readonly IReadOnlyDictionary<Type, SyncEntityDescriptor> ByType =
        new Dictionary<Type, SyncEntityDescriptor>
        {
            [typeof(Category)] = new("categories", 10, false, true),
            [typeof(Tax)] = new("taxes", 10, false, true),
            [typeof(Discount)] = new("discounts", 10, false, true),
            [typeof(User)] = new("users", 10, false, false,
                null, new[]
                {
                    nameof(User.PasswordHash), nameof(User.PasswordSalt),
                    nameof(User.LastLoginAt), nameof(User.ImagePath)
                }),
            [typeof(Customer)] = new("customers", 20, false, true),
            [typeof(Supplier)] = new("suppliers", 20, false, true),
            [typeof(Setting)] = new("settings", 20, false, true),
            [typeof(Product)] = new("products", 30, false, true,
                new[] { new SyncRelationship(nameof(Product.CategoryId), "categories") },
                new[] { nameof(Product.ImagePath), nameof(Product.StockQuantity) }),
            [typeof(CashSession)] = new("register_sessions", 40, true, true,
                new[]
                {
                    new SyncRelationship(nameof(CashSession.OpenedByUserId), "users"),
                    new SyncRelationship(nameof(CashSession.ClosedByUserId), "users")
                }),
            [typeof(Sale)] = new("sales", 50, true, true,
                new[]
                {
                    new SyncRelationship(nameof(Sale.CustomerId), "customers"),
                    new SyncRelationship(nameof(Sale.UserId), "users"),
                    new SyncRelationship(nameof(Sale.CashSessionId), "register_sessions"),
                    new SyncRelationship(nameof(Sale.RefundedSaleId), "sales")
                }),
            [typeof(PurchaseDocument)] = new("purchases", 50, true, true,
                new[]
                {
                    new SyncRelationship(nameof(PurchaseDocument.SupplierId), "suppliers"),
                    new SyncRelationship(nameof(PurchaseDocument.UserId), "users")
                }),
            [typeof(SaleItem)] = new("sale_items", 60, true, true,
                new[]
                {
                    new SyncRelationship(nameof(SaleItem.SaleId), "sales"),
                    new SyncRelationship(nameof(SaleItem.ProductId), "products"),
                    new SyncRelationship(nameof(SaleItem.PromotionId), "discounts"),
                    new SyncRelationship(nameof(SaleItem.RefundedSaleItemId), "sale_items")
                }),
            [typeof(SalePayment)] = new("payments", 60, true, true,
                new[] { new SyncRelationship(nameof(SalePayment.SaleId), "sales") }),
            [typeof(PurchaseItem)] = new("purchase_items", 60, true, true,
                new[]
                {
                    new SyncRelationship(nameof(PurchaseItem.PurchaseDocumentId), "purchases"),
                    new SyncRelationship(nameof(PurchaseItem.ProductId), "products")
                }),
            [typeof(StockTransaction)] = new("inventory_movements", 70, true, true,
                new[]
                {
                    new SyncRelationship(nameof(StockTransaction.ProductId), "products"),
                    new SyncRelationship(nameof(StockTransaction.SaleId), "sales"),
                    new SyncRelationship(nameof(StockTransaction.SaleItemId), "sale_items"),
                    new SyncRelationship(nameof(StockTransaction.PurchaseDocumentId), "purchases"),
                    new SyncRelationship(nameof(StockTransaction.PurchaseItemId), "purchase_items"),
                    new SyncRelationship(nameof(StockTransaction.UserId), "users")
                }, new[] { nameof(StockTransaction.BalanceAfter) }),
            [typeof(CashMovement)] = new("cash_movements", 70, true, true,
                new[]
                {
                    new SyncRelationship(nameof(CashMovement.CashSessionId), "register_sessions"),
                    new SyncRelationship(nameof(CashMovement.UserId), "users")
                }),
            [typeof(Expense)] = new("expenses", 70, true, true,
                new[] { new SyncRelationship(nameof(Expense.UserId), "users") })
        };

    private static readonly IReadOnlyDictionary<string, SyncEntityDescriptor> ByName =
        ByType.Values.ToDictionary(value => value.EntityType, StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(Type type, out SyncEntityDescriptor descriptor)
        => ByType.TryGetValue(type, out descriptor!);

    public static bool TryGet(string entityType, out SyncEntityDescriptor descriptor)
        => ByName.TryGetValue(entityType, out descriptor!);

    public static IReadOnlyList<SyncEntityDescriptor> All { get; } =
        ByType.Values.OrderBy(value => value.DependencyOrder).ToArray();

    public static Type? GetClrType(string entityType)
        => ByType.FirstOrDefault(pair => string.Equals(
            pair.Value.EntityType, entityType, StringComparison.OrdinalIgnoreCase)).Key;
}

public sealed record SyncEntityDescriptor(
    string EntityType,
    int DependencyOrder,
    bool IsFinancialOrImmutable,
    bool StoreScoped,
    IReadOnlyList<SyncRelationship>? RelationshipList = null,
    IReadOnlyList<string>? ExcludedPropertyList = null)
{
    public IReadOnlyList<SyncRelationship> Relationships { get; } = RelationshipList ?? Array.Empty<SyncRelationship>();
    public IReadOnlySet<string> ExcludedProperties { get; } = new HashSet<string>(
        ExcludedPropertyList ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
}

public sealed record SyncRelationship(string LocalProperty, string TargetEntityType)
{
    public string PayloadProperty =>
        char.ToLowerInvariant(LocalProperty[0]) + LocalProperty[1..^2] + "RecordId";
}
