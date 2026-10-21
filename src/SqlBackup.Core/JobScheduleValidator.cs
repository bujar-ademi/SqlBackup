namespace SqlBackup.Core;

public static class JobScheduleValidator
{
    public static void Validate(BackupJob job, ServerInspection inspection)
    {
        if (!job.Enabled || job.Schedule?.Enabled != true)
        {
            return;
        }

        ScheduleCalculator.Validate(job.Schedule);
        foreach (string database in job.Databases)
        {
            var info = inspection.Databases.SingleOrDefault(item => item.Name == database);
            if (info?.CanBackUp != true)
            {
                throw new JobValidationException("ScheduleDatabaseUnavailable",
                    $"Database '{database}' is unavailable or lacks BACKUP DATABASE permission. Review Select databases.");
            }

            if (job.Schedule.LogIntervalMinutes > 0)
            {
                if (info.RecoveryModel is not ("FULL" or "BULK_LOGGED"))
                {
                    throw new JobValidationException("ScheduleLogRecoveryRequired",
                        $"Database '{database}' uses {info.RecoveryModel} recovery. Disable Transaction Log in Advanced Schedule, or have your SQL administrator configure a suitable recovery strategy. The application never changes recovery models.");
                }

                if (!info.CanBackUpLog)
                {
                    throw new JobValidationException("ScheduleLogPermissionRequired",
                        $"Database '{database}' lacks BACKUP LOG permission. Disable Transaction Log or grant the configured SQL identity that permission.");
                }
            }

            if (job.Schedule.DifferentialIntervalMinutes > 0 && database.Equals("master", StringComparison.OrdinalIgnoreCase))
            {
                throw new JobValidationException("ScheduleMasterFullOnly",
                    "The master database supports only Full backups. Use a separate Full-only job for master.");
            }
        }
    }
}
