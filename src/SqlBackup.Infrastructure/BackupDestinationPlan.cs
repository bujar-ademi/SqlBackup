namespace SqlBackup.Infrastructure;

public sealed record BackupDestinationPlan(string SqlFolder, string ServiceFolder, Guid? DirectDestinationId);
