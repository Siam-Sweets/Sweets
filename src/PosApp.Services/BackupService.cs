using Microsoft.Data.Sqlite;
using PosApp.Core.Interfaces;
using PosApp.Data;

namespace PosApp.Services;

public class BackupService : IBackupService
{
    public string BackupFolder => DbPathResolver.BackupFolder();

    public Task<string> CreateBackupAsync(string? destinationPath = null, int? retentionCount = null)
    {
        return Task.Run(() =>
        {
            var isAutomatic = string.IsNullOrWhiteSpace(destinationPath);
            var destination = destinationPath;
            if (isAutomatic)
            {
                Directory.CreateDirectory(BackupFolder);
                destination = Path.Combine(
                    BackupFolder,
                    $"posapp-backup-{DateTime.Now:yyyyMMdd-HHmmss-fff}.db");
            }

            destination = Path.GetFullPath(destination!);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (string.Equals(destination, Path.GetFullPath(DbPathResolver.DefaultPath()),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose a path different from the live database.");

            using var source = new SqliteConnection(DbPathResolver.ConnectionString());
            using var target = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = destination,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString());
            source.Open();
            target.Open();
            source.BackupDatabase(target);

            if (isAutomatic)
                PruneAutomaticBackups(Math.Clamp(retentionCount ?? 20, 1, 200));
            return destination;
        });
    }

    public async Task StageRestoreAsync(string backupPath)
    {
        if (!File.Exists(backupPath))
            throw new FileNotFoundException("Backup file not found.", backupPath);

        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(backupPath),
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            var result = (await command.ExecuteScalarAsync())?.ToString();
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected file is not a healthy SQLite backup.");

            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' " +
                "AND name IN ('Users', 'Products', 'Sales', 'Settings');";
            var requiredTables = Convert.ToInt32(await command.ExecuteScalarAsync());
            if (requiredTables != 4)
                throw new InvalidOperationException("The selected file is not a PosApp database backup.");
        }

        File.Copy(backupPath, DbPathResolver.PendingRestorePath(), overwrite: true);
    }

    private void PruneAutomaticBackups(int keep)
    {
        var backups = new DirectoryInfo(BackupFolder)
            .GetFiles("posapp-backup-*.db")
            .OrderByDescending(file => file.CreationTimeUtc)
            .ToList();
        foreach (var oldBackup in backups.Skip(keep))
            oldBackup.Delete();
    }
}
