using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using SqlBackup.Core;
using SqlBackup.Infrastructure;
using SqlBackup.Service;

namespace SqlBackup.Tests;

public static class ScheduleChecks
{
    public static async Task RunAsync()
    {
        var start = new DateTime(2026, 1, 1, 2, 30, 0, DateTimeKind.Unspecified);
        var schedule = new BackupSchedule(true, "Central European Standard Time", start,
            1440, 0, 0, Enum.GetValues<DayOfWeek>(), []);
        var spring = ScheduleCalculator.Next(schedule, new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero));
        Check(spring?.DueUtc == new DateTimeOffset(2026, 3, 30, 0, 30, 0, TimeSpan.Zero),
            "Nonexistent spring DST occurrence is skipped without shifting its local time");
        var fall = ScheduleCalculator.Next(schedule, new DateTimeOffset(2026, 10, 24, 23, 0, 0, TimeSpan.Zero));
        Check(fall?.DueUtc == new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero)
            && ScheduleCalculator.Next(schedule, fall.DueUtc)?.DueUtc.Date == new DateTime(2026, 10, 26),
            "Repeated autumn DST time runs once using the earlier UTC occurrence");

        var monthly = schedule with { TimeZoneId = "UTC", StartLocal = new DateTime(2026, 1, 1), MonthDays = [31] };
        Check(ScheduleCalculator.Next(monthly, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero))?.DueUtc.Day == 31,
            "Monthly day 31 skips short months without clamping");
        var overnight = monthly with
        {
            StartLocal = new DateTime(2026, 1, 1),
            MonthDays = [],
            FullIntervalMinutes = 60,
            WindowStart = new TimeOnly(22, 0),
            WindowEnd = new TimeOnly(6, 0)
        };
        var window = ScheduleCalculator.Next(overnight, new DateTimeOffset(2026, 1, 1, 6, 0, 0, TimeSpan.Zero));
        Check(window?.DueUtc.Hour == 22, "Overnight window skips the excluded daytime range");
        var mixed = overnight with { WindowStart = null, WindowEnd = null, FullIntervalMinutes = 1440, DifferentialIntervalMinutes = 60, LogIntervalMinutes = 15 };
        var collision = ScheduleCalculator.Next(mixed, new DateTimeOffset(2025, 12, 31, 23, 59, 0, TimeSpan.Zero));
        Check(collision?.Type == BackupType.Full
            && ScheduleCalculator.Next(mixed, collision.DueUtc)?.Type == BackupType.Log,
            "Collisions prefer Full then advance to the next distinct occurrence");
        Check(ScheduleCalculator.Due(mixed, DateTimeOffset.MinValue,
            new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero))?.Type == BackupType.Full,
            "Missed schedules coalesce into one highest-priority recovery base");
        var weekdayOnly = schedule with { TimeZoneId = "UTC", Weekdays = [DayOfWeek.Monday] };
        Check(ScheduleCalculator.Next(weekdayOnly, new DateTimeOffset(2026, 1, 6, 0, 0, 0, TimeSpan.Zero))?.DueUtc.Day == 12,
            "Weekday filters skip ineligible dates");
        try
        {
            ScheduleCalculator.Validate(schedule with { FullIntervalMinutes = 0 });
            throw new InvalidOperationException("Expected invalid schedule rejection.");
        }
        catch (ArgumentException)
        {
            Check(true, "Schedules reject missing Full recovery intervals");
        }

        await CheckPersistentSchedulerAsync(mixed);
    }

    private static async Task CheckPersistentSchedulerAsync(BackupSchedule schedule)
    {
        string directory = Path.Combine(Path.GetTempPath(), "SqlBackup.ScheduleTests", Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationStore(directory);
        await configuration.InitializeAsync(CancellationToken.None);
        var jobs = new JobStore(configuration);
        var runs = new RunStore(configuration);
        var states = new ScheduleStore(configuration);
        var job = new BackupJob(Guid.NewGuid(), "Scheduled fixture", ["TestDatabase"], @"C:\FictionalBackups",
            Connection: new ConnectionConfiguration("Fixture", "fictional-server", true, null), Schedule: schedule);
        await jobs.SaveAsync(job, CancellationToken.None);
        job = await jobs.GetAsync(job.Id, CancellationToken.None);
        var engine = new SimulatedSqlEngine { PreflightFailures = 2, RecoveryModel = "FULL" };
        using var coordinator = new BackupCoordinator(configuration, jobs, runs, new ConnectionSecrets(configuration),
            engine, NullLogger<BackupCoordinator>.Instance);
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            var worker = new ScheduleWorker(configuration, jobs, states, coordinator, NullLogger<ScheduleWorker>.Instance);
            var now = new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);
            await worker.TickAsync(now, CancellationToken.None);
            var run = await WaitAsync(runs);
            Check(run.Type == BackupType.Full && !run.Job.CopyOnly && run.ScheduledForUtc.HasValue
                && run.PreflightAttempts == 3 && engine.ExecutionCount == 1,
                "Scheduled Full uses a regular base; only read-only preflight is retried before a single backup execution");
            // Simulate a crash after durable enqueue but before cursor advancement.
            await states.WriteAsync(new ScheduleStatus(job.Id, job.Revision, DateTimeOffset.MinValue, "Crash fixture"), CancellationToken.None);
            var restarted = new ScheduleWorker(configuration, jobs, states, coordinator, NullLogger<ScheduleWorker>.Instance);
            await restarted.TickAsync(now, CancellationToken.None);
            Check((await runs.ListAsync(CancellationToken.None)).Length == 1,
                "Durable occurrence identity prevents replay after a cursor-write crash");
            await restarted.TickAsync(now.AddMinutes(15), CancellationToken.None);
            await WaitAsync(runs);
            Check((await runs.ListAsync(CancellationToken.None)).Any(item => item.Type == BackupType.Log),
                "Later Log occurrences are queued by the same service without a desktop process");
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }

        var skipJob = job with
        {
            Id = Guid.NewGuid(),
            Revision = 0,
            Name = "Skip missed fixture",
            Schedule = schedule with { RunMissed = false, DifferentialIntervalMinutes = 0, LogIntervalMinutes = 0 }
        };
        await jobs.SaveAsync(skipJob, CancellationToken.None);
        using var skipCoordinator = new BackupCoordinator(configuration, jobs, runs, new ConnectionSecrets(configuration),
            engine, NullLogger<BackupCoordinator>.Instance);
        await skipCoordinator.StartAsync(CancellationToken.None);
        try
        {
            var skipWorker = new ScheduleWorker(configuration, jobs, states, skipCoordinator, NullLogger<ScheduleWorker>.Instance);
            await skipWorker.TickAsync(new DateTimeOffset(2026, 1, 5, 12, 1, 0, TimeSpan.Zero), CancellationToken.None);
            Check(!(await runs.ListAsync(CancellationToken.None, all: true)).Any(item => item.Job.Id == skipJob.Id),
                "Disabling catch-up skips occurrences older than the documented 90-second grace period");
        }
        finally
        {
            await skipCoordinator.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<BackupRun> WaitAsync(RunStore runs)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var result = (await runs.ListAsync(timeout.Token)).First();
            if (result.Status is not (RunStatus.Queued or RunStatus.Running))
            {
                return result;
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
