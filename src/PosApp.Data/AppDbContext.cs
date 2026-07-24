using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Utilities;

namespace PosApp.Data;

/// <summary>
/// EF Core context for the local offline database. Store-owned rows are
/// filtered automatically by the currently selected store.
/// </summary>
public class AppDbContext : DbContext
{
    public static event EventHandler? CloudOutboxChanged;
    private readonly string _connectionString;
    private readonly IStoreContext _storeContext;

    public AppDbContext(string connectionString)
        : base()
    {
        _connectionString = connectionString;
        _storeContext = new FixedStoreContext();
    }

    public AppDbContext(DbContextOptions<AppDbContext> options, IStoreContext storeContext)
        : base(options)
    {
        _connectionString = string.Empty;
        _storeContext = storeContext;
    }

    public int CurrentStoreId => Math.Max(1, _storeContext.StoreId);

    public DbSet<Store> Stores => Set<Store>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleItem> SaleItems => Set<SaleItem>();
    public DbSet<SalePayment> SalePayments => Set<SalePayment>();
    public DbSet<StockTransaction> StockTransactions => Set<StockTransaction>();
    public DbSet<StockTransfer> StockTransfers => Set<StockTransfer>();
    public DbSet<StockTransferItem> StockTransferItems => Set<StockTransferItem>();
    public DbSet<Tax> Taxes => Set<Tax>();
    public DbSet<Discount> Discounts => Set<Discount>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<PurchaseDocument> PurchaseDocuments => Set<PurchaseDocument>();
    public DbSet<PurchaseItem> PurchaseItems => Set<PurchaseItem>();
    public DbSet<CashSession> CashSessions => Set<CashSession>();
    public DbSet<CashMovement> CashMovements => Set<CashMovement>();
    public DbSet<SyncOutboxItem> SyncOutbox => Set<SyncOutboxItem>();
    public DbSet<SyncState> SyncStates => Set<SyncState>();
    public DbSet<SyncConflict> SyncConflicts => Set<SyncConflict>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured && !string.IsNullOrEmpty(_connectionString))
            optionsBuilder.UseSqlite(_connectionString);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyConcurrencyTokensAndValidateImmutableRows();
        var changes = PrepareStoreChanges();
        if (changes.Count == 0)
            return base.SaveChanges(acceptAllChangesOnSuccess);

        var ownsTransaction = Database.CurrentTransaction == null;
        using var transaction = ownsTransaction ? Database.BeginTransaction() : null;
        var originalEntries = ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        try
        {
            var affected = base.SaveChanges(false);
            AddOutboxRows(changes);
            MarkFirstPhaseEntriesSaved(originalEntries);
            base.SaveChanges(false);
            if (ownsTransaction) transaction!.Commit();
            ChangeTracker.AcceptAllChanges();
            if (ownsTransaction) RaiseCloudOutboxChanged();
            return affected;
        }
        catch
        {
            if (ownsTransaction && transaction != null) transaction.Rollback();
            ChangeTracker.Clear();
            throw;
        }
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => SaveChangesAsync(true, cancellationToken);

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ApplyConcurrencyTokensAndValidateImmutableRows();
        var changes = PrepareStoreChanges();
        if (changes.Count == 0)
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        var ownsTransaction = Database.CurrentTransaction == null;
        await using var transaction = ownsTransaction
            ? await Database.BeginTransactionAsync(cancellationToken)
            : null;
        var originalEntries = ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        try
        {
            var affected = await base.SaveChangesAsync(false, cancellationToken);
            AddOutboxRows(changes);
            MarkFirstPhaseEntriesSaved(originalEntries);
            await base.SaveChangesAsync(false, cancellationToken);
            if (ownsTransaction) await transaction!.CommitAsync(cancellationToken);
            ChangeTracker.AcceptAllChanges();
            if (ownsTransaction) RaiseCloudOutboxChanged();
            return affected;
        }
        catch
        {
            if (ownsTransaction && transaction != null)
                await transaction.RollbackAsync(cancellationToken);
            ChangeTracker.Clear();
            throw;
        }
    }

    public async Task CommitExternalTransactionAsync(
        IDbContextTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await transaction.CommitAsync(cancellationToken);
        ChangeTracker.AcceptAllChanges();
        RaiseCloudOutboxChanged();
    }

    public async Task RollbackExternalTransactionAsync(
        IDbContextTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        try { await transaction.RollbackAsync(cancellationToken); }
        finally { ChangeTracker.Clear(); }
    }

    private static void MarkFirstPhaseEntriesSaved(IEnumerable<EntityEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.State == EntityState.Deleted)
                entry.State = EntityState.Detached;
            else if (entry.State is EntityState.Added or EntityState.Modified)
                entry.State = EntityState.Unchanged;
        }
    }

    private void ApplyConcurrencyTokensAndValidateImmutableRows()
    {
        foreach (var entry in ChangeTracker.Entries<Product>())
        {
            if (entry.State == EntityState.Modified &&
                entry.Property(x => x.StockQuantity).IsModified &&
                !_storeContext.IsCloudCaptureSuppressed)
            {
                entry.Entity.StockVersion = Math.Max(
                    entry.Entity.StockVersion,
                    Convert.ToInt64(entry.Property(x => x.StockVersion).OriginalValue)) + 1;
                entry.Property(x => x.StockVersion).IsModified = true;
            }
        }

        foreach (var entry in ChangeTracker.Entries<Discount>())
        {
            if (entry.State == EntityState.Modified &&
                entry.Property(x => x.UsedCount).IsModified &&
                !_storeContext.IsCloudCaptureSuppressed)
            {
                entry.Entity.UsageVersion = Math.Max(
                    entry.Entity.UsageVersion,
                    Convert.ToInt64(entry.Property(x => x.UsageVersion).OriginalValue)) + 1;
                entry.Property(x => x.UsageVersion).IsModified = true;
            }
        }

        foreach (var entry in ChangeTracker.Entries<StockTransaction>())
        {
            if (entry.State == EntityState.Deleted)
                throw new InvalidOperationException("Stock ledger rows are append-only and cannot be deleted.");
            if (entry.State != EntityState.Modified) continue;
            var illegal = entry.Properties.Any(property => property.IsModified &&
                property.Metadata.Name is not nameof(StoreScopedEntity.CloudVersion)
                    and not nameof(StoreScopedEntity.SyncVersion)
                    and not nameof(StoreScopedEntity.SyncUpdatedAt));
            if (illegal)
                throw new InvalidOperationException("Stock ledger rows are append-only and cannot be edited.");
        }
    }

    private static void RaiseCloudOutboxChanged()
    {
        try { CloudOutboxChanged?.Invoke(null, EventArgs.Empty); }
        catch { /* local commits must never fail because a background trigger failed */ }
    }

    private void AddOutboxRows(IEnumerable<PendingSyncChange> changes)
    {
        var operationId = SyncOperationScope.CurrentOperationId ?? Guid.NewGuid().ToString("N");
        foreach (var change in changes)
        {
            SyncOutbox.Add(new SyncOutboxItem
            {
                StoreId = change.Entity is Store store ? store.Id : change.StoreId,
                EntityType = change.EntityType,
                EntitySyncId = change.EntitySyncId,
                Operation = change.State == EntityState.Deleted ? "delete" : "upsert",
                EntityVersion = change.EntityVersion,
                BaseCloudVersion = change.BaseCloudVersion,
                OperationId = operationId,
                PayloadJson = change.State == EntityState.Deleted
                    ? "{}"
                    : SyncPayloadSerializer.SerializeForSync(change.Entity, this)
            });
        }
    }

    private List<PendingSyncChange> PrepareStoreChanges()
    {
        if (_storeContext.IsCloudCaptureSuppressed) return new List<PendingSyncChange>();

        var now = DateTime.UtcNow;
        var result = new List<PendingSyncChange>();
        foreach (var entry in ChangeTracker.Entries()
                     .Where(candidate => candidate.Entity is StoreScopedEntity or Store))
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            if (entry.Entity is StoreScopedEntity entity)
            {
                var baseCloudVersion = entity.CloudVersion;
                if (entry.State == EntityState.Added)
                {
                    if (entity.StoreId <= 0) entity.StoreId = CurrentStoreId;
                    if (string.IsNullOrWhiteSpace(entity.SyncId)) entity.SyncId = Guid.NewGuid().ToString("N");
                    entity.SyncVersion = Math.Max(1, entity.SyncVersion);
                    entity.SyncUpdatedAt = now;
                }
                else
                {
                    var storeProperty = entry.Property(nameof(StoreScopedEntity.StoreId));
                    if (storeProperty.IsModified && !Equals(storeProperty.OriginalValue, storeProperty.CurrentValue))
                        throw new InvalidOperationException("A record cannot be moved between stores.");
                    entity.SyncVersion = Math.Max(1, entity.SyncVersion + 1);
                    entity.SyncUpdatedAt = now;
                }

                if (ShouldQueue(entity))
                {
                    result.Add(new PendingSyncChange(
                        entity, entry.State, entity.StoreId, entity.GetType().Name, entity.SyncId,
                        entity.SyncVersion, baseCloudVersion));
                }
                continue;
            }

            var store = (Store)entry.Entity;
            var storeBaseCloudVersion = store.CloudVersion;
            if (entry.State == EntityState.Added)
            {
                if (string.IsNullOrWhiteSpace(store.SyncId)) store.SyncId = Guid.NewGuid().ToString("N");
                store.SyncVersion = Math.Max(1, store.SyncVersion);
                store.SyncUpdatedAt = now;
            }
            else
            {
                store.SyncVersion = Math.Max(1, store.SyncVersion + 1);
                store.SyncUpdatedAt = now;
            }

            if (_storeContext.IsCloudSyncEnabled)
            {
                result.Add(new PendingSyncChange(
                    store, entry.State, store.Id, nameof(Store), store.SyncId,
                    store.SyncVersion, storeBaseCloudVersion));
            }
        }
        return result;
    }

    private bool ShouldQueue(StoreScopedEntity entity)
    {
        if (!_storeContext.IsCloudSyncEnabled) return false;
        if (entity is Setting setting && SettingSyncPolicy.IsDeviceLocal(setting.Key))
            return false;
        return true;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Store>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.Name).IsRequired();
            b.Property(s => s.Code).IsRequired().UseCollation("NOCASE");
            b.Property(s => s.SyncId).IsRequired().UseCollation("NOCASE");
            b.Property(s => s.SyncVersion).IsConcurrencyToken();
            b.Property(s => s.CloudVersion).HasDefaultValue(0L);
            b.HasIndex(s => s.Code).IsUnique();
            b.HasIndex(s => s.SyncId).IsUnique();
        });

        ConfigureStoreEntity<Product>(modelBuilder);
        ConfigureStoreEntity<Category>(modelBuilder);
        ConfigureStoreEntity<Customer>(modelBuilder);
        ConfigureStoreEntity<User>(modelBuilder);
        ConfigureStoreEntity<Sale>(modelBuilder);
        ConfigureStoreEntity<SaleItem>(modelBuilder);
        ConfigureStoreEntity<SalePayment>(modelBuilder);
        ConfigureStoreEntity<StockTransaction>(modelBuilder);
        ConfigureStoreEntity<StockTransfer>(modelBuilder);
        ConfigureStoreEntity<StockTransferItem>(modelBuilder);
        ConfigureStoreEntity<Tax>(modelBuilder);
        ConfigureStoreEntity<Discount>(modelBuilder);
        ConfigureStoreEntity<Setting>(modelBuilder);
        ConfigureStoreEntity<Supplier>(modelBuilder);
        ConfigureStoreEntity<PurchaseDocument>(modelBuilder);
        ConfigureStoreEntity<PurchaseItem>(modelBuilder);
        ConfigureStoreEntity<CashSession>(modelBuilder);
        ConfigureStoreEntity<CashMovement>(modelBuilder);

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
            b.Property(p => p.StockVersion).IsConcurrencyToken();
            b.Property(p => p.LowStockThreshold).HasColumnType("decimal(18,4)");
            b.HasIndex(p => new { p.StoreId, p.Sku }).IsUnique();
            b.HasIndex(p => new { p.StoreId, p.Barcode }).IsUnique();
            b.HasOne(p => p.Category).WithMany(c => c.Products)
                .HasForeignKey(p => p.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Category>(b =>
        {
            b.HasKey(c => c.Id);
            b.Property(c => c.Name).IsRequired().UseCollation("NOCASE");
            b.HasIndex(c => new { c.StoreId, c.Name }).IsUnique();
        });

        modelBuilder.Entity<Customer>(b =>
        {
            b.HasKey(c => c.Id);
            b.Property(c => c.Name).IsRequired();
            b.Property(c => c.LoyaltyPoints).HasColumnType("decimal(18,4)");
            b.Property(c => c.StoreCredit).HasColumnType("decimal(18,4)");
            b.Property(c => c.LoyaltyRate).HasColumnType("decimal(8,4)");
        });

        modelBuilder.Entity<User>(b =>
        {
            b.HasKey(u => u.Id);
            b.Property(u => u.Username).IsRequired().UseCollation("NOCASE");
            b.HasIndex(u => new { u.StoreId, u.Username }).IsUnique();
        });

        modelBuilder.Entity<Sale>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.Subtotal).HasColumnType("decimal(18,4)");
            b.Property(s => s.DiscountTotal).HasColumnType("decimal(18,4)");
            b.Property(s => s.TaxTotal).HasColumnType("decimal(18,4)");
            b.Property(s => s.Rounding).HasColumnType("decimal(18,4)");
            b.Property(s => s.AmountPaid).HasColumnType("decimal(18,4)");
            b.Property(s => s.Change).HasColumnType("decimal(18,4)");
            b.HasOne(s => s.Customer).WithMany(c => c.Sales)
                .HasForeignKey(s => s.CustomerId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne(s => s.User).WithMany(u => u.Sales)
                .HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(s => s.CashSession).WithMany(session => session.Sales)
                .HasForeignKey(s => s.CashSessionId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(s => new { s.StoreId, s.ReceiptNumber }).IsUnique();
            b.HasIndex(s => new { s.StoreId, s.OperationId }).IsUnique();
            b.HasOne<Sale>().WithMany().HasForeignKey(s => s.RefundedSaleId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(s => s.SaleDate);
            b.HasIndex(s => s.CashSessionId);
            b.HasIndex(s => s.RefundedSaleId);
        });

        modelBuilder.Entity<SaleItem>(b =>
        {
            b.HasKey(i => i.Id);
            b.Property(i => i.UnitPrice).HasColumnType("decimal(18,4)");
            b.Property(i => i.CostPrice).HasColumnType("decimal(18,4)");
            b.Property(i => i.TaxRate).HasColumnType("decimal(6,3)");
            b.Property(i => i.DiscountAmount).HasColumnType("decimal(18,4)");
            b.Property(i => i.Quantity).HasColumnType("decimal(18,4)");
            b.Property(i => i.Unit).HasConversion<int>();
            b.HasOne(i => i.Sale).WithMany(s => s.Items)
                .HasForeignKey(i => i.SaleId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(i => i.Product).WithMany(p => p.SaleItems)
                .HasForeignKey(i => i.ProductId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SaleItem>().WithMany().HasForeignKey(i => i.RefundedSaleItemId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(i => i.RefundedSaleItemId);
        });

        modelBuilder.Entity<SalePayment>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Amount).HasColumnType("decimal(18,4)");
            b.HasOne(p => p.Sale).WithMany(s => s.Payments)
                .HasForeignKey(p => p.SaleId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StockTransaction>(b =>
        {
            b.HasKey(t => t.Id);
            b.Property(t => t.Quantity).HasColumnType("decimal(18,4)");
            b.Property(t => t.BalanceAfter).HasColumnType("decimal(18,4)");
            b.Property(t => t.UnitCost).HasColumnType("decimal(18,4)");
            b.HasOne(t => t.Product).WithMany(p => p.StockTransactions)
                .HasForeignKey(t => t.ProductId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Sale>().WithMany().HasForeignKey(t => t.SaleId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SaleItem>().WithMany().HasForeignKey(t => t.SaleItemId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<StockTransfer>().WithMany().HasForeignKey(t => t.StockTransferId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<StockTransferItem>().WithMany().HasForeignKey(t => t.StockTransferItemId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(t => t.CreatedAt);
            b.HasIndex(t => t.StockTransferId);
            b.HasIndex(t => t.StockTransferItemId);
            b.HasIndex(t => new { t.StoreId, t.OperationKey }).IsUnique();
        });

        modelBuilder.Entity<StockTransfer>(b =>
        {
            b.HasKey(t => t.Id);
            b.Property(t => t.TransferNumber).IsRequired().UseCollation("NOCASE");
            b.HasIndex(t => new { t.StoreId, t.TransferNumber }).IsUnique();
            b.HasIndex(t => new { t.StoreId, t.OperationId }).IsUnique();
            b.HasIndex(t => new { t.DestinationStoreId, t.Status });
            b.HasOne<Store>().WithMany().HasForeignKey(t => t.DestinationStoreId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(t => t.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(t => t.DispatchedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(t => t.ReceivedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(t => t.CancelledByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StockTransferItem>(b =>
        {
            b.HasKey(i => i.Id);
            b.Property(i => i.Quantity).HasColumnType("decimal(18,4)");
            b.Property(i => i.UnitCost).HasColumnType("decimal(18,4)");
            b.Property(i => i.Unit).HasConversion<int>();
            b.HasOne(i => i.StockTransfer).WithMany(t => t.Items)
                .HasForeignKey(i => i.StockTransferId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Product>().WithMany().HasForeignKey(i => i.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(i => i.StockTransferId);
            b.HasOne<Product>().WithMany().HasForeignKey(i => i.DestinationProductId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(i => i.DestinationProductId);
        });

        modelBuilder.Entity<Tax>(b =>
        {
            b.HasKey(t => t.Id);
            b.Property(t => t.Rate).HasColumnType("decimal(6,3)");
        });

        modelBuilder.Entity<Discount>(b =>
        {
            b.HasKey(d => d.Id);
            b.Property(d => d.Value).HasColumnType("decimal(18,4)");
            b.Property(d => d.Code).UseCollation("NOCASE");
            b.Property(d => d.UsageVersion).IsConcurrencyToken();
            b.HasIndex(d => new { d.StoreId, d.Code }).IsUnique();
        });

        modelBuilder.Entity<Setting>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.Key).IsRequired();
            b.HasIndex(s => new { s.StoreId, s.Key }).IsUnique();
        });

        modelBuilder.Entity<Supplier>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.Name).IsRequired();
            b.HasIndex(s => new { s.StoreId, s.Name });
        });

        modelBuilder.Entity<PurchaseDocument>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.DocumentNumber).IsRequired();
            b.Property(p => p.Subtotal).HasColumnType("decimal(18,4)");
            b.Property(p => p.TaxTotal).HasColumnType("decimal(18,4)");
            b.Property(p => p.Total).HasColumnType("decimal(18,4)");
            b.HasIndex(p => new { p.StoreId, p.DocumentNumber }).IsUnique();
            b.HasIndex(p => new { p.StoreId, p.OperationId }).IsUnique();
            b.HasIndex(p => p.DocumentDate);
            b.HasOne(p => p.Supplier).WithMany(s => s.Purchases)
                .HasForeignKey(p => p.SupplierId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne<User>().WithMany().HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PurchaseItem>(b =>
        {
            b.HasKey(i => i.Id);
            b.Property(i => i.Quantity).HasColumnType("decimal(18,4)");
            b.Property(i => i.UnitCost).HasColumnType("decimal(18,4)");
            b.Property(i => i.TaxRate).HasColumnType("decimal(6,3)");
            b.HasOne(i => i.PurchaseDocument).WithMany(p => p.Items)
                .HasForeignKey(i => i.PurchaseDocumentId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(i => i.Product).WithMany().HasForeignKey(i => i.ProductId)
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
            b.HasOne<User>().WithMany().HasForeignKey(s => s.OpenedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(s => s.ClosedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CashMovement>(b =>
        {
            b.HasKey(m => m.Id);
            b.Property(m => m.Amount).HasColumnType("decimal(18,4)");
            b.HasIndex(m => m.CreatedAt);
            b.HasOne(m => m.CashSession).WithMany(s => s.Movements)
                .HasForeignKey(m => m.CashSessionId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<User>().WithMany().HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SyncOutboxItem>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.PayloadJson).IsRequired();
            b.HasIndex(x => x.ChangeId).IsUnique();
            b.HasIndex(x => new { x.StoreId, x.Id });
            b.HasIndex(x => new { x.OperationId, x.Id });
        });

        modelBuilder.Entity<SyncState>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.StoreId).IsUnique();
        });

        modelBuilder.Entity<SyncConflict>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.StoreId, x.ResolvedAt });
            b.HasIndex(x => x.ChangeId).IsUnique();
        });

        modelBuilder.Entity<SyncRun>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Status).IsRequired();
            b.HasIndex(x => x.StartedAt);
        });
    }

    private void ConfigureStoreEntity<TEntity>(ModelBuilder modelBuilder)
        where TEntity : StoreScopedEntity
    {
        modelBuilder.Entity<TEntity>(b =>
        {
            // StoreScopedEntity is a CLR-only metadata base, not an EF entity
            // hierarchy. Each concrete POS entity must keep its own existing table.
            b.HasBaseType((Type?)null);
            b.Property(x => x.SyncId).IsRequired().UseCollation("NOCASE");
            b.Property(x => x.SyncVersion).IsConcurrencyToken();
            b.Property(x => x.CloudVersion).HasDefaultValue(0L);
            b.HasIndex(x => new { x.StoreId, x.SyncId }).IsUnique();
            b.HasIndex(x => x.StoreId);
            b.HasQueryFilter(x => x.StoreId == CurrentStoreId);
        });
    }

    private sealed record PendingSyncChange(
        object Entity, EntityState State, int StoreId, string EntityType,
        string EntitySyncId, long EntityVersion, long BaseCloudVersion);

    private sealed class FixedStoreContext : IStoreContext
    {
        public int StoreId => 1;
        public string StoreSyncId => string.Empty;
        public bool IsCloudSyncEnabled => false;
        public bool IsCloudCaptureSuppressed => false;
        public IDisposable SuppressCloudCapture() => EmptyScope.Instance;
        public void SetCurrentStore(Store store) { }

        private sealed class EmptyScope : IDisposable
        {
            public static readonly EmptyScope Instance = new();
            public void Dispose() { }
        }
    }
}
