using System.Security.Cryptography;
using System.Text;

namespace SqlBackup.Core;

public static class BackupDestinations
{
    public static BackupDestination[] Read(BackupJob job)
    {
        if (job.Destinations is not null)
        {
            return job.Destinations;
        }

        var destinations = new List<BackupDestination>();
        if (!string.IsNullOrWhiteSpace(job.Destination))
        {
            string primaryFolder = BackupLocations.PrimaryFolder(job);
            string? userName = job.TemporaryFolder is null ? job.Storage?.SourceUserName : job.Storage?.DestinationUserName;
            destinations.Add(new BackupDestination(LegacyId(job.Id, "primary"), "Primary folder",
                DestinationKind.Folder, primaryFolder, UserName: userName,
                RetentionDays: job.Storage?.PrimaryRetentionDays ?? 0));
        }

        if (!string.IsNullOrWhiteSpace(job.Storage?.SecondaryFolder))
        {
            destinations.Add(new BackupDestination(LegacyId(job.Id, "secondary"), "Secondary folder",
                DestinationKind.Folder, job.Storage.SecondaryFolder, UserName: job.Storage.SecondaryUserName,
                RetentionDays: job.Storage.SecondaryRetentionDays));
        }

        return destinations.ToArray();
    }

    private static Guid LegacyId(Guid jobId, string role)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{jobId:N}/{role}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
