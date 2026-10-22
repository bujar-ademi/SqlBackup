namespace SqlBackup.Infrastructure;

public sealed record StorageObjectReceipt(string Path, long SizeBytes, string Sha256, string? EntityTag = null, string? VersionId = null);
