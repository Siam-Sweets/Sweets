using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PosApp.Core.Entities;

namespace PosApp.Data;

/// <summary>
/// EF Core DbContext for the local SQLite database. The DB file lives
/// under the current user's local application-data folder.
/// </summary>
public class AppDbContext : DbContext
{
    private readonly string _connectionString;

    /// <summary>Used only while applying trusted server changes.</summary>
    public bool SuppressSyncCapture { get; set; }

    /// <summary>Background migration/apply operations must be able to see every store.</summary>
    public bool BypassStoreFilter { get; set; }

    private bool CloudStoreFilterEnabled => SyncCaptureContext.Current.Enabled &&
                                            !string.IsNullOrWhiteSpace(SyncCaptureContext.Current.StoreId);
    private string CurrentCloudStoreId => SyncCaptureContext.Current.StoreId;

    public AppDbContext(string connectionString)
        : base()
    {
        _connectionString = connectionString;
    }

    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
        _connectionString = string.Empty;
    }

    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleItem> SaleItems => Set<SaleItem>();
    public DbSet<SalePayment> SalePayments => Set<SalePayment>();
    public DbSet<StockTransaction> StockTransactions => Set<StockTransaction>();
    public DbSet<Tax> Taxes => Set<Tax>();
    public DbSet<Discount> Discounts => Set<Discount>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<PurchaseDocument> PurchaseDocuments => Set<PurchaseDocument>();
    public DbSet<PurchaseItem> PurchaseItems => Set<PurchaseItem>();
    public DbSet<CashSession> CashSessions => Set<CashSession>();
    public DbSet<CashMovement> CashMovements => Set<CashMovement>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<CloudAccountState> CloudAccountStates => Set<CloudAccountState>();
    public DbSet<SyncIdentity> SyncIdentities => Set<SyncIdentity>();
    public DbSet<SyncOutboxOperation> SyncOutboxOperations => Set<SyncOutboxOperation>();
    public DbSet<SyncCursorState> SyncCursorStates => Set<SyncCursorState>();
    public DbSet<SyncConflict> SyncConflicts => Set<SyncConflict>();
    public DbSet<CloudCachedStore> CloudCachedStores => Set<CloudCachedStore>();
    public DbSet<CloudCachedDeviceSession> CloudCachedDeviceSessions => Set<CloudCachedDeviceSession>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured && !string.IsNullOrEmpty(_connectionString))
        {
            optionsBuilder.UseSqlite(_connectionString);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Product
        modelBuilder.Entity<Product>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Name).IsRequired();
            b.Property(p => p.Sku).UseCollation("NOCASE");
            b.Property(p => p.Barcode).UseCollation("NOCASE");
            b.Property(p => p.Price).HasColumnType("decimal(18,4)");
            b.Property(p => p.CostPrice).HasColumnType("decimal(18,4)");
            b.Property(p => p.TaxRate).HasColumnType("decimal(6,3)");
            b.Property(p => p.StockQuantity).HasColumnType("decimal(18,4)");
            b.Property(p => p.LowStockThreshold).HasColumnType("decimal(18,4)");
            // Cloud branch ownership lives in SyncIdentities. These indexes are
            // intentionally non-unique at the physical SQLite level so two
            // branches can use the same catalog identifiers; service validation
            // still enforces uniqueness inside the currently selected branch.
            b.HasIndex(p => p.Sku);
            b.HasIndex(p => p.Barcode);
            b.HasOne(p => p.Category)
                .WithMany(c => c.Products)
                .HasForeignKey(p => p.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Category
        modelBuilder.Entity<Category>(b =>
        {
            b.HasKey(c => c.Id);
            b.Property(c => c.Name).IsRequired();
            b.HasIndex(c => c.Name);
        });

        // Customer
        modelBuilder.Entity<Customer>(b =>
        {
            b.HasKey(c => c.Id);
            b.Property(c => c.Name).IsRequired();
            b.Property(c => c.LoyaltyPoints).HasColumnType("decimal(18,4)");
            b.Property(c => c.StoreCredit).HasColumnType("decimal(18,4)");
            b.Property(c => c.LoyaltyRate).HasColumnType("decimal(8,4)");
        });

        // User
        modelBuilder.Entity<User>(b =>
        {
            b.HasKey(u => u.Id);
            b.Property(u => u.Username).IsRequired().UseCollation("NOCASE");
            b.HasIndex(u => u.Username).IsUnique();
        });

        // Sale
        modelBuilder.Entity<Sale>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.Subtotal).HasColumnType("decimal(18,4)");
            b.Property(s => s.DiscountTotal).HasColumnType("decimal(18,4)");
            b.Property(s => s.TaxTotal).HasColumnType("decimal(18,4)");
            b.Property(s => s.Rounding).HasColumnType("decimal(18,4)");
            b.Property(s => s.AmountPaid).HasColumnType("decimal(18,4)");
            b.Property(s => s.Change).HasColumnType("decimal(18,4)");
            b.HasOne(s => s.Customer)
                .WithMany(c => c.Sales)
                .HasForeignKey(s => s.CustomerId)
                .OnDelete(DeleteBehavior.SetNull);
            b.HasOne(s => s.User)
                .WithMany(u => u.Sales)
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne(s => s.CashSession)
                .WithMany(session => session.Sales)
                .HasForeignKey(s => s.CashSessionId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(s => s.ReceiptNumber);
            b.HasIndex(s => s.SaleDate);
            b.HasIndex(s => s.CashSessionId);
            b.HasIndex(s => s.RefundedSaleId);
        });

        // SaleItem
        modelBuilder.Entity<SaleItem>(b =>
        {
            b.HasKey(i => i.Id);
            b.Property(i => i.UnitPrice).HasColumnType("decimal(18,4)");
            b.Property(i => i.CostPrice).HasColumnType("decimal(18,4)");
            b.Property(i => i.TaxRate).HasColumnType("decimal(6,3)");
            b.Property(i => i.DiscountAmount).HasColumnType("decimal(18,4)");
            b.Property(i => i.Quantity).HasColumnType("decimal(18,4)");
            b.Property(i => i.Unit).HasConversion<int>();
            b.HasOne(i => i.Sale)
                .WithMany(s => s.Items)
                .HasForeignKey(i => i.SaleId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(i => i.Product)
                .WithMany(p => p.SaleItems)
                .HasForeignKey(i => i.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(i => i.RefundedSaleItemId);
        });

        // SalePayment
        modelBuilder.Entity<SalePayment>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Amount).HasColumnType("decimal(18,4)");
            b.HasOne(p => p.Sale)
                .WithMany(s => s.Payments)
                .HasForeignKey(p => p.SaleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // StockTransaction
        modelBuilder.Entity<StockTransaction>(b =>
        {
            b.HasKey(t => t.Id);
            b.Property(t => t.Quantity).HasColumnType("decimal(18,4)");
            b.Property(t => t.BalanceAfter).HasColumnType("decimal(18,4)");
            b.Property(t => t.UnitCost).HasColumnType("decimal(18,4)");
            b.HasOne(t => t.Product)
                .WithMany(p => p.StockTransactions)
                .HasForeignKey(t => t.ProductId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(t => t.CreatedAt);
            b.HasIndex(t => t.PurchaseDocumentId);
            b.HasIndex(t => t.PurchaseItemId).IsUnique();
            b.HasOne(t => t.PurchaseDocument)
                .WithMany()
                .HasForeignKey(t => t.PurchaseDocumentId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne(t => t.PurchaseItem)
                .WithMany()
                .HasForeignKey(t => t.PurchaseItemId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>()
                .WithMany()
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Tax
        modelBuilder.Entity<Tax>(b =>
        {
            b.HasKey(t => t.Id);
            b.Property(t => t.Rate).HasColumnType("decimal(6,3)");
        });

        // Discount
        modelBuilder.Entity<Discount>(b =>
        {
            b.HasKey(d => d.Id);
            b.Property(d => d.Value).HasColumnType("decimal(18,4)");
            b.Property(d => d.Code).UseCollation("NOCASE");
            b.HasIndex(d => d.Code);
        });

        // Setting
        modelBuilder.Entity<Setting>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.Key).IsRequired();
            b.HasIndex(s => s.Key);
        });

        modelBuilder.Entity<Supplier>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.Name).IsRequired();
            b.HasIndex(s => s.Name);
        });

        modelBuilder.Entity<PurchaseDocument>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.DocumentNumber).IsRequired();
            b.Property(p => p.Subtotal).HasColumnType("decimal(18,4)");
            b.Property(p => p.TaxTotal).HasColumnType("decimal(18,4)");
            b.Property(p => p.Total).HasColumnType("decimal(18,4)");
            b.HasIndex(p => p.DocumentNumber);
            b.HasIndex(p => p.DocumentDate);
            b.HasOne(p => p.Supplier)
                .WithMany(s => s.Purchases)
                .HasForeignKey(p => p.SupplierId)
                .OnDelete(DeleteBehavior.SetNull);
            b.HasOne<User>()
                .WithMany()
                .HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PurchaseItem>(b =>
        {
            b.HasKey(i => i.Id);
            b.Property(i => i.Quantity).HasColumnType("decimal(18,4)");
            b.Property(i => i.UnitCost).HasColumnType("decimal(18,4)");
            b.Property(i => i.TaxRate).HasColumnType("decimal(6,3)");
            b.HasOne(i => i.PurchaseDocument)
                .WithMany(p => p.Items)
                .HasForeignKey(i => i.PurchaseDocumentId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(i => i.Product)
                .WithMany()
                .HasForeignKey(i => i.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CashSession>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.OpeningFloat).HasColumnType("decimal(18,4)");
            b.Property(s => s.ExpectedCash).HasColumnType("decimal(18,4)");
            b.Property(s => s.CountedCash).HasColumnType("decimal(18,4)");
            b.Property(s => s.Variance).HasColumnType("decimal(18,4)");
            b.HasIndex(s => s.OpenedAt);
            b.HasOne<User>()
                .WithMany()
                .HasForeignKey(s => s.OpenedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>()
                .WithMany()
                .HasForeignKey(s => s.ClosedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CashMovement>(b =>
        {
            b.HasKey(m => m.Id);
            b.Property(m => m.Amount).HasColumnType("decimal(18,4)");
            b.HasIndex(m => m.CreatedAt);
            b.HasOne(m => m.CashSession)
                .WithMany(s => s.Movements)
                .HasForeignKey(m => m.CashSessionId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<User>()
                .WithMany()
                .HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Expense>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.Amount).HasColumnType("decimal(18,4)");
            b.HasIndex(e => e.ExpenseDate);
            b.HasOne<User>()
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CloudAccountState>(b =>
        {
            b.HasKey(value => value.Id);
            b.ToTable(table => table.HasCheckConstraint("CK_CloudAccountState_SingleRow", "Id = 1"));
        });

        modelBuilder.Entity<SyncIdentity>(b =>
        {
            b.HasKey(value => value.Id);
            b.HasIndex(value => new { value.EntityType, value.LocalId }).IsUnique();
            b.HasIndex(value => value.RecordId).IsUnique();
            b.HasIndex(value => new { value.TenantId, value.StoreId, value.EntityType });
        });

        modelBuilder.Entity<SyncOutboxOperation>(b =>
        {
            b.HasKey(value => value.Id);
            b.HasIndex(value => value.OperationId).IsUnique();
            b.HasIndex(value => value.IdempotencyKey).IsUnique();
            b.HasIndex(value => new { value.CreatedByUserId, value.Status, value.NextAttemptAtUtc, value.Id });
            b.Property(value => value.PayloadJson).IsRequired();
        });

        modelBuilder.Entity<SyncCursorState>(b =>
        {
            b.HasKey(value => value.Id);
            b.HasIndex(value => new { value.TenantId, value.StoreId, value.DeviceId }).IsUnique();
        });

        modelBuilder.Entity<SyncConflict>(b =>
        {
            b.HasKey(value => value.Id);
            b.HasIndex(value => value.ConflictId).IsUnique();
            b.HasIndex(value => new { value.Status, value.DetectedAtUtc });
        });

        modelBuilder.Entity<CloudCachedStore>(b =>
        {
            b.HasKey(value => value.Id);
            b.HasIndex(value => value.CloudStoreId).IsUnique();
        });

        modelBuilder.Entity<CloudCachedDeviceSession>(b =>
        {
            b.HasKey(value => value.Id);
            b.HasIndex(value => value.CloudSessionId).IsUnique();
        });

        // Existing operational rows retain their integer keys. Store ownership
        // lives in SyncIdentities, and these parameterized filters keep normal
        // services/reports scoped to the selected branch. Legacy rows without an
        // identity remain visible until the administrator completes migration.
        modelBuilder.Entity<Category>().HasQueryFilter(value => BypassStoreFilter || !CloudStoreFilterEnabled ||
            !SyncIdentities.Any(identity => identity.EntityType == "categories" && identity.LocalId == value.Id) ||
            SyncIdentities.Any(identity => identity.EntityType == "categories" && identity.LocalId == value.Id &&
                                               identity.StoreId == CurrentCloudStoreId && identity.DeletedAtUtc == null));
        modelBuilder.Entity<Tax>().HasQueryFilter(value => BypassStoreFilter || !CloudStoreFilterEnabled ||
            !SyncIdentities.Any(identity => identity.EntityType == "taxes" && identity.LocalId == value.Id) ||
            SyncIdentities.Any(identity => identity.EntityType == "taxes" && identity.LocalId == value.Id &&
                                               identity.StoreId == CurrentCloudStoreId && identity.DeletedAtUtc == null));
        modelBuilder.Entity<Discount>().HasQueryFilter(value => BypassStoreFilter || !CloudStoreFilterEnabled ||
            !SyncIdentities.Any(identity => identity.EntityType == "discounts" && identity.LocalId == value.Id) ||
            SyncIdentities.Any(identity => identity.EntityType == "discounts" && identity.LocalId == value.Id &&
                                               identity.StoreId == CurrentCloudStoreId && identity.DeletedAtUtc == null));
        modelBuilder.Entity<Customer>().HasQueryFilter(value => BypassStoreFilter || !CloudStoreFilterEnabled ||
            !SyncIdentities.Any(identity => identity.EntityType == "customers" && identity.LocalId == value.Id) ||
            SyncIdentities.Any(identity => identity.EntityType == "customers" && identity.LocalId == value.Id &&
                                               identity.StoreId == CurrentCloudStoreId && identity.DeletedAtUtc == null));
        modelBuilder.Entity<Supplier>().HasQueryFilter(value => BypassStoreFilter || !CloudStoreFilterEnabled ||
            !SyncIdentities.Any(identity => identity.EntityType == "suppliers" && identity.LocalId == value.Id) ||
            SyncIdentities.Any(identity => identity.EntityType == "suppliers" && identity.LocalId == value.Id &&
                                               identity.StoreId == CurrentCloudStoreId && identity.DeletedAtUtc == null));
        modelBuilder.Entity<Product>().HasQueryFilter(value => BypassStoreFilter || !CloudStoreFilterEnabled ||
            !SyncIdentities.Any(identity => identity.EntityType == "products" && identity.LocalId == value.Id) ||
            SyncIdentities.Any(identity => identity.EntityType == "products" && identity.LocalId == value.Id &&
                                               identity.StoreId == CurrentCloudStoreId && identity.DeletedAtUtc == null));
        modelBuilder.Entity<Setting>().HasQueryFilter(value => BypassStoreFilter || !CloudStoreFilterEnabled ||
            !SyncIdentities.Any(identity => identity.EntityType == "settings" && identity.LocalId == value.Id) ||
            SyncIdentities.Any(identity => identity.EntityType == "settings" && identity.LocalId == value.Id &&
                                               identity.StoreId == CurrentCloudStoreId && identity.DeletedAtUtc == null));
        modelBuilder.Entity<Sale>().HasQueryFilter(value => BypassStoreFilter || !CloudStoreFilterEnabled ||
            !SyncIdentities.Any(identity => identity.EntityType == "sales" && identity.LocalId == value.Id) ||
            SyncIdentities.Any(identity => identity.EntityType == "sales" && identity.LocalId == value.Id &&
                                               identity.StoreId == CurrentCloudStoreId && identity.DeletedAtUtc == null));
        modelBuilder.Entity<SaleItem>().HasQueryFilter(value => BypassStoreFilter || !CloudStoreFilterEnabled ||
            !SyncIdentities.Any(identity => identity.EntityType == "sale_items" && identity.LocalId == value.Id) ||
            SyncIdentities.Any(identity => identity.EntityType == "sale_items" && identity.LocalId == value.Id &&
                                               identity.StoreId == CurrentCloudStoreId && identity.DeletedAtUtc == null));
        modelBuilder.Entity<SalePayment>().HasQueryFilter(value => BypassStoreFilter || !CloudStoreFilterEnabled ||
            !SyncIdentities.Any(identity => identity.EntityType == "payments" && identity.LocalId == value.Id) ||
            SyncIdentities.Any(identity => identity.EntityType == "payments" && identity.LocalId == value.Id &&
                                               identity.StoreId == CurrentCloudStoreId && identity.DeletedAtUtc == null));
        modelBuilder.Entity<PurchaseDocument>().HasQueryFilter(value => BypassStoreFilter || !CloudStoreFilterEnabled ||
            !SyncIdentities.Any(identity => identity.EntityType == "purchases" && identity.LocalId == value.Id) ||
            SyncIdentities.Any(identity => identity.EntityType == "purchases" && identity.LocalId == value.Id &&
                                               identity.StoreId == CurrentCloudStoreId && identity.DeletedAtUtc == null));
        modelBuilder.Entity<PurchaseItem>().HasQueryFilter(value => BypassStoreFilter || !CloudStoreFilterEnabled ||
            !SyncIdentities.Any(identity => identity.EntityType == "purchase_items" && identity.LocalId == value.Id) ||
            SyncIdentities.Any(identity => identity.EntityType == "purchase_items" && identity.LocalId == value.Id &&
                                               identity.StoreId == CurrentCloudStoreId && identity.DeletedAtUtc == null));
        modelBuilder.Entity<StockTransaction>().HasQueryFilter(value => BypassStoreFilter || !CloudStoreFilterEnabled ||
            !SyncIdentities.Any(identity => identity.EntityType == "inventory_movements" && identity.LocalId == value.Id) ||
            SyncIdentities.Any(identity => identity.EntityType == "inventory_movements" && identity.LocalId == value.Id &&
                                               identity.StoreId == CurrentCloudStoreId && identity.DeletedAtUtc == null));
        modelBuilder.Entity<CashSession>().HasQueryFilter(value => BypassStoreFilter || !CloudStoreFilterEnabled ||
            !SyncIdentities.Any(identity => identity.EntityType == "register_sessions" && identity.LocalId == value.Id) ||
            SyncIdentities.Any(identity => identity.EntityType == "register_sessions" && identity.LocalId == value.Id &&
                                               identity.StoreId == CurrentCloudStoreId && identity.DeletedAtUtc == null));
        modelBuilder.Entity<CashMovement>().HasQueryFilter(value => BypassStoreFilter || !CloudStoreFilterEnabled ||
            !SyncIdentities.Any(identity => identity.EntityType == "cash_movements" && identity.LocalId == value.Id) ||
            SyncIdentities.Any(identity => identity.EntityType == "cash_movements" && identity.LocalId == value.Id &&
                                               identity.StoreId == CurrentCloudStoreId && identity.DeletedAtUtc == null));
        modelBuilder.Entity<Expense>().HasQueryFilter(value => BypassStoreFilter || !CloudStoreFilterEnabled ||
            !SyncIdentities.Any(identity => identity.EntityType == "expenses" && identity.LocalId == value.Id) ||
            SyncIdentities.Any(identity => identity.EntityType == "expenses" && identity.LocalId == value.Id &&
                                               identity.StoreId == CurrentCloudStoreId && identity.DeletedAtUtc == null));
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
        => SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => SaveChangesAsync(true, cancellationToken);

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        var capture = SyncCaptureContext.Current;
        var changes = !SuppressSyncCapture && capture.Enabled
            ? LocalSyncOutboxCapture.Snapshot(this)
            : Array.Empty<TrackedSyncChange>();

        if (changes.Count == 0)
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        IDbContextTransaction? ownedTransaction = null;
        try
        {
            if (Database.CurrentTransaction == null)
                ownedTransaction = await Database.BeginTransactionAsync(cancellationToken);

            // Keep the business entries unaccepted until their outbox rows are
            // durable. A short-lived context shares this exact connection and
            // transaction for sync metadata; this avoids writing the business
            // entries twice and means a serialization failure leaves the caller's
            // tracked states retryable after the database rollback.
            var affected = await base.SaveChangesAsync(false, cancellationToken);
            var currentTransaction = Database.CurrentTransaction
                                     ?? throw new InvalidOperationException("The local sync transaction is unavailable.");
            var metadataOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(Database.GetDbConnection())
                .Options;
            await using (var metadataDb = new AppDbContext(metadataOptions)
                         {
                             SuppressSyncCapture = true,
                             BypassStoreFilter = true
                         })
            {
                await metadataDb.Database.UseTransactionAsync(
                    currentTransaction.GetDbTransaction(), cancellationToken);
                await LocalSyncOutboxCapture.CaptureAsync(
                    metadataDb, changes, capture, cancellationToken);
                await metadataDb.SaveChangesAsync(true, cancellationToken);
            }

            if (ownedTransaction != null)
            {
                await ownedTransaction.CommitAsync(cancellationToken);
                SyncCaptureContext.NotifyOutboxChanged();
            }

            if (acceptAllChangesOnSuccess)
                ChangeTracker.AcceptAllChanges();

            return affected;
        }
        catch
        {
            if (ownedTransaction != null)
                await ownedTransaction.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            if (ownedTransaction != null)
                await ownedTransaction.DisposeAsync();
        }
    }
}
