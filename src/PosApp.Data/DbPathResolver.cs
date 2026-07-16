using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PosApp.Data;

namespace PosApp.Data;

/// <summary>
/// Resolves the local application folder and SQLite database path under
/// %LOCALAPPDATA%/PosApp for both installed and portable executables.
/// </summary>
public static class DbPathResolver
{
    public static string AppFolder()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PosApp");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static string DefaultPath()
    {
        return Path.Combine(AppFolder(), "posapp.db");
    }

    public static string BackupFolder()
    {
        var folder = Path.Combine(AppFolder(), "Backups");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static string PendingRestorePath() => Path.Combine(AppFolder(), "posapp.restore-pending.db");

    /// <summary>
    /// Applies a previously validated restore before EF opens the live database.
    /// The outgoing database is retained as a timestamped safety copy.
    /// </summary>
    public static string? ApplyPendingRestore()
    {
        var pending = PendingRestorePath();
        if (!File.Exists(pending)) return null;

        var live = DefaultPath();
        string? safetyCopy = null;
        if (File.Exists(live))
        {
            safetyCopy = Path.Combine(
                BackupFolder(),
                $"posapp-before-restore-{DateTime.Now:yyyyMMdd-HHmmss-fff}.db");
            using var source = new SqliteConnection(ConnectionString(live));
            using var target = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = safetyCopy,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString());
            source.Open();
            target.Open();
            source.BackupDatabase(target);
        }

        // These sidecars belong to the outgoing database and must not be paired
        // with the restored file. No connection exists yet at this point.
        File.Delete(live + "-wal");
        File.Delete(live + "-shm");
        File.Copy(pending, live, overwrite: true);
        File.Delete(pending);
        return safetyCopy;
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
