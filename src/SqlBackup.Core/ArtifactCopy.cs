namespace SqlBackup.Core;

public sealed record ArtifactCopy(string Folder, string Path, StageStatus Status,
    string Message, string? Sha256 = null);
