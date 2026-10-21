namespace SqlBackup.Core;

public static class RunPresentation
{
    public static RunSummary Summarize(BackupRun run)
    {
        int backups = run.Databases.Count(item => item.Backup == StageStatus.Succeeded);
        int verificationIssues = run.Databases.Count(item => item.Verification is StageStatus.Failed or StageStatus.Unknown);
        int copyIssues = run.Databases.SelectMany(item => item.Copies ?? [])
            .Count(item => item.Status is StageStatus.Failed or StageStatus.Unknown);
        string stages = $"Backup {backups}/{run.Databases.Length} · Verify issues {verificationIssues} · Copy issues {copyIssues}";
        int cleanupIssues = run.Databases.Count(item => item.StagingCleanup is StageStatus.Failed or StageStatus.Unknown);
        if (cleanupIssues > 0)
        {
            stages += $" · Temporary cleanup issues {cleanupIssues}";
        }
        return new RunSummary(run.Id, run.Job.Id, run.Job.Name, run.CreatedUtc, run.Status, run.Type, stages, backups,
            run.Databases.Any(item => item.SizeBytes.HasValue)
                ? run.Databases.Where(item => item.Backup == StageStatus.Succeeded).Sum(item => item.SizeBytes ?? 0) : null);
    }

    public static NotificationMessage? Notification(BackupRun run)
    {
        var policy = run.Job.Notifications ?? new JobNotificationPolicy();
        bool success = run.Status == RunStatus.Succeeded;
        bool failed = run.Status is RunStatus.Failed or RunStatus.Interrupted;
        if ((!success || !policy.OnSuccess) && (!failed || !policy.OnFailure))
        {
            return null;
        }

        // Use fixed stage counts and opaque IDs; never include names, paths, SQL errors or credentials.
        return new NotificationMessage("run/" + run.Id.ToString("N"), run.CompletedUtc ?? DateTimeOffset.UtcNow,
            run.Job.Id, run.Id, success ? "Information" : "Warning", $"Backup run {run.Status}",
            Summarize(run).Stages + ". Open execution history for details.");
    }
}
