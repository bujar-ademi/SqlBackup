using Microsoft.Data.Sqlite;
using SqlBackup.Core;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SqlBackup.Infrastructure;

public sealed class ConfigurationStore
{
    public string ConnectionString { get; }
    public string DirectoryPath { get; }

    public ConfigurationStore(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        SecurityIdentifier identity = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("A Windows identity is required.");

        foreach (var sid in new[]
        {
            identity,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }

        new DirectoryInfo(directory).SetAccessControl(security);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "configuration.db"),
            Pooling = false
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        long version = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        if (version > 9)
        {
            throw new InvalidOperationException("The configuration schema requires a newer application version.");
        }

        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Configuration (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                Metadata TEXT NOT NULL,
                ProtectedPassword BLOB NULL
            );
            CREATE TABLE IF NOT EXISTS Jobs (
                Id TEXT PRIMARY KEY,
                Revision INTEGER NOT NULL,
                Payload TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Runs (
                Id TEXT PRIMARY KEY,
                JobId TEXT NOT NULL,
                Status INTEGER NOT NULL,
                CreatedUtc TEXT NOT NULL,
                Payload TEXT NOT NULL,
                ProtectedPassword BLOB NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Runs_CreatedUtc ON Runs(CreatedUtc);
            CREATE INDEX IF NOT EXISTS IX_Runs_JobCreatedUtc ON Runs(JobId, CreatedUtc DESC);
            CREATE TABLE IF NOT EXISTS JobSecrets (
                JobId TEXT PRIMARY KEY,
                ProtectedPassword BLOB NULL
            );
            CREATE TABLE IF NOT EXISTS JobStorageSecrets (
                JobId TEXT PRIMARY KEY,
                Payload TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS RunStorageSecrets (
                RunId TEXT PRIMARY KEY,
                Payload TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS JobDestinationSecrets (
                JobId TEXT PRIMARY KEY,
                Payload TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS RunDestinationSecrets (
                RunId TEXT PRIMARY KEY,
                Payload TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS RunEvents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId TEXT NOT NULL,
                Payload TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_RunEvents_RunId ON RunEvents(RunId, Id);
            CREATE TABLE IF NOT EXISTS Notifications (
                EventKey TEXT PRIMARY KEY,
                CreatedUtc TEXT NOT NULL,
                ReadUtc TEXT NULL,
                Payload TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Notifications_CreatedUtc ON Notifications(CreatedUtc);
            CREATE TABLE IF NOT EXISTS CleanupAudit (
                Id TEXT PRIMARY KEY,
                CreatedUtc TEXT NOT NULL,
                JobId TEXT NOT NULL,
                Payload TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ScheduleState (
                JobId TEXT PRIMARY KEY,
                JobRevision INTEGER NOT NULL,
                CheckedUtc TEXT NOT NULL,
                Message TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (version < 3)
        {
            await JobConnectionMigration.ApplyAsync(connection, transaction, cancellationToken);
        }

        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Runs') WHERE name = 'ScheduledKey';";
        if ((long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L) == 0)
        {
            command.CommandText = "ALTER TABLE Runs ADD COLUMN ScheduledKey TEXT NULL;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        command.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_Runs_ScheduledKey ON Runs(ScheduledKey); PRAGMA user_version = 9;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
    }

    public async Task<ConnectionConfiguration?> ReadAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Metadata FROM Configuration WHERE Id = 1;";
        string? metadata = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (metadata is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize<ConnectionConfiguration>(metadata);
    }

    public async Task SaveAsync(ConnectionConfiguration configuration, string? password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.Name) || configuration.Name.Length > 100
            || string.IsNullOrWhiteSpace(configuration.Server) || configuration.Server.Length > 256)
        {
            throw new ArgumentException("A name and server are required (maximum 100 and 256 characters).");
        }

        if (!configuration.UseWindowsAuthentication
            && (string.IsNullOrWhiteSpace(configuration.UserName) || configuration.UserName.Length > 128))
        {
            throw new ArgumentException("SQL authentication requires a user name.");
        }

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ProtectedPassword FROM Configuration WHERE Id = 1;";
        byte[]? protectedPassword = await command.ExecuteScalarAsync(cancellationToken) as byte[];

        command.CommandText = "SELECT Metadata FROM Configuration WHERE Id = 1;";
        string? previousMetadata = await command.ExecuteScalarAsync(cancellationToken) as string;
        var previous = previousMetadata is null
            ? null
            : JsonSerializer.Deserialize<ConnectionConfiguration>(previousMetadata);
        if (previous?.Server != configuration.Server || previous?.UserName != configuration.UserName)
        {
            protectedPassword = null;
        }

        if (password?.Length > 1024)
        {
            throw new ArgumentException("The password exceeds the supported length.");
        }

        if (configuration.UseWindowsAuthentication)
        {
            protectedPassword = null;
        }
        else if (!string.IsNullOrEmpty(password))
        {
            byte[] plaintext = Encoding.UTF8.GetBytes(password);
            try
            {
                protectedPassword = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        if (!configuration.UseWindowsAuthentication && protectedPassword is null)
        {
            throw new ArgumentException("SQL authentication requires a password.");
        }

        var saved = configuration with
        {
            HasPassword = protectedPassword is not null,
            UserName = configuration.UseWindowsAuthentication ? null : configuration.UserName
        };
        command.CommandText = """
            INSERT INTO Configuration (Id, Metadata, ProtectedPassword) VALUES (1, $metadata, $password)
            ON CONFLICT(Id) DO UPDATE SET Metadata = excluded.Metadata, ProtectedPassword = excluded.ProtectedPassword;
            """;
        command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(saved));
        command.Parameters.AddWithValue("$password", (object?)protectedPassword ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
