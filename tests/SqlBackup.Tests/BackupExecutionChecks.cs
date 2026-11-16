using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using SqlBackup.Core;
using SqlBackup.Infrastructure;
using SqlBackup.Service;

namespace SqlBackup.Tests;

public static class BackupExecutionChecks
{
    public static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        string directory = Path.Combine(Path.GetTempPath(), "SqlBackup.Tests", Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationStore(directory);
        await configuration.InitializeAsync(token);
        var jobs = new JobStore(configuration);
        var runs = new RunStore(configuration);
        var secrets = new ConnectionSecrets(configuration);
        var engine = new SimulatedSqlEngine { BlockExecution = true };
        using var coordinator = new BackupCoordinator(configuration, jobs, runs, secrets, engine, NullLogger<BackupCoordinator>.Instance);
        var history = new HistoryStore(configuration);
        var handler = new ManagementHandler(jobs, runs, secrets, engine, new DestinationDiagnostics(),
            coordinator, NullLogger<ManagementHandler>.Instance, history: history);

        await coordinator.StartAsync(token);
        try
        {
            Guid first = await QueueAsync("Server-A");
            await WaitForAsync(first, run => run.Databases[0].Backup == StageStatus.Running);
            Guid overlapping = await QueueAsync("server-a");
            Guid second = await QueueAsync("Server-B");
            Guid third = await QueueAsync("Server-C");
            Guid fourth = await QueueAsync("Server-D");
            await WaitForAsync(second, run => run.Databases[0].Backup == StageStatus.Running);
            await WaitForAsync(third, run => run.Databases[0].Backup == StageStatus.Running);

            Check(engine.ExecutionCount == 3
                && (await runs.GetAsync(overlapping, token)).Status == RunStatus.Queued
                && (await runs.GetAsync(fourth, token)).Status == RunStatus.Queued,
                "Three independent runs start while an overlapping run and a fourth independent run wait");
            Check(!ExecutionGate.TryEnterCleanup(token), "Cleanup is excluded while backups are active");

            var rejected = await handler.HandleAsync(new ServiceRequest("StopJob", RunId: fourth), token);
            Check(!rejected.Success, "Stop rejects a queued run");
            var stop = await handler.HandleAsync(new ServiceRequest("StopJob", RunId: first), token);
            Check(stop.Success, "Management accepts stop for the selected active run");
            var cancelled = await WaitForAsync(first, run => run.Status == RunStatus.Cancelled);
            Check(cancelled.Databases[0].Backup == StageStatus.Unknown && cancelled.CompletedUtc.HasValue
                && (await history.ReadLogAsync(first, token)).Any(entry => entry.Status == "Cancelled"),
                "Stop durably records cancellation and marks the unfinished backup uncertain");
            await WaitForAsync(overlapping, run => run.Databases[0].Backup == StageStatus.Running);
            Check((await runs.GetAsync(second, token)).Status == RunStatus.Running
                && (await runs.GetAsync(third, token)).Status == RunStatus.Running
                && (await runs.GetAsync(fourth, token)).Status == RunStatus.Queued,
                "Stop releases one slot and leaves the other active jobs running");

            engine.Release.SetResult();
            foreach (Guid runId in new[] { overlapping, second, third, fourth })
            {
                await WaitForAsync(runId, run => run.Status == RunStatus.Succeeded);
            }

            Check(engine.ExecutionCount == 5, "All five runs executed exactly once");
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }

        async Task<Guid> QueueAsync(string server)
        {
            var job = new BackupJob(Guid.NewGuid(), "Execution fixture", ["TestDatabase"], @"C:\FictionalBackups",
                Connection: new ConnectionConfiguration("Fixture", server, true, null));
            await jobs.SaveAsync(job, token);
            return await coordinator.EnqueueAsync(job.Id, token);
        }

        async Task<BackupRun> WaitForAsync(Guid runId, Func<BackupRun, bool> complete)
        {
            while (true)
            {
                var run = await runs.GetAsync(runId, token);
                if (complete(run))
                {
                    return run;
                }

                await Task.Delay(25, token);
            }
        }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException(description);
        }

        Console.WriteLine("PASS: " + description);
    }
}
