namespace SqlBackup.Core;

public sealed record DatabaseOverview(string Database, DateTimeOffset? LastBackupUtc, BackupType? LastBackupType,
    StageStatus? Backup, StageStatus? Verification, string CopyStatus, string Message);
