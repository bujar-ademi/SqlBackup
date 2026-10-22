using SqlBackup.Core;
using SqlBackup.Infrastructure;

namespace SqlBackup.Service;

public sealed class JobDiagnosticsService(JobStore jobs, ConnectionSecrets secrets, ISqlBackupEngine engine,
    DestinationDiagnostics destination, StorageDiagnostics storage, StorageSecrets? storageSecrets = null,
    DestinationStorage? destinationStorage = null)
{
    public async Task<DiagnosticItem[]> CheckAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(jobId, cancellationToken);
        var checks = new List<DiagnosticItem>();
        string sqlStage = "SQL connection";
        BackupDestinationPlan? plan = null;
        try
        {
            var connection = await secrets.ReadSnapshotAsync(jobId, cancellationToken);
            var inspection = await engine.InspectAsync(connection, cancellationToken);
            checks.Add(new DiagnosticItem("SQL connection", StageStatus.Succeeded,
                $"SQL {inspection.Version} · {inspection.Edition}. Windows authentication uses the service identity."));
            foreach (string database in job.Databases)
            {
                var info = inspection.Databases.SingleOrDefault(item => item.Name == database);
                checks.Add(new DiagnosticItem(database, info?.CanBackUp == true ? StageStatus.Succeeded : StageStatus.Failed,
                    info is null ? "Database is no longer visible to this SQL identity."
                        : $"{info.State} · {info.RecoveryModel} · BACKUP DATABASE: {info.CanBackUp} · BACKUP LOG: {info.CanBackUpLog}"));
            }

            try
            {
                JobScheduleValidator.Validate(job, inspection);
                bool enabled = job.Enabled && job.Schedule?.Enabled == true;
                checks.Add(new DiagnosticItem("Schedule prerequisites", enabled ? StageStatus.Succeeded : StageStatus.Skipped,
                    enabled ? "Configured types match inspected capabilities. Execution checks actual Differential/Log bases again."
                        : "Automatic schedule is disabled. Scheduled backup types were not validated."));
            }
            catch (ArgumentException exception)
            {
                checks.Add(new DiagnosticItem("Schedule prerequisites", StageStatus.Failed, exception.Message));
            }

            sqlStage = "SQL destination visibility";
            if (job.Destinations is not null)
            {
                plan = await new BackupDestinationPlanner(engine).SelectAsync(job, connection, cancellationToken);
            }

            string visibility = await destination.CheckAsync(connection, plan?.SqlFolder ?? BackupLocations.SqlFolder(job), cancellationToken);
            checks.Add(new DiagnosticItem("SQL destination visibility", StageStatus.Succeeded,
                visibility + " This does not prove SQL write permissions or a successful backup."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            checks.Add(new DiagnosticItem(sqlStage, StageStatus.Failed, SqlFailure.Describe(exception)));
        }

        var access = storageSecrets is null ? new StorageAccessSnapshot()
            : await storageSecrets.PrepareAsync(job, null, cancellationToken);
        if (job.Destinations is not null)
        {
            bool direct = plan?.DirectDestinationId.HasValue == true;
            var sourceJob = job with
            {
                Destinations = null,
                Destination = plan?.ServiceFolder ?? BackupLocations.SourceFolder(job),
                TemporaryFolder = null,
                Storage = new StoragePolicy(SourceUserName: direct ? null : job.Storage?.SourceUserName)
            };
            checks.AddRange(await storage.CheckAsync(sourceJob, cancellationToken,
                new StorageAccessSnapshot(Source: direct ? null : access.Source)));
            var credentials = await jobs.PrepareDestinationsAsync(job, null, cancellationToken);
            foreach (var credential in credentials)
            {
                try
                {
                    await (destinationStorage ?? new DestinationStorage()).For(credential.Destination.Kind)
                        .CheckAccessAsync(credential, cancellationToken);
                    checks.Add(new DiagnosticItem(credential.Destination.Name, StageStatus.Succeeded,
                        "Destination is accessible. This does not prove a complete upload, sufficient capacity or restore."));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    checks.Add(new DiagnosticItem(credential.Destination.Name, StageStatus.Failed, DestinationFailure.Describe(exception)));
                }
            }

            return checks.ToArray();
        }

        checks.AddRange(await storage.CheckAsync(job, cancellationToken, access));
        return checks.ToArray();
    }
}
