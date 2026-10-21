using SqlBackup.Core;

namespace SqlBackup.Infrastructure;

public interface ISqlBackupEngine
{
    Task<ServerInspection> InspectAsync(ConnectionSnapshot connection, CancellationToken cancellationToken);

    Task<DatabaseBackupResult> ExecuteAsync(
        ConnectionSnapshot connection,
        BackupJob job,
        string database,
        string artifactPath,
        Func<DatabaseBackupResult, Task> reportAsync,
        CancellationToken cancellationToken,
        BackupType type = BackupType.Full);
}
