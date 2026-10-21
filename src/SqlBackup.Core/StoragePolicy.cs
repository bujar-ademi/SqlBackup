namespace SqlBackup.Core;

public sealed record StoragePolicy(int PrimaryRetentionDays = 0, string? SecondaryFolder = null,
    int SecondaryRetentionDays = 0, string? ServiceSourceFolder = null,
    string? DestinationUserName = null, string? SecondaryUserName = null, string? SourceUserName = null);
