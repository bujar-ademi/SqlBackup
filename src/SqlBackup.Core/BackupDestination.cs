namespace SqlBackup.Core;

public sealed record BackupDestination(
    Guid Id,
    string Name,
    DestinationKind Kind,
    string Location,
    string Prefix = "",
    string? Region = null,
    string? UserName = null,
    int RetentionDays = 0);
