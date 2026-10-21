namespace SqlBackup.Core;

public sealed record ScheduledOccurrence(BackupType Type, DateTimeOffset DueUtc);
