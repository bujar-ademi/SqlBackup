namespace SqlBackup.Core;

public sealed record BackupJob(
    Guid Id,
    string Name,
    string[] Databases,
    string Destination,
    bool Enabled = true,
    bool Checksum = true,
    bool Verify = true,
    bool Compression = false,
    bool CopyOnly = true,
    int Revision = 0,
    ConnectionConfiguration? Connection = null,
    BackupSchedule? Schedule = null,
    StoragePolicy? Storage = null,
    JobNotificationPolicy? Notifications = null,
    string? TemporaryFolder = null,
    BackupDestination[]? Destinations = null);
