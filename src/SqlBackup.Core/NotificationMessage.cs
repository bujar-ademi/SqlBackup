namespace SqlBackup.Core;

public sealed record NotificationMessage(string EventKey, DateTimeOffset CreatedUtc, Guid JobId,
    Guid? RunId, string Severity, string Title, string Message, DateTimeOffset? ReadUtc = null);
