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
    {
        var pending = PendingRestorePath();
        if (!File.Exists(pending)) return null;

        var live = DefaultPath();
        var prepared = live + $".restore-{Guid.NewGuid():N}.tmp";
        string? safetyCopy = null;
        try
        {
            // Never touch the live store until a durable copy of the staged file has
            // passed SQLite integrity and PosApp schema checks.
            DatabaseFileValidator.ValidatePosAppDatabase(pending);
            DatabaseFileValidator.CopyDurably(pending, prepared);
            DatabaseFileValidator.ValidatePosAppDatabase(prepared);

            if (File.Exists(live))
            {
                safetyCopy = Path.Combine(
                    BackupFolder(),
                    $"posapp-before-restore-{DateTime.Now:yyyyMMdd-HHmmss-fff}.db");
                {
                    // Keep the backup connections inside their own scope. Windows
                    // does not allow the live database to be replaced while either
                    // connection still owns a handle to it.
                    using var source = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = live,
                        Mode = SqliteOpenMode.ReadWrite,
                        Cache = SqliteCacheMode.Private,
                        Pooling = false
                    }.ToString());
                    using var target = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = safetyCopy,
                        Mode = SqliteOpenMode.ReadWriteCreate,
                        Cache = SqliteCacheMode.Private,
                        Pooling = false
                    }.ToString());
                    source.Open();
                    using (var checkpoint = source.CreateCommand())
                    {
                        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                        checkpoint.ExecuteNonQuery();
                    }
                    target.Open();
                    source.BackupDatabase(target);
                }
                DatabaseFileValidator.ValidatePosAppDatabase(safetyCopy);

                // Sidecars now contain no uncheckpointed data and belong to the old
                // database identity. Replace the main file atomically afterwards.
                File.Delete(live + "-wal");
                File.Delete(live + "-shm");
                File.Replace(prepared, live, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(prepared, live);
            }

            File.Delete(pending);
            return safetyCopy;
        }
        finally
        {
            if (File.Exists(prepared)) File.Delete(prepared);
        }
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
