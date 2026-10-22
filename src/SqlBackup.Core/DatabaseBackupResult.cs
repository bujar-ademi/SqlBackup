namespace SqlBackup.Core;

public sealed record DatabaseBackupResult(
    string Database,
    StageStatus Backup,
    StageStatus Verification,
    string? ArtifactPath = null,
    long? SizeBytes = null,
    string? BackupSetId = null,
    string? FirstLsn = null,
    string? LastLsn = null,
    string? CheckpointLsn = null,
    string? DatabaseBackupLsn = null,
    string Message = "Waiting.",
    string? DatabaseId = null,
    string? RecoveryForkId = null,
    bool? HeaderCopyOnly = null,
    string? Sha256 = null,
    ArtifactCopy[]? Copies = null,
    bool? HasSingleRecoveryFork = null,
    BackupType? HeaderType = null,
    StageStatus? StagingCleanup = null,
    DestinationTransfer[]? DestinationTransfers = null);
