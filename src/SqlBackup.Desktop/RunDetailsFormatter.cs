using System.Text;
using SqlBackup.Core;

namespace SqlBackup.Desktop;

public static class RunDetailsFormatter
{
    public static string Format(BackupRun run)
    {
        var details = new StringBuilder();
        details.AppendLine($"{run.Job.Name} · {run.Type} · {run.Status}");
        details.AppendLine($"Server: {run.Connection.Server}");
        details.AppendLine($"Job revision: {run.Job.Revision}");
        details.AppendLine($"Started: {run.StartedUtc?.ToLocalTime():g}");
        if (run.StartedUtc.HasValue)
        {
            var elapsed = (run.CompletedUtc ?? DateTimeOffset.UtcNow) - run.StartedUtc.Value;
            details.AppendLine("Duration: " + elapsed.ToString(@"dd\.hh\:mm\:ss"));
        }

        details.AppendLine(run.Message);
        foreach (var database in run.Databases)
        {
            details.AppendLine();
            details.AppendLine(database.Database);
            details.AppendLine($"Backup: {database.Backup} · VERIFYONLY: {database.Verification}");
            details.AppendLine(database.Message);
            details.AppendLine(database.ArtifactPath);
            if (database.StagingCleanup.HasValue)
            {
                details.AppendLine($"Temporary cleanup: {database.StagingCleanup}");
            }
            foreach (var copy in database.Copies ?? [])
            {
                details.AppendLine($"Copy: {copy.Status} · {copy.Path}");
                details.AppendLine(copy.Message);
            }

            if (database.SizeBytes.HasValue)
            {
                details.AppendLine($"Backup payload size: {database.SizeBytes:N0} bytes");
            }
        }

        return details.ToString();
    }
}
