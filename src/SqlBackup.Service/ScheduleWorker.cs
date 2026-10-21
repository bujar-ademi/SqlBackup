using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlBackup.Core;
using SqlBackup.Infrastructure;

namespace SqlBackup.Service;

public sealed class ScheduleWorker(
    ConfigurationStore configuration,
    JobStore jobs,
    ScheduleStore schedules,
    BackupCoordinator coordinator,
    ILogger<ScheduleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await configuration.InitializeAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            await TickAsync(DateTimeOffset.UtcNow, stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task TickAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        if (coordinator.IsMaintenanceActive)
        {
            return;
        }

        foreach (var job in await jobs.ListAsync(cancellationToken))
        {
            if (!job.Enabled || job.Schedule?.Enabled != true)
            {
                continue;
            }

            var state = await schedules.ReadAsync(job.Id, cancellationToken);
            DateTimeOffset after = state?.JobRevision == job.Revision ? state.CheckedUtc : DateTimeOffset.MinValue;
            if (!job.Schedule.RunMissed && after < nowUtc.AddSeconds(-90))
            {
                after = nowUtc.AddSeconds(-90);
            }

            var due = ScheduleCalculator.Due(job.Schedule, after, nowUtc);
            string message = "Schedule checked; waiting for the next occurrence.";
            if (due is not null)
            {
                try
                {
                    await coordinator.EnqueueAsync(job.Id, cancellationToken, job.Revision,
                        job.Connection, due.Type, due.DueUtc);
                    message = $"{due.Type} occurrence accepted for {due.DueUtc:O}. Missed occurrences were coalesced.";
                }
                catch (ArgumentException)
                {
                    // Busy queue or a concurrent job edit: leave the cursor unchanged and retry a later tick.
                    logger.LogInformation("Scheduled occurrence deferred. EventCode={EventCode}", "ScheduleDeferred");
                    continue;
                }
            }

            await schedules.WriteAsync(new ScheduleStatus(job.Id, job.Revision, nowUtc, message), cancellationToken);
        }
    }
}
