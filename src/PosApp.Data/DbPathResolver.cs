using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PosApp.Data;

namespace PosApp.Data;

/// <summary>
/// Resolves where the SQLite database file lives. On a real install this
/// is %APPDATA%/PosApp/posapp.db; on a portable exe it sits next to the exe.
/// </summary>
public static class DbPathResolver
{
    public static string DefaultPath()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PosApp");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "posapp.db");
    }

    public static string ConnectionString(string? path = null)
    {
        var file = path ?? DefaultPath();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        return builder.ToString();
    }

    public static DbContextOptions<AppDbContext> BuildOptions(string? path = null)
    {
        return new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(ConnectionString(path))
            .Options;
    }
}
