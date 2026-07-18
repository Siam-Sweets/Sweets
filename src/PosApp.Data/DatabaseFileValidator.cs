using Microsoft.Data.Sqlite;

namespace PosApp.Data;

/// <summary>Shared validation and durable-copy helpers for local database recovery.</summary>
public static class DatabaseFileValidator
{
    public static void ValidatePosAppDatabase(string databasePath)
    {
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("Database backup file not found.", databasePath);
        if (new FileInfo(databasePath).Length == 0)
            throw new InvalidOperationException("The selected database backup is empty.");

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            // Validation is immediately followed by an atomic move/replace in the
            // restore workflow. A pooled native SQLite handle can keep the file
            // locked on Windows even after SqliteConnection.Dispose returns.
            Pooling = false
        }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var result = command.ExecuteScalar()?.ToString();
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected file is not a healthy SQLite backup.");

        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' " +
            "AND name IN ('Users', 'Products', 'Sales', 'Settings');";
        var requiredTables = Convert.ToInt32(command.ExecuteScalar());
        if (requiredTables != 4)
            throw new InvalidOperationException("The selected file is not a PosApp database backup.");
    }

    public static void CopyDurably(string sourcePath, string destinationPath)
    {
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 128, FileOptions.SequentialScan);
        using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 128, FileOptions.WriteThrough);
        source.CopyTo(destination);
        destination.Flush(flushToDisk: true);
    }
}
