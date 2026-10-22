namespace SqlBackup.Core;

public sealed record DestinationTransfer(
    Guid DestinationId,
    StageStatus Status,
    string Path,
    string Message,
    string? Sha256 = null,
    long? SizeBytes = null,
    string? EntityTag = null,
    string? VersionId = null,
    int Attempts = 0);
