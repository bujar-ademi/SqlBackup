using Microsoft.Data.Sqlite;
using SqlBackup.Infrastructure;

namespace SqlBackup.Setup;

public static class SetupDatabase
{
    public static void Snapshot(string database, string snapshot)
    {
        ArtifactFiles.EnsureNoLinks(database);
        ArtifactFiles.EnsureNoLinks(snapshot);
        using var source = Open(database, SqliteOpenMode.ReadWrite);
        using var destination = Open(snapshot, SqliteOpenMode.ReadWriteCreate);
        source.BackupDatabase(destination);
    }

    public static void Restore(string dataDirectory, string? snapshot)
    {
        string database = Path.Combine(dataDirectory, "configuration.db");
        string restored = database + ".restore";
        ArtifactFiles.EnsureNoLinks(database);
        if (snapshot is not null)
        {
            ArtifactFiles.EnsureNoLinks(snapshot);
            ArtifactFiles.EnsureNoLinks(restored);
            // Validate recovery evidence before touching the current database or its journal.
            using (var check = Open(snapshot, SqliteOpenMode.ReadOnly))
            {
                using var command = check.CreateCommand();
                command.CommandText = "PRAGMA quick_check;";
                if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
                {
                    throw new IOException("The database recovery snapshot failed integrity validation.");
                }
            }

            File.Copy(snapshot, restored, overwrite: true);
        }

        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
        {
            string file = database + suffix;
            ArtifactFiles.EnsureNoLinks(file);
            File.Delete(file);
        }

        if (snapshot is not null)
        {
            File.Move(restored, database, overwrite: true);
        }
        else
        {
            File.Delete(database);
        }
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
