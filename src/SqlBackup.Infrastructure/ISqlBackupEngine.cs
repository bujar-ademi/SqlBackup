using SqlBackup.Core;

namespace SqlBackup.Infrastructure;

public interface ISqlBackupEngine
{
    Task<bool> CanUseDirectDestinationAsync(ConnectionSnapshot connection, BackupJob job, CancellationToken cancellationToken);

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
