namespace SqlBackup.Core;

public sealed record JobNotificationPolicy(bool OnSuccess = false, bool OnFailure = true);
