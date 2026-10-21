using SqlBackup.Core;

namespace SqlBackup.Infrastructure;

public sealed class OperationsStore(JobStore jobs, RunStore runs)
{
    public async Task<OperationalSnapshot> ReadAsync(Guid? jobId, CancellationToken cancellationToken)
    {
        var savedJobs = await jobs.ListAsync(cancellationToken);
        var history = await runs.ReadOperationalHistoryAsync(savedJobs, jobId, cancellationToken);
        return OperationalProjection.Create(savedJobs, history, jobId, DateTimeOffset.UtcNow);
    }
}
