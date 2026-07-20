using Microsoft.EntityFrameworkCore;

namespace PosApp.Data;

/// <summary>Creates short-lived contexts for background sync without retaining WPF view scopes.</summary>
public sealed class PosAppDbContextFactory : IDbContextFactory<AppDbContext>
{
    private readonly DbContextOptions<AppDbContext> _options;

    public PosAppDbContextFactory(string connectionString)
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public AppDbContext CreateDbContext() => new(_options);

    public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}
