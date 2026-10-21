namespace SqlBackup.Core;

public static class OperationalProjection
{
    public static OperationalSnapshot Create(BackupJob[] jobs, BackupRun[] history, Guid? selectedJobId, DateTimeOffset nowUtc)
    {
        var overviews = new List<JobOverview>();
        foreach (var job in jobs)
        {
            var latest = history.FirstOrDefault(item => item.Job.Id == job.Id
                && string.Equals(item.Connection.Server, job.Connection?.Server, StringComparison.OrdinalIgnoreCase));
            DateTimeOffset? next = null;
            string scheduleMessage = "Automatic backups disabled";
            if (job.Enabled && job.Schedule?.Enabled == true)
            {
                try
                {
                    next = ScheduleCalculator.Next(job.Schedule, nowUtc)?.DueUtc;
                    scheduleMessage = next.HasValue ? "Next scheduled occurrence" : "No eligible occurrence in five years";
                }
                catch (ArgumentException)
                {
                    scheduleMessage = "Saved schedule is invalid; review Advanced Schedule";
                }
            }

            string status = latest is null ? "No executions yet" : latest.Status.ToString();
            if (!job.Enabled)
            {
                status = "Disabled · " + status;
            }

            overviews.Add(new JobOverview(job.Id, latest?.Status, latest?.CreatedUtc, status, next, scheduleMessage));
        }

        var selected = jobs.SingleOrDefault(item => item.Id == selectedJobId);
        var databases = new List<DatabaseOverview>();
        if (selected is not null)
        {
            // A changed server cannot inherit another server's successful history under the same job ID.
            var matching = history.Where(item => item.Job.Id == selected.Id
                && string.Equals(item.Connection.Server, selected.Connection?.Server, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (string database in selected.Databases)
            {
                var latest = matching.FirstOrDefault(item => item.Databases.Any(result => result.Database == database));
                var result = latest?.Databases.First(item => item.Database == database);
                var lastSuccess = matching.FirstOrDefault(item => item.Databases.Any(candidate =>
                    candidate.Database == database && candidate.Backup == StageStatus.Succeeded));
                string copy = "Not configured";
                if (result?.Copies is { Length: > 0 } copies)
                {
                    copy = string.Join(", ", copies.Select(item => item.Status.ToString()).Distinct());
                }
                else if (latest?.Job.Storage?.SecondaryFolder is { Length: > 0 } || latest?.Job.TemporaryFolder is not null)
                {
                    copy = "Not completed";
                }

                databases.Add(new DatabaseOverview(database, lastSuccess?.CompletedUtc ?? lastSuccess?.CreatedUtc,
                    lastSuccess?.Type, result?.Backup, result?.Verification, copy,
                    result?.Message ?? "No execution evidence for this database on the saved server."));
            }
        }

        return new OperationalSnapshot(overviews.ToArray(), databases.ToArray());
    }
}
