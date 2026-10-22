namespace SqlBackup.Core;

public sealed record BackupRun(
    Guid Id,
    BackupJob Job,
    ConnectionConfiguration Connection,
    DateTimeOffset CreatedUtc,
    RunStatus Status,
    DatabaseBackupResult[] Databases,
    string Message,
    DateTimeOffset? StartedUtc = null,
    DateTimeOffset? CompletedUtc = null,
    BackupType Type = BackupType.Full,
    DateTimeOffset? ScheduledForUtc = null,
    string? OccurrenceKey = null,
    int PreflightAttempts = 0,
    string? SqlOutputFolder = null,
    string? ServiceOutputFolder = null,
    Guid? DirectDestinationId = null,
    bool IsTransferRetry = false);
