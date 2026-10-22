using SqlBackup.Core;

namespace SqlBackup.Infrastructure;

public sealed class BackupDestinationPlanner(ISqlBackupEngine engine)
{
    public async Task<BackupDestinationPlan> SelectAsync(BackupJob job, ConnectionSnapshot connection, CancellationToken cancellationToken)
    {
        foreach (var destination in job.Destinations ?? [])
        {
            if (destination.Kind != DestinationKind.Folder || destination.UserName is not null)
            {
                continue;
            }

            var candidate = job with
            {
                Destinations = null,
                Destination = destination.Location,
                TemporaryFolder = job.TemporaryFolder ?? BackupLocations.DefaultTemporaryFolder
            };
            if (await engine.CanUseDirectDestinationAsync(connection, candidate, cancellationToken))
            {
                return new BackupDestinationPlan(destination.Location, destination.Location, destination.Id);
            }
        }

        string fallback = job.TemporaryFolder ?? BackupLocations.DefaultTemporaryFolder;
        return new BackupDestinationPlan(fallback, job.Storage?.ServiceSourceFolder ?? fallback, null);
    }
}
