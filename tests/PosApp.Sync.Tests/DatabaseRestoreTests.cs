using Microsoft.Data.Sqlite;
using PosApp.Data;

namespace PosApp.Sync.Tests;

public sealed class DatabaseRestoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "posapp-restore-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void RestoreFullyReplacesDatabaseAndOldSqliteSidecars()
    {
        Directory.CreateDirectory(_root);
        var live = Path.Combine(_root, "posapp.db");
        var pending = Path.Combine(_root, "posapp.restore-pending.db");
        var backups = Path.Combine(_root, "Backups");
        CreateDatabase(live, "old-local-data");
        CreateDatabase(pending, "restored-data");

        // Simulate stale artifacts left by the outgoing database. The restored
        // main file must never inherit any one of these identities.
        File.WriteAllBytes(live + "-wal", Array.Empty<byte>());
        File.WriteAllBytes(live + "-shm", Array.Empty<byte>());
        File.WriteAllBytes(live + "-journal", Array.Empty<byte>());

        var safetyCopy = DatabaseRestoreCoordinator.ApplyPendingRestore(
            pending,
            live,
            backups,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(10));

        Assert.NotNull(safetyCopy);
        Assert.Equal("restored-data", ReadMarker(live));
        Assert.Equal("old-local-data", ReadMarker(safetyCopy!));
        Assert.False(File.Exists(pending));
        Assert.False(File.Exists(live + "-wal"));
        Assert.False(File.Exists(live + "-shm"));
        Assert.False(File.Exists(live + "-journal"));
    }

    [Fact]
    public void LockedDatabaseLeavesLiveAndPendingFilesUntouchedUntilRetry()
    {
        Directory.CreateDirectory(_root);
        var live = Path.Combine(_root, "posapp.db");
        var pending = Path.Combine(_root, "posapp.restore-pending.db");
        var backups = Path.Combine(_root, "Backups");
        CreateDatabase(live, "current-data");
        CreateDatabase(pending, "replacement-data");

        var sharedMemory = live + "-shm";
        File.WriteAllBytes(sharedMemory, new byte[] { 1 });
        using (new FileStream(sharedMemory, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var error = Assert.Throws<DatabaseRestoreInUseException>(() =>
                DatabaseRestoreCoordinator.ApplyPendingRestore(
                    pending,
                    live,
                    backups,
                    TimeSpan.FromMilliseconds(75),
                    TimeSpan.FromMilliseconds(10)));

            Assert.Contains("another PosApp process", error.Message);
            Assert.Equal("current-data", ReadMarker(live));
            Assert.True(File.Exists(pending));
        }

        var safetyCopy = DatabaseRestoreCoordinator.ApplyPendingRestore(
            pending,
            live,
            backups,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(10));

        Assert.Equal("replacement-data", ReadMarker(live));
        Assert.Equal("current-data", ReadMarker(safetyCopy!));
        Assert.False(File.Exists(pending));
        Assert.False(File.Exists(sharedMemory));
    }

    private static void CreateDatabase(string path, string marker)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE Users (Id INTEGER PRIMARY KEY);" +
            "CREATE TABLE Products (Id INTEGER PRIMARY KEY);" +
            "CREATE TABLE Sales (Id INTEGER PRIMARY KEY);" +
            "CREATE TABLE Settings (Id INTEGER PRIMARY KEY);" +
            "CREATE TABLE RestoreMarker (Value TEXT NOT NULL);" +
            "INSERT INTO RestoreMarker (Value) VALUES ($marker);";
        command.Parameters.AddWithValue("$marker", marker);
        command.ExecuteNonQuery();
    }

    private static string ReadMarker(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM RestoreMarker LIMIT 1;";
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
