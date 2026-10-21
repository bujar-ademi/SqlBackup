namespace SqlBackup.Core;

public sealed record RetentionArtifact(
    Guid RunId, string Database, string Folder, string Path,
    BackupType Type, DateTimeOffset CreatedUtc, bool CopyOnly,
    string? DatabaseId, string? RecoveryForkId, string? BackupSetId,
    string? FirstLsn, string? LastLsn, string? CheckpointLsn, string? BaseLsn,
    string? Sha256, bool Verified, string Server);
