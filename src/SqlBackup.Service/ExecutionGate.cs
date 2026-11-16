namespace SqlBackup.Service;

public static class ExecutionGate
{
    private static readonly object Sync = new();
    private static TaskCompletionSource? cleanupFinished;
    private static int activeBackups;

    public static async Task EnterBackupAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task waitForCleanup;
            lock (Sync)
            {
                if (cleanupFinished is null)
                {
                    activeBackups++;
                    return;
                }

                waitForCleanup = cleanupFinished.Task;
            }

            await waitForCleanup.WaitAsync(cancellationToken);
        }
    }

    public static void ExitBackup()
    {
        lock (Sync)
        {
            activeBackups--;
        }
    }

    public static bool TryEnterCleanup(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Sync)
        {
            if (activeBackups > 0 || cleanupFinished is not null)
            {
                return false;
            }

            cleanupFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    public static void ExitCleanup()
    {
        lock (Sync)
        {
            cleanupFinished!.SetResult();
            cleanupFinished = null;
        }
    }
}
