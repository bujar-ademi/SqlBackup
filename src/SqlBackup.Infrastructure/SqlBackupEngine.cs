using Microsoft.Data.SqlClient;
using SqlBackup.Core;
using System.Data;
using System.Globalization;
using System.Security.Principal;

namespace SqlBackup.Infrastructure;

public sealed class SqlBackupEngine : ISqlBackupEngine
{
    public async Task<ServerInspection> InspectAsync(ConnectionSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(snapshot);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = """
            SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')),
                   CONVERT(nvarchar(128), SERVERPROPERTY('Edition')),
                   CONVERT(int, SERVERPROPERTY('EngineEdition')),
                   CONVERT(int, SERVERPROPERTY('ProductMajorVersion'));
            """;
        string version;
        string edition;
        int engine;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            version = reader.GetString(0);
            edition = reader.GetString(1);
            engine = reader.GetInt32(2);
            if (reader.GetInt32(3) < 15 || engine is not (2 or 3 or 4))
            {
                throw new ArgumentException("Phase 2 supports SQL Server 2019+ Enterprise, Developer, Standard, Web, or Express instances; Azure SQL is excluded.");
            }
        }

        command.CommandText = """
            SELECT TOP (512) name, state_desc, recovery_model_desc,
                   COALESCE(HAS_DBACCESS(name), 0),
                   COALESCE(HAS_PERMS_BY_NAME(name, 'DATABASE', 'BACKUP DATABASE'), 0),
                   COALESCE(HAS_PERMS_BY_NAME(name, 'DATABASE', 'BACKUP LOG'), 0)
            FROM sys.databases WHERE name <> N'tempdb' AND source_database_id IS NULL
            ORDER BY name;
            """;
        var databases = new List<DatabaseInfo>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                string state = reader.GetString(1);
                bool eligible = state == "ONLINE" && reader.GetInt32(3) == 1 && reader.GetInt32(4) == 1;
                databases.Add(new DatabaseInfo(reader.GetString(0), state, reader.GetString(2), eligible,
                    eligible ? "Ready" : "Requires ONLINE state, database access, and BACKUP DATABASE permission",
                    state == "ONLINE" && reader.GetInt32(3) == 1 && reader.GetInt32(5) == 1));
            }
        }

        bool compression = engine == 3 || edition.Contains("Standard", StringComparison.OrdinalIgnoreCase);
        return new ServerInspection(version, edition, compression,
            WindowsIdentity.GetCurrent().Name, databases.ToArray());
    }

    public async Task<DatabaseBackupResult> ExecuteAsync(
        ConnectionSnapshot snapshot,
        BackupJob job,
        string database,
        string artifactPath,
        Func<DatabaseBackupResult, Task> reportAsync,
        CancellationToken cancellationToken,
        BackupType type = BackupType.Full)
    {
        var result = new DatabaseBackupResult(database, StageStatus.Running,
            job.Verify ? StageStatus.Pending : StageStatus.Skipped, artifactPath,
            Message: $"SQL Server is preparing the {type} backup.");
        await reportAsync(result);
        await using var connection = CreateConnection(snapshot);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await SqlBackupPrerequisites.CheckAsync(connection, database, type, cancellationToken);
            await EnsureDestinationUnusedAsync(connection, artifactPath, cancellationToken);
            await using var backup = BackupSql.CreateBackupCommand(connection, job, database, artifactPath, type);
            await backup.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception)
        {
            return result with { Backup = StageStatus.Failed, Verification = StageStatus.Skipped, Message = SqlFailure.Describe(exception) };
        }
        catch (ArgumentException exception)
        {
            return result with { Backup = StageStatus.Failed, Verification = StageStatus.Skipped, Message = exception.Message };
        }

        result = result with { Backup = StageStatus.Succeeded, Message = $"{type} backup completed; recording metadata." };
        // Persist primary completion before metadata or verification can fail.
        await reportAsync(result);
        try
        {
            result = await ReadHeaderAsync(connection, artifactPath, result, cancellationToken);
        }
        catch (SqlException)
        {
            result = result with { Message = $"{type} backup completed; header metadata could not be read with this login." };
        }

        await reportAsync(result);
        if (!job.Verify)
        {
            return result;
        }

        result = result with { Verification = StageStatus.Running };
        await reportAsync(result);
        try
        {
            await using var verify = connection.CreateCommand();
            verify.CommandTimeout = 0;
            verify.CommandText = job.Checksum
                ? "RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM;"
                : "RESTORE VERIFYONLY FROM DISK = @path;";
            verify.Parameters.Add("@path", SqlDbType.NVarChar, 1024).Value = artifactPath;
            await verify.ExecuteNonQueryAsync(cancellationToken);
            return result with
            {
                Verification = StageStatus.Succeeded,
                Message = $"{type} backup and RESTORE VERIFYONLY completed. Restore has not been tested."
            };
        }
        catch (SqlException exception)
        {
            return result with { Verification = StageStatus.Failed, Message = "Backup completed; VERIFYONLY failed. " + SqlFailure.Describe(exception) };
        }
    }

    internal static SqlConnection CreateConnection(ConnectionSnapshot snapshot)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = snapshot.Configuration.Server,
            InitialCatalog = "master",
            IntegratedSecurity = snapshot.Configuration.UseWindowsAuthentication,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = snapshot.Configuration.TrustServerCertificate,
            ConnectTimeout = 5,
            ApplicationName = "AbaSqlBackup",
            PersistSecurityInfo = false,
            Pooling = false
        };
        if (!builder.IntegratedSecurity)
        {
            builder.UserID = snapshot.Configuration.UserName;
            builder.Password = ConnectionSecrets.UnprotectPassword(snapshot);
        }

        return new SqlConnection(builder.ConnectionString);
    }

    private static async Task EnsureDestinationUnusedAsync(SqlConnection connection, string path, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = """
            SELECT CONVERT(int, file_exists), CONVERT(int, file_is_a_directory),
                   CONVERT(int, parent_directory_exists) FROM sys.dm_os_file_exists(@path);
            """;
        command.Parameters.Add("@path", SqlDbType.NVarChar, 1024).Value = path;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetInt32(0) != 0
            || reader.GetInt32(1) != 0 || reader.GetInt32(2) != 1)
        {
            throw new ArgumentException("The SQL-visible destination is missing, inaccessible, or already contains this artifact. No file is overwritten.");
        }
    }

    private static async Task<DatabaseBackupResult> ReadHeaderAsync(
        SqlConnection connection, string path, DatabaseBackupResult result, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 15;
        command.CommandText = "RESTORE HEADERONLY FROM DISK = @path;";
        command.Parameters.Add("@path", SqlDbType.NVarChar, 1024).Value = path;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return result;
        }

        return result with
        {
            SizeBytes = Convert.ToInt64(reader["CompressedBackupSize"], CultureInfo.InvariantCulture),
            BackupSetId = Convert.ToString(reader["BackupSetGUID"], CultureInfo.InvariantCulture),
            FirstLsn = Convert.ToString(reader["FirstLSN"], CultureInfo.InvariantCulture),
            LastLsn = Convert.ToString(reader["LastLSN"], CultureInfo.InvariantCulture),
            CheckpointLsn = Convert.ToString(reader["CheckpointLSN"], CultureInfo.InvariantCulture),
            DatabaseBackupLsn = Convert.ToString(reader["DatabaseBackupLSN"], CultureInfo.InvariantCulture),
            DatabaseId = Convert.ToString(reader["FamilyGUID"], CultureInfo.InvariantCulture),
            RecoveryForkId = Convert.ToString(reader["RecoveryForkID"], CultureInfo.InvariantCulture),
            HeaderCopyOnly = Convert.ToBoolean(reader["IsCopyOnly"], CultureInfo.InvariantCulture),
            HasSingleRecoveryFork = Equals(reader["RecoveryForkID"], reader["FirstRecoveryForkID"]),
            HeaderType = Convert.ToInt32(reader["BackupType"], CultureInfo.InvariantCulture) switch
            {
                1 => BackupType.Full,
                2 => BackupType.Log,
                5 => BackupType.Differential,
                _ => null
            }
        };
    }
}
