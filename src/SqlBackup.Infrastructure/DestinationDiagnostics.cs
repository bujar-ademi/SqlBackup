using Microsoft.Data.SqlClient;
using System.Data;

namespace SqlBackup.Infrastructure;

public sealed class DestinationDiagnostics
{
    public async Task<string> CheckAsync(ConnectionSnapshot snapshot, string destination, CancellationToken cancellationToken)
    {
        SqlBackup.Core.JobValidation.ValidateDestination(destination);
        await using var connection = SqlBackupEngine.CreateConnection(snapshot);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT file_is_a_directory FROM sys.dm_os_file_exists(@path);";
        command.Parameters.Add("@path", SqlDbType.NVarChar, 1024).Value = destination;
        bool sqlFolderVisible = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
        if (!sqlFolderVisible)
        {
            throw new SqlBackup.Core.JobValidationException("SqlDestinationUnavailable",
                "SQL Server cannot see the temporary/backup folder. It may not exist or its SQL service account lacks access. Create it on the SQL host and grant that SQL service account read/write access, then test again. For remote SQL, use that server's path; a folder on the application host does not prove SQL access.");
        }

        bool serviceFolderVisible = Directory.Exists(destination);
        return $"SQL Server sees folder: {sqlFolderVisible}. App service sees folder: {serviceFolderVisible}. "
            + "These checks do not prove write access or sufficient disk space. SQL Server writes using its own service account. "
            + "For remote SQL, drive paths refer to that server. The folder must already exist.";
    }
}
