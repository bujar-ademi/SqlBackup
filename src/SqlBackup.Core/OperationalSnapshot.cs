namespace SqlBackup.Core;

public sealed record OperationalSnapshot(JobOverview[] Jobs, DatabaseOverview[] Databases);
