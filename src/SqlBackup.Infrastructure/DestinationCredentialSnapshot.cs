using SqlBackup.Core;

namespace SqlBackup.Infrastructure;

public sealed record DestinationCredentialSnapshot(
    BackupDestination Destination,
    byte[]? ProtectedSecret,
    byte[]? ProtectedSessionToken = null);
