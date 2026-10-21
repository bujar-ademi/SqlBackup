namespace SqlBackup.Core;

public sealed record JobOverview(Guid JobId, RunStatus? LastStatus, DateTimeOffset? LastRunUtc,
    string StatusText, DateTimeOffset? NextRunUtc, string ScheduleMessage);
