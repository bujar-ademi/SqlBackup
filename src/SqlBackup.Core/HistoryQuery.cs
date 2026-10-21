namespace SqlBackup.Core;

public sealed record HistoryQuery(Guid? JobId = null, RunStatus? Status = null, BackupType? Type = null,
    string? Database = null, DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null,
    int Offset = 0, int Limit = 20);
