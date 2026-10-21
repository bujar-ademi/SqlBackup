using Microsoft.Data.SqlClient;
using SqlBackup.Core;
using System.Data;

namespace SqlBackup.Infrastructure;

public static class SqlBackupPrerequisites
{
    public static async Task CheckAsync(SqlConnection connection, string database, BackupType type, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 10;
        command.CommandText = """
            DECLARE @resource nvarchar(255) = N'AbaSqlBackup.Database.' + CONVERT(nvarchar(20), DB_ID(@database));
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive',
                 @LockOwner = 'Session', @LockTimeout = 0, @DbPrincipal = 'public';
            SELECT @result;
            """;
        command.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = database;
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) < 0)
        {
            throw new ArgumentException("Another AbaSqlBackup execution holds this database lock. Wait and inspect its outcome before retrying.");
        }

        if (type == BackupType.Full)
        {
            return;
        }

        if (database.Equals("master", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The master database supports only Full backups.");
        }

        command.Parameters.Clear();
        if (type == BackupType.Log)
        {
            // Keep AUTO_CLOSE databases open while inspecting recovery state and starting BACKUP LOG.
            await connection.ChangeDatabaseAsync(database, cancellationToken);
            command.CommandText = """
                SELECT CASE WHEN d.recovery_model IN (1, 2) AND r.last_log_backup_lsn IS NOT NULL THEN 1 ELSE 0 END
                FROM sys.databases d JOIN sys.database_recovery_status r ON r.database_id = d.database_id
                WHERE d.name = @database;
                """;
            command.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = database;
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
            {
                throw new ArgumentException("Log backup requires FULL or BULK_LOGGED recovery and an established backup chain. No recovery model was changed.");
            }
        }
        else
        {
            await connection.ChangeDatabaseAsync(database, cancellationToken);
            try
            {
                command.CommandText = "SELECT COUNT(*) FROM sys.database_files WHERE type = 0 AND differential_base_lsn > 0;";
                if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 0)
                {
                    throw new ArgumentException("Differential backup requires an existing regular Full base. A copy-only Full is not a Differential base.");
                }
            }
            finally
            {
                await connection.ChangeDatabaseAsync("master", cancellationToken);
            }
        }
    }
}
