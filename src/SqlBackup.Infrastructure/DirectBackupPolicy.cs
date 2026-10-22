using SqlBackup.Core;

namespace SqlBackup.Infrastructure;

public static class DirectBackupPolicy
{
    public static bool IsCandidate(BackupJob job)
    {
        // A source mapping may refer to a different SQL host or filesystem namespace.
        return job.TemporaryFolder is not null
            && job.Destination.Length >= 3
            && char.IsAsciiLetter(job.Destination[0])
            && job.Destination[1] == ':'
            && job.Destination[2] == '\\'
            && job.Storage?.ServiceSourceFolder is null
            && job.Storage?.SourceUserName is null
            && job.Storage?.DestinationUserName is null;
    }

    public static bool MatchesLocalServer(
        string? machineName, string? physicalMachineName, bool clustered, bool folderVisible, string serviceMachineName)
    {
        return !clustered && folderVisible
            && string.Equals(machineName, serviceMachineName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(physicalMachineName, serviceMachineName, StringComparison.OrdinalIgnoreCase);
    }
}
