namespace SqlBackup.Core;

public sealed record CleanupAudit(Guid Id, DateTimeOffset CreatedUtc, Guid JobId,
    string Path, string Outcome, string Reason);
