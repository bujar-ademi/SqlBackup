using SqlBackup.Core;

namespace SqlBackup.Infrastructure;

public interface IBackupHistoryInspector
{
    Task<bool> HasExternalDependenciesAsync(ConnectionSnapshot connection, string database,
        DateTimeOffset sinceUtc, string[] ownedBackupSetIds, CancellationToken cancellationToken);
}
