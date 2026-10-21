namespace SqlBackup.Core;

public sealed record ScheduleStatus(Guid JobId, int JobRevision, DateTimeOffset CheckedUtc, string Message);
