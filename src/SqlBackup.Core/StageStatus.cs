namespace SqlBackup.Core;

public enum StageStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped,
    Unknown
}
