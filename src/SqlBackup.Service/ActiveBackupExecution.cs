namespace SqlBackup.Service;

internal sealed class ActiveBackupExecution : IDisposable
{
    private readonly CancellationTokenSource stopSource = new();

    public CancellationToken StopToken => stopSource.Token;
    public Task? StopRequest { get; private set; }

    public void RequestStop()
    {
        StopRequest ??= stopSource.CancelAsync();
    }

    public void Dispose()
    {
        stopSource.Dispose();
    }
}
