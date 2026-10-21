namespace SqlBackup.Infrastructure;

public sealed record StorageAccessSnapshot(
    ShareCredentialSnapshot? Destination = null,
    ShareCredentialSnapshot? Secondary = null,
    ShareCredentialSnapshot? Source = null);
