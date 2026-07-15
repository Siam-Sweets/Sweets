using Microsoft.EntityFrameworkCore;
using PosApp.Core.Entities;

namespace PosApp.Data;

/// <summary>
/// EF Core DbContext for the local SQLite database. The DB file lives
/// next to the exe (AppData on portable deployments).
/// </summary>
public class AppDbContext : DbContext
{
    private readonly string _connectionString;

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
            b.Property(p => p.Price).HasColumnType("decimal(18,4)");
            b.Property(p => p.CostPrice).HasColumnType("decimal(18,4)");
            b.Property(p => p.TaxRate).HasColumnType("decimal(6,3)");
            b.Property(p => p.StockQuantity).HasColumnType("decimal(18,4)");
            b.Property(p => p.LowStockThreshold).HasColumnType("decimal(18,4)");
            b.HasIndex(p => p.Sku).IsUnique();
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
            b.HasIndex(c => c.Name).IsUnique();
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
            b.Property(u => u.Username).IsRequired();
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
            b.HasIndex(s => s.ReceiptNumber).IsUnique();
            b.HasIndex(s => s.SaleDate);
        });

        // SaleItem
        modelBuilder.Entity<SaleItem>(b =>
        {
            b.HasKey(i => i.Id);
            b.Property(i => i.UnitPrice).HasColumnType("decimal(18,4)");
            b.Property(i => i.TaxRate).HasColumnType("decimal(6,3)");
            b.Property(i => i.DiscountAmount).HasColumnType("decimal(18,4)");
            b.Property(i => i.Quantity).HasColumnType("decimal(18,4)");
            b.HasOne(i => i.Sale)
                .WithMany(s => s.Items)
                .HasForeignKey(i => i.SaleId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(i => i.Product)
                .WithMany(p => p.SaleItems)
                .HasForeignKey(i => i.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
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
        });

        // Setting
        modelBuilder.Entity<Setting>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.Key).IsRequired();
            b.HasIndex(s => s.Key).IsUnique();
        });
    }
}
