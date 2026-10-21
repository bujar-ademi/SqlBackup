namespace SqlBackup.Core;

public enum RunStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Interrupted
}
