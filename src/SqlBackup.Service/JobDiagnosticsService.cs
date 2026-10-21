using SqlBackup.Core;
using SqlBackup.Infrastructure;

namespace SqlBackup.Service;

public sealed class JobDiagnosticsService(JobStore jobs, ConnectionSecrets secrets, ISqlBackupEngine engine,
    DestinationDiagnostics destination, StorageDiagnostics storage, StorageSecrets? storageSecrets = null)
{
    public async Task<DiagnosticItem[]> CheckAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(jobId, cancellationToken);
        var checks = new List<DiagnosticItem>();
        string sqlStage = "SQL connection";
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
            string visibility = await destination.CheckAsync(connection, BackupLocations.SqlFolder(job), cancellationToken);
            checks.Add(new DiagnosticItem("SQL destination visibility", StageStatus.Succeeded,
                visibility + " This does not prove SQL write permissions or a successful backup."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            checks.Add(new DiagnosticItem(sqlStage, StageStatus.Failed, SqlFailure.Describe(exception)));
        }

        var access = storageSecrets is null ? new StorageAccessSnapshot()
            : await storageSecrets.PrepareAsync(job, null, cancellationToken);
        checks.AddRange(await storage.CheckAsync(job, cancellationToken, access));
        return checks.ToArray();
    }
}
