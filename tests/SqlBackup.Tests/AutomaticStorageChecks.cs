using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SqlBackup.Core;
using SqlBackup.Infrastructure;
using SqlBackup.Service;

namespace SqlBackup.Tests;

public static class AutomaticStorageChecks
{
    public static async Task RunAsync()
    {
        var candidate = new BackupJob(Guid.NewGuid(), "Local", ["TestDatabase"], @"D:\Backup",
            TemporaryFolder: @"C:\Staging");
        Check(DirectBackupPolicy.IsCandidate(candidate), "Local destinations are candidates regardless of drive letter");
        Check(!DirectBackupPolicy.IsCandidate(candidate with { Destination = @"\\nas\backups" }),
            "UNC destinations retain staging");
        Check(!DirectBackupPolicy.IsCandidate(candidate with { Storage = new StoragePolicy(ServiceSourceFolder: @"\\sql\staging") }),
            "An explicit source mapping prevents automatic local-path substitution");
        Check(DirectBackupPolicy.MatchesLocalServer("SQLHOST", "sqlhost", false, true, "SqlHost"),
            "Both SQL host names must match the service computer");
        Check(!DirectBackupPolicy.MatchesLocalServer("REMOTE", "REMOTE", false, true, "LOCAL")
            && !DirectBackupPolicy.MatchesLocalServer("LOCAL", "REMOTE", false, true, "LOCAL")
            && !DirectBackupPolicy.MatchesLocalServer("LOCAL", "LOCAL", true, true, "LOCAL")
            && !DirectBackupPolicy.MatchesLocalServer("LOCAL", "LOCAL", false, false, "LOCAL")
            && !DirectBackupPolicy.MatchesLocalServer(null, null, false, true, "LOCAL"),
            "Remote, clustered, unknown and inaccessible SQL destinations retain staging");

        await CheckRunAsync(true, false);
        await CheckRunAsync(false, false);
        await CheckRunAsync(false, true);
        await CheckRunAsync(true, false, true);
    }

    private static async Task CheckRunAsync(bool directAvailable, bool inspectionFails, bool failBackup = false)
    {
        string directory = Path.Combine(Path.GetTempPath(), "SqlBackup.AutomaticStorage", Guid.NewGuid().ToString("N"));
        string staging = Directory.CreateDirectory(Path.Combine(directory, "staging")).FullName;
        string final = Directory.CreateDirectory(Path.Combine(directory, "final")).FullName;
        string secondary = Directory.CreateDirectory(Path.Combine(directory, "secondary")).FullName;
        var configuration = new ConfigurationStore(Path.Combine(directory, "state"));
        await configuration.InitializeAsync(CancellationToken.None);
        var jobs = new JobStore(configuration);
        var runs = new RunStore(configuration);
        var job = new BackupJob(Guid.NewGuid(), "Automatic storage", ["TestDatabase"], final,
            Connection: new ConnectionConfiguration("Fixture", "fictional-server", true, null),
            Storage: new StoragePolicy(7, secondary), TemporaryFolder: staging);
        await jobs.SaveAsync(job, CancellationToken.None);
        job = await jobs.GetAsync(job.Id, CancellationToken.None);
        var engine = new SimulatedSqlEngine
        {
            DirectDestinationAvailable = directAvailable,
            DirectDestinationFailure = inspectionFails ? new IOException("Fixture inspection unavailable") : null,
            FailBackup = failBackup,
            WriteArtifact = true
        };
        using var coordinator = new BackupCoordinator(configuration, jobs, runs, new ConnectionSecrets(configuration),
            engine, NullLogger<BackupCoordinator>.Instance);
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            Guid runId = await coordinator.EnqueueAsync(job.Id, CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            BackupRun run;
            do
            {
                await Task.Delay(20, timeout.Token);
                run = await runs.GetAsync(runId, timeout.Token);
            }
            while (run.Status is RunStatus.Queued or RunStatus.Running);

            var result = run.Databases.Single();
            Check(run.Status == (failBackup ? RunStatus.Failed : RunStatus.Succeeded) && engine.ExecutionCount == 1,
                "Automatic selection executes SQL exactly once and preserves the outcome");
            string savedJob = JsonSerializer.Serialize(await jobs.GetAsync(job.Id, timeout.Token));
            Check(savedJob == JsonSerializer.Serialize(job),
                "Automatic selection leaves the saved job configuration unchanged");

            var cancelledJob = job with { Id = Guid.NewGuid(), Revision = 0 };
            await jobs.SaveAsync(cancelledJob, timeout.Token);
            engine.DirectDestinationFailure = new OperationCanceledException();
            try
            {
                await coordinator.EnqueueAsync(cancelledJob.Id, timeout.Token);
                throw new InvalidOperationException("Cancelled selection must not enqueue a run.");
            }
            catch (OperationCanceledException)
            {
                Check((await runs.ListAsync(timeout.Token)).Length == 1,
                    "Cancelled destination inspection never queues or executes a backup");
            }

            if (failBackup)
            {
                Check(run.Job.TemporaryFolder is null && result.Backup == StageStatus.Failed
                    && !Directory.EnumerateFiles(staging).Any()
                    && !Directory.EnumerateFiles(final).Any(),
                    "A direct SQL backup failure is reported without retrying into staging");
                return;
            }

            Check(File.Exists(Path.Combine(final, Path.GetFileName(result.ArtifactPath!)))
                && File.Exists(Path.Combine(secondary, Path.GetFileName(result.ArtifactPath!))),
                "Primary and secondary final artifacts exist in both storage modes");
            if (directAvailable)
            {
                Check(run.Job.TemporaryFolder is null && Path.GetDirectoryName(result.ArtifactPath) == final
                    && result.Copies?.Length == 1 && result.Copies[0].Folder == secondary
                    && result.StagingCleanup != StageStatus.Succeeded && !Directory.EnumerateFiles(staging).Any(),
                    "Confirmed local output bypasses staging, primary copying and temporary cleanup");
                StorageSecrets.ValidateSnapshot(run.Job,
                    await new StorageSecrets(configuration).ReadRunAsync(runId, timeout.Token));
            }
            else
            {
                Check(run.Job.TemporaryFolder == staging && result.Copies?.Length == 2
                    && result.StagingCleanup == StageStatus.Succeeded,
                    "Unavailable or uncertain direct output keeps verified staging transfers and cleanup");
            }
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
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
