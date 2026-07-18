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

            using var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DbPathResolver.DefaultPath(),
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
            using var target = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = destination,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
            source.Open();
            target.Open();
            source.BackupDatabase(target);

            if (isAutomatic)
                PruneAutomaticBackups(Math.Clamp(retentionCount ?? 20, 1, 200));
            return destination;
        });
    }

    public async Task ValidateBackupAsync(string backupPath)
    {
        await Task.Run(() => DatabaseFileValidator.ValidatePosAppDatabase(backupPath));
    }

    public async Task StageRestoreAsync(string backupPath)
    {
        await ValidateBackupAsync(backupPath);

        var pending = DbPathResolver.PendingRestorePath();
        var temporary = pending + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await Task.Run(() =>
            {
                DatabaseFileValidator.CopyDurably(backupPath, temporary);
                DatabaseFileValidator.ValidatePosAppDatabase(temporary);

                // The temporary file is in the same directory as the pending file,
                // allowing Windows to publish the fully flushed copy atomically.
                if (File.Exists(pending))
                    File.Replace(temporary, pending, destinationBackupFileName: null, ignoreMetadataErrors: true);
                else
                    File.Move(temporary, pending);
            });
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
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
