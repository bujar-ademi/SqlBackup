using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using SqlBackup.Core;
using SqlBackup.Infrastructure;
using SqlBackup.Service;

namespace SqlBackup.Tests;

public static class LocalOperationsChecks
{
    public static async Task RunAsync(ConnectionSnapshot snapshot, string database, string directory)
    {
        var configuration = new ConfigurationStore(Path.Combine(directory, "operations-state"));
        await configuration.InitializeAsync(CancellationToken.None);
        var jobs = new JobStore(configuration);
        var runs = new RunStore(configuration);
        var secrets = new ConnectionSecrets(configuration);
        var engine = new SqlBackupEngine();
        var job = new BackupJob(Guid.NewGuid(), "Local operations fixture", [database], directory,
            Connection: snapshot.Configuration, Notifications: new JobNotificationPolicy(true, true));
        await jobs.SaveAsync(job, CancellationToken.None);
        job = await jobs.GetAsync(job.Id, CancellationToken.None);
        var diagnostics = new JobDiagnosticsService(jobs, secrets, engine, new DestinationDiagnostics(), new StorageDiagnostics());
        var checks = await diagnostics.CheckAsync(job.Id, CancellationToken.None);
        Check(checks.All(item => item.Status is StageStatus.Succeeded or StageStatus.Skipped),
            "Real isolated job diagnostics confirm SQL capabilities, destination visibility and service access");
        var failedJob = job with { Id = Guid.NewGuid(), Revision = 0, Destination = Path.Combine(directory, "missing-operations-folder") };
        await jobs.SaveAsync(failedJob, CancellationToken.None);
        var missingChecks = await diagnostics.CheckAsync(failedJob.Id, CancellationToken.None);
        Check(missingChecks.Any(item => item.Name == "SQL destination visibility" && item.Status == StageStatus.Failed)
            && missingChecks.Any(item => item.Name == "Service source folder" && item.Status == StageStatus.Failed),
            "An invisible SQL destination is a failed diagnostic, never a successful visibility check");

        using var coordinator = new BackupCoordinator(configuration, jobs, runs, secrets, engine, NullLogger<BackupCoordinator>.Instance);
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            Guid successId = await coordinator.EnqueueAsync(job.Id, CancellationToken.None);
            var success = await WaitAsync(new HistoryStore(configuration), successId);
            Guid failedId = await coordinator.EnqueueAsync(failedJob.Id, CancellationToken.None);
            var failure = await WaitAsync(new HistoryStore(configuration), failedId);
            var inbox = await new NotificationStore(configuration).ListAsync(CancellationToken.None);
            Check(success.Status == RunStatus.Succeeded && failure.Status == RunStatus.Failed
                && inbox.Items.Any(item => item.RunId == successId && item.Severity == "Information")
                && inbox.Items.Any(item => item.RunId == failedId && item.Severity == "Warning"),
                "Real successful and failed SQL runs deliver separate durable local alerts without a desktop");
            var filtered = await new HistoryStore(configuration).QueryAsync(new HistoryQuery(failedJob.Id, RunStatus.Failed), CancellationToken.None);
            Check(filtered.Items.Single().Id == failedId,
                "Operational history filters retrieve the actual failed SQL execution");
            var log = await new HistoryStore(configuration).ReadLogAsync(successId, CancellationToken.None);
            Check(log.Any(item => item.Stage == "Connection" && item.Status == "Succeeded")
                && log.Any(item => item.Stage == "Backup" && item.Status == "Succeeded")
                && log.Any(item => item.Stage == "VERIFYONLY" && item.Status == "Succeeded")
                && log.All(item => item.CreatedUtc.HasValue),
                "A real SQL run records timestamped connection, backup and verification log transitions");
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<BackupRun> WaitAsync(HistoryStore history, Guid runId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var run = await history.GetAsync(runId, timeout.Token);
            if (run.Status is not (RunStatus.Queued or RunStatus.Running))
            {
                return run;
            }

            await Task.Delay(25, timeout.Token);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }

        Console.WriteLine("PASS: " + message);
    }
}
