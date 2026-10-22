namespace SqlBackup.Core;

public sealed record ServiceRequest(
    string Operation,
    ConnectionConfiguration? Configuration = null,
    string? Password = null,
    BackupJob? Job = null,
    Guid? JobId = null,
    Guid? RunId = null,
    string? PreviewToken = null,
    HistoryQuery? History = null,
    string? NotificationKey = null,
    StoragePasswords? StoragePasswords = null,
    DestinationPassword[]? DestinationPasswords = null);
