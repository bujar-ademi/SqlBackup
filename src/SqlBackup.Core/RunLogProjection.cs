namespace SqlBackup.Core;

public static class RunLogProjection
{
    public static RunLogEntry[] Changes(BackupRun? previous, BackupRun current, DateTimeOffset observedUtc)
    {
        var entries = new List<RunLogEntry>();
        if (previous?.Status != current.Status)
        {
            entries.Add(new RunLogEntry(observedUtc, "Job", current.Status.ToString(), current.Message));
        }

        if (previous is not null && current.PreflightAttempts > previous.PreflightAttempts)
        {
            entries.Add(new RunLogEntry(observedUtc, "Connection", "Running",
                $"Starting read-only SQL inspection, attempt {current.PreflightAttempts}."));
        }

        if (previous?.Message != current.Message && current.Message.StartsWith("SQL inspection complete", StringComparison.Ordinal))
        {
            entries.Add(new RunLogEntry(observedUtc, "Connection", "Succeeded", current.Message));
        }

        foreach (var result in current.Databases)
        {
            var old = previous?.Databases.SingleOrDefault(item => item.Database == result.Database);
            if (old?.Backup != result.Backup)
            {
                entries.Add(Stage(result, "Backup", result.Backup, observedUtc));
            }

            if (old?.SizeBytes != result.SizeBytes && result.SizeBytes.HasValue && result.Backup == StageStatus.Succeeded)
            {
                entries.Add(new RunLogEntry(observedUtc, "Artifact", "Succeeded",
                    $"Recorded backup payload size: {DisplaySize.Format(result.SizeBytes)}.", result.Database));
            }

            if (old?.Verification != result.Verification)
            {
                entries.Add(Stage(result, "VERIFYONLY", result.Verification, observedUtc));
            }

            foreach (var copy in result.Copies ?? [])
            {
                var prior = old?.Copies?.SingleOrDefault(item => item.Path == copy.Path);
                if (prior?.Status != copy.Status)
                {
                    entries.Add(new RunLogEntry(observedUtc, "Copy", copy.Status.ToString(), copy.Message, result.Database));
                }
            }

            if (old?.StagingCleanup != result.StagingCleanup && result.StagingCleanup.HasValue)
            {
                entries.Add(new RunLogEntry(observedUtc, "Temporary cleanup", result.StagingCleanup.ToString()!,
                    result.StagingCleanup switch
                    {
                        StageStatus.Succeeded => "Temporary backup removed after final copy evidence was persisted.",
                        StageStatus.Running => "Checking final copies before removing the temporary backup.",
                        StageStatus.Skipped => "Temporary backup retained; backup, verification or required copy evidence is incomplete.",
                        _ => "Temporary cleanup could not be confirmed. Inspect staging; recorded final copies remain available."
                    }, result.Database));
            }

            foreach (var transfer in result.DestinationTransfers ?? [])
            {
                var prior = old?.DestinationTransfers?.SingleOrDefault(item => item.DestinationId == transfer.DestinationId);
                if (prior?.Status != transfer.Status || prior.Attempts != transfer.Attempts)
                {
                    entries.Add(new RunLogEntry(observedUtc, "Destination", transfer.Status.ToString(), transfer.Message, result.Database));
                }
            }
        }

        return entries.ToArray();
    }

    public static RunLogEntry[] FromSnapshot(BackupRun run)
    {
        var entries = new List<RunLogEntry>();
        if (run.StartedUtc.HasValue)
        {
            entries.Add(new RunLogEntry(run.StartedUtc, "Job", "Running", "Recorded execution start."));
        }

        // Older histories have final stage evidence but no observed transition timestamps. Do not invent times.
        foreach (var result in run.Databases)
        {
            entries.Add(Stage(result, "Backup", result.Backup, null));
            entries.Add(Stage(result, "VERIFYONLY", result.Verification, null));
            foreach (var copy in result.Copies ?? [])
            {
                entries.Add(new RunLogEntry(null, "Copy", copy.Status.ToString(), copy.Message, result.Database));
            }

            foreach (var transfer in result.DestinationTransfers ?? [])
            {
                entries.Add(new RunLogEntry(null, "Destination", transfer.Status.ToString(), transfer.Message, result.Database));
            }
        }

        entries.Add(new RunLogEntry(run.CompletedUtc, "Job", run.Status.ToString(), run.Message));
        return entries.ToArray();
    }

    private static RunLogEntry Stage(DatabaseBackupResult result, string stage, StageStatus status, DateTimeOffset? time)
    {
        string message = $"{stage}: {status}.";
        if (status is StageStatus.Failed or StageStatus.Unknown)
        {
            message += " " + result.Message;
        }
        else if (stage == "Backup" && status == StageStatus.Succeeded)
        {
            message += $" Payload {DisplaySize.Format(result.SizeBytes)} · {result.ArtifactPath}";
        }

        return new RunLogEntry(time, stage, status.ToString(), message, result.Database);
    }
}
