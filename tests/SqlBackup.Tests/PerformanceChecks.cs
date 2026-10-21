using System.IO;
using System.Text.Json;
using SqlBackup.Core;
using SqlBackup.Infrastructure;

namespace SqlBackup.Tests;

public static class PerformanceChecks
{
    public static async Task RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SqlBackup.PerformanceTests", Guid.NewGuid().ToString("N"));
        var store = new ConfigurationStore(directory);
        await store.InitializeAsync(CancellationToken.None);
        var jobs = new JobStore(store);
        var runs = new RunStore(store);
        var history = new HistoryStore(store);
        var job = new BackupJob(Guid.NewGuid(), "Performance fixture", ["Fixture"], @"C:\FictionalBackups",
            Connection: new ConnectionConfiguration("Fixture", "fictional-server", true, null),
            Notifications: new JobNotificationPolicy(false, false));
        await jobs.SaveAsync(job, CancellationToken.None);
        job = await jobs.GetAsync(job.Id, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var oldSuccess = new BackupRun(Guid.NewGuid(), job, job.Connection!, now.AddDays(-10), RunStatus.Succeeded,
            [new DatabaseBackupResult("Fixture", StageStatus.Succeeded, StageStatus.Succeeded, SizeBytes: 1048576)], "Fixture");
        await runs.AddAsync(oldSuccess, null, CancellationToken.None);
        for (int index = 0; index < 40; index++)
        {
            await runs.AddAsync(oldSuccess with
            {
                Id = Guid.NewGuid(),
                CreatedUtc = now.AddMinutes(-index),
                Status = RunStatus.Failed,
                Databases = [new DatabaseBackupResult("Fixture", StageStatus.Failed, StageStatus.Skipped)]
            },
                null, CancellationToken.None);
        }

        var bounded = await runs.ReadOperationalHistoryAsync([job], job.Id, CancellationToken.None);
        var projected = OperationalProjection.Create([job], bounded, job.Id, now);
        var full = OperationalProjection.Create([job], await runs.ListAsync(CancellationToken.None, all: true), job.Id, now);
        Check(bounded.Length == 2 && JsonSerializer.Serialize(projected) == JsonSerializer.Serialize(full),
            "Operational polling materializes only the latest run and older successful evidence, even beyond the first history page");

        var queued = oldSuccess with
        {
            Id = Guid.NewGuid(),
            CreatedUtc = now,
            Status = RunStatus.Queued,
            Databases = [new DatabaseBackupResult("Fixture", StageStatus.Pending, StageStatus.Pending)]
        };
        await runs.AddAsync(queued, null, CancellationToken.None);
        var running = queued with
        {
            Status = RunStatus.Running,
            StartedUtc = now,
            PreflightAttempts = 1,
            Databases = [new DatabaseBackupResult("Fixture", StageStatus.Running, StageStatus.Pending)]
        };
        await runs.UpdateAsync(running, CancellationToken.None);
        var backupComplete = running with
        {
            Databases = [new DatabaseBackupResult("Fixture", StageStatus.Succeeded,
            StageStatus.Running, @"C:\FictionalBackups\fixture.bak", SizeBytes: 1048576)]
        };
        await runs.UpdateAsync(backupComplete, CancellationToken.None);
        var terminal = backupComplete with
        {
            Status = RunStatus.Failed,
            CompletedUtc = now.AddSeconds(2),
            Databases = [backupComplete.Databases[0] with { Verification = StageStatus.Failed, Message = "Fictional verification failure" }]
        };
        await runs.UpdateAsync(terminal, CancellationToken.None);
        var log = await history.ReadLogAsync(terminal.Id, CancellationToken.None);
        Check(log.All(item => item.CreatedUtc.HasValue)
            && log.Any(item => item.Stage == "Backup" && item.Status == "Succeeded")
            && log.Any(item => item.Stage == "VERIFYONLY" && item.Status == "Failed")
            && log.Any(item => item.Stage == "Connection"),
            "New job logs persist observed timestamps and distinct connection, backup and verification stages");
        await runs.UpdateAsync(terminal, CancellationToken.None);
        Check((await history.ReadLogAsync(terminal.Id, CancellationToken.None)).Length == log.Length,
            "Repeated persistence cannot duplicate unchanged log transitions");
        var reopened = new HistoryStore(new ConfigurationStore(directory));
        Check((await reopened.ReadLogAsync(terminal.Id, CancellationToken.None)).Length == log.Length,
            "Execution log evidence survives reopening the service store");
        Check(RunLogProjection.FromSnapshot(terminal).Any(item => item.Stage == "Backup" && item.CreatedUtc is null),
            "Legacy snapshot logs never invent per-stage timestamps");
        var summary = RunPresentation.Summarize(terminal);
        Check(summary.BackupObjectCount == 1 && summary.SizeText == "1 MiB" && summary.StatusSymbol == "!",
            "Compact history rows retain successful primary count, payload size and failed overall status");
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
