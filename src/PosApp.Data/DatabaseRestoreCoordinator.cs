using Microsoft.Data.Sqlite;

namespace PosApp.Data;

/// <summary>
/// Raised when a staged restore cannot obtain exclusive access to the active
/// SQLite database before its bounded startup wait expires.
/// </summary>
public sealed class DatabaseRestoreInUseException : IOException
{
    public DatabaseRestoreInUseException(string databasePath, Exception? innerException = null)
        : base(
            "The backup restore is still waiting because another PosApp process is using this " +
            "organization's database. Close every PosApp window (or end PosApp in Task Manager), " +
            "then start PosApp again. The staged backup was retained and the current database " +
            "was not replaced.",
            innerException)
    {
        DatabasePath = databasePath;
    }

    public string DatabasePath { get; }
}

/// <summary>
/// Applies a validated staged database as an all-or-nothing local replacement.
/// The live database and every SQLite sidecar are held exclusively during the
/// swap so an old WAL can never be replayed into the restored database.
/// </summary>
public static class DatabaseRestoreCoordinator
{
    private static readonly TimeSpan DefaultExclusiveAccessTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(250);

    public static string? ApplyPendingRestore(
        string pendingPath,
        string livePath,
        string backupFolder,
        TimeSpan? exclusiveAccessTimeout = null,
        TimeSpan? retryDelay = null)
    {
        if (string.IsNullOrWhiteSpace(pendingPath))
            throw new ArgumentException("A staged restore path is required.", nameof(pendingPath));
        if (string.IsNullOrWhiteSpace(livePath))
            throw new ArgumentException("A live database path is required.", nameof(livePath));
        if (string.IsNullOrWhiteSpace(backupFolder))
            throw new ArgumentException("A backup folder is required.", nameof(backupFolder));

        var pending = Path.GetFullPath(pendingPath);
        if (!File.Exists(pending)) return null;

        var live = Path.GetFullPath(livePath);
        if (string.Equals(pending, live, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The staged restore and live database paths must be different.");

        var backups = Path.GetFullPath(backupFolder);
        var timeout = exclusiveAccessTimeout ?? DefaultExclusiveAccessTimeout;
        var delay = retryDelay ?? DefaultRetryDelay;
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(exclusiveAccessTimeout));
        if (delay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryDelay));

        Directory.CreateDirectory(Path.GetDirectoryName(live)!);
        Directory.CreateDirectory(backups);

        var prepared = live + $".restore-{Guid.NewGuid():N}.tmp";
        string? safetyCopy = null;
        string? safetyTemporary = null;
        try
        {
            // A previous application process can leave native pooled handles alive
            // briefly while it exits. Release every pool owned by this process before
            // validating files or attempting the exclusive restore lease.
            SqliteConnection.ClearAllPools();

            // Never touch the live database until a durable private copy of the
            // selected backup has passed integrity and PosApp schema validation.
            DatabaseFileValidator.ValidatePosAppDatabase(pending);
            DatabaseFileValidator.CopyDurably(pending, prepared);
            DatabaseFileValidator.ValidatePosAppDatabase(prepared);

            using (var lease = AcquireExclusiveLease(live, timeout, delay))
            {
                if (File.Exists(live))
                {
                    // The checkpoint completed before the lease was acquired, so the
                    // main file is a complete snapshot. Copy through the locked handle
                    // to keep the old database recoverable without reopening it.
                    safetyCopy = Path.Combine(
                        backups,
                        $"posapp-before-restore-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.db");
                    safetyTemporary = safetyCopy + ".tmp";
                    CopyLockedDatabaseDurably(lease.GetRequiredStream(live), safetyTemporary);
                    DatabaseFileValidator.ValidatePosAppDatabase(safetyTemporary);
                    File.Move(safetyTemporary, safetyCopy);
                    safetyTemporary = null;
                }

                // WAL, shared-memory, and rollback-journal files belong to the old
                // database identity. They must disappear before the new main file is
                // published or SQLite could apply obsolete pages to restored data.
                DeleteSidecars(live);

                if (File.Exists(live))
                    File.Replace(prepared, live, destinationBackupFileName: null, ignoreMetadataErrors: true);
                else
                    File.Move(prepared, live);
            }

            // The source was already validated, but validate the published path too.
            // Only after that succeeds is the pending marker removed.
            DatabaseFileValidator.ValidatePosAppDatabase(live);
            File.Delete(pending);
            TryDelete(pending + "-wal");
            TryDelete(pending + "-shm");
            TryDelete(pending + "-journal");
            return safetyCopy;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(safetyTemporary);
            TryDelete(prepared);
            TryDelete(prepared + "-wal");
            TryDelete(prepared + "-shm");
            TryDelete(prepared + "-journal");
        }
    }

    private static RestoreFileLease AcquireExclusiveLease(
        string livePath,
        TimeSpan timeout,
        TimeSpan retryDelay)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastContention = null;

        while (true)
        {
            try
            {
                if (File.Exists(livePath)) CheckpointWal(livePath);
                var lease = RestoreFileLease.Acquire(livePath);
                // Close the tiny race between releasing the checkpoint connection
                // and taking the raw file lease. If another writer published a WAL
                // or rollback journal in that interval, release and checkpoint the
                // complete database again instead of discarding committed pages.
                if (!lease.HasNonEmptyFile(livePath + "-wal") &&
                    !lease.HasNonEmptyFile(livePath + "-journal"))
                    return lease;

                lease.Dispose();
                lastContention = new IOException(
                    "SQLite recovery data changed while the exclusive restore lease was being acquired.");
            }
            catch (RestoreLeaseUnavailableException exception)
            {
                lastContention = exception.InnerException ?? exception;
            }
            catch (SqliteException exception) when (IsDatabaseContention(exception))
            {
                lastContention = exception;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new DatabaseRestoreInUseException(livePath, lastContention);

            Thread.Sleep(remaining < retryDelay ? remaining : retryDelay);
        }
    }

    private static void CheckpointWal(string livePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = livePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        connection.Open();

        using (var busyTimeout = connection.CreateCommand())
        {
            busyTimeout.CommandText = "PRAGMA busy_timeout=1000;";
            busyTimeout.ExecuteNonQuery();
        }

        using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var result = checkpoint.ExecuteReader();
        if (result.Read() && !result.IsDBNull(0) && result.GetInt64(0) != 0)
            throw new RestoreLeaseUnavailableException(
                livePath,
                new IOException("SQLite reported an active reader or writer during the restore checkpoint."));
    }

    private static bool IsDatabaseContention(SqliteException exception)
        // SQLITE_BUSY, SQLITE_LOCKED, or SQLITE_CANTOPEN (the Windows result when
        // another process denies the requested file sharing mode).
        => exception.SqliteErrorCode is 5 or 6 or 14;

    private static void CopyLockedDatabaseDurably(FileStream source, string destinationPath)
    {
        source.Position = 0;
        using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 128,
            FileOptions.WriteThrough);
        source.CopyTo(destination);
        destination.Flush(flushToDisk: true);
    }

    private static void DeleteSidecars(string livePath)
    {
        File.Delete(livePath + "-wal");
        File.Delete(livePath + "-shm");
        File.Delete(livePath + "-journal");
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.Delete(path); }
        catch
        {
            // Temporary cleanup must never replace the original restore error.
            // Unique names keep a leftover file from becoming an active database.
        }
    }

    private sealed class RestoreFileLease : IDisposable
    {
        private readonly Dictionary<string, FileStream> _streams =
            new(StringComparer.OrdinalIgnoreCase);

        private RestoreFileLease()
        {
        }

        public static RestoreFileLease Acquire(string livePath)
        {
            var lease = new RestoreFileLease();
            try
            {
                foreach (var path in DatabaseFiles(livePath).Where(File.Exists))
                {
                    try
                    {
                        // FileShare.Delete lets the coordinator atomically replace or
                        // delete its own locked files while denying new SQLite readers
                        // and writers until the complete swap is finished.
                        lease._streams[path] = new FileStream(
                            path,
                            FileMode.Open,
                            FileAccess.ReadWrite,
                            FileShare.Delete);
                    }
                    catch (IOException exception)
                    {
                        throw new RestoreLeaseUnavailableException(path, exception);
                    }
                }

                return lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        public FileStream GetRequiredStream(string path)
            => _streams.TryGetValue(path, out var stream)
                ? stream
                : throw new InvalidOperationException("The live database restore lease was not acquired.");

        public bool HasNonEmptyFile(string path)
            => _streams.TryGetValue(path, out var stream) && stream.Length > 0;

        public void Dispose()
        {
            foreach (var stream in _streams.Values.Reverse()) stream.Dispose();
            _streams.Clear();
        }

        private static IEnumerable<string> DatabaseFiles(string livePath)
        {
            yield return livePath;
            yield return livePath + "-wal";
            yield return livePath + "-shm";
            yield return livePath + "-journal";
        }
    }

    private sealed class RestoreLeaseUnavailableException : IOException
    {
        public RestoreLeaseUnavailableException(string path, Exception innerException)
            : base($"Could not obtain exclusive restore access to '{path}'.", innerException)
        {
        }
    }
}
