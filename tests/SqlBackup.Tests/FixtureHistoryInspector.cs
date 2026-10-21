using SqlBackup.Core;
using SqlBackup.Infrastructure;

namespace SqlBackup.Tests;

public sealed class FixtureHistoryInspector : IBackupHistoryInspector
{
    public bool HasExternal { get; set; }

    public Task<bool> HasExternalDependenciesAsync(ConnectionSnapshot connection, string database,
        DateTimeOffset sinceUtc, string[] ownedBackupSetIds, CancellationToken cancellationToken)
    {
        return Task.FromResult(HasExternal);
    }
}
