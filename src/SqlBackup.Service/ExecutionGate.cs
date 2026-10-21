namespace SqlBackup.Service;

public static class ExecutionGate
{
    // Excludes cleanup from the SQL/copy stages within the single locked host.
    public static SemaphoreSlim Instance { get; } = new(1, 1);
}
