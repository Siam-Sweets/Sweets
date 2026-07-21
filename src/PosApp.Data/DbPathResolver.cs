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
    // The active ID is intentionally fixed for this process. Switching writes
    // the registry and restarts PosApp so an existing DbContext can never begin
    // using another organization's database halfway through a transaction.
    private static readonly Lazy<string> RunningProfileId = new(() =>
        new LocalOrganizationProfileStore(AppFolder()).GetActiveProfile().Id);

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
        return new LocalOrganizationProfileStore(AppFolder())
            .GetDatabasePath(CurrentProfileId());
    }

    public static string CurrentProfileId() => RunningProfileId.Value;

    public static string CurrentProfileFolder()
        => new LocalOrganizationProfileStore(AppFolder()).GetProfileFolder(CurrentProfileId());

    public static string CloudTokenPath()
        => new LocalOrganizationProfileStore(AppFolder()).GetTokenPath(CurrentProfileId());

    public static string BackupFolder()
    {
        var folder = Path.Combine(CurrentProfileFolder(), "Backups");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static string UpdateFolder()
    {
        var folder = Path.Combine(AppFolder(), "Updates");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static string ProfileUpdateStateFolder()
    {
        var folder = Path.Combine(CurrentProfileFolder(), "Updates");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static string UpdateBackupFolder()
    {
        var folder = Path.Combine(BackupFolder(), "Updates");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static string PendingUpdateRecordPath() => Path.Combine(ProfileUpdateStateFolder(), "update-pending.json");

    public static string LastUpdateRecordPath() => Path.Combine(ProfileUpdateStateFolder(), "update-last.json");

    public static string LastSuccessfulVersionPath()
        => Path.Combine(ProfileUpdateStateFolder(), "last-successful-version.txt");

    public static string PendingRestorePath() => Path.Combine(CurrentProfileFolder(), "posapp.restore-pending.db");

    /// <summary>
    /// Applies a previously validated restore before EF opens the live database.
    /// The outgoing database is retained as a timestamped safety copy.
    /// </summary>
    public static string? ApplyPendingRestore()
        => DatabaseRestoreCoordinator.ApplyPendingRestore(
            PendingRestorePath(),
            DefaultPath(),
            BackupFolder());

    /// <summary>
    /// Releases native Microsoft.Data.Sqlite pooled connections after all EF
    /// contexts have been disposed during application shutdown.
    /// </summary>
    public static void ClearSqliteConnectionPools() => SqliteConnection.ClearAllPools();

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
