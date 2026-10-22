using SqlBackup.Core;
using SqlBackup.Infrastructure;

namespace SqlBackup.Tests;

public sealed class SimulatedSqlEngine : ISqlBackupEngine
{
    public bool DirectDestinationAvailable { get; set; }
    public Exception? DirectDestinationFailure { get; set; }

    public Task<bool> CanUseDirectDestinationAsync(ConnectionSnapshot connection, BackupJob job, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DirectDestinationFailure is not null)
        {
            throw DirectDestinationFailure;
        }

        return Task.FromResult(DirectDestinationAvailable);
    }

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool BlockExecution { get; set; }
    public bool FailVerification { get; set; }
    public bool FailBackup { get; set; }
    public bool WriteArtifact { get; set; }
    public bool SupportsCompression { get; set; } = true;
    public int ExecutionCount { get; private set; }
    public int PreflightFailures { get; set; }
    public string RecoveryModel { get; set; } = "SIMPLE";
    public List<BackupType> ExecutedTypes { get; } = [];
    public string? ObservedPassword { get; private set; }
    public string? InspectedServer { get; private set; }
    public string? InspectedPassword { get; private set; }

    public Task<ServerInspection> InspectAsync(ConnectionSnapshot connection, CancellationToken cancellationToken)
    {
        if (PreflightFailures > 0)
        {
            PreflightFailures--;
            throw new TimeoutException("Simulated read-only inspection timeout.");
        }

        InspectedServer = connection.Configuration.Server;
        InspectedPassword = ConnectionSecrets.UnprotectPassword(connection);
        return Task.FromResult(new ServerInspection("15.0.test", "Simulated test engine", SupportsCompression,
            "FictionalIdentity", [new DatabaseInfo("TestDatabase", "ONLINE", RecoveryModel, true, "Ready", true)]));
    }

    public async Task<DatabaseBackupResult> ExecuteAsync(
        ConnectionSnapshot connection,
        BackupJob job,
        string database,
        string artifactPath,
        Func<DatabaseBackupResult, Task> reportAsync,
        CancellationToken cancellationToken,
        BackupType type = BackupType.Full)
    {
        ExecutionCount++;
        ExecutedTypes.Add(type);
        ObservedPassword = ConnectionSecrets.UnprotectPassword(connection);
        var result = new DatabaseBackupResult(database, StageStatus.Running, StageStatus.Pending, artifactPath);
        await reportAsync(result);
        Started.TrySetResult();
        if (BlockExecution)
        {
            await Release.Task.WaitAsync(cancellationToken);
        }

        if (FailBackup)
        {
            return result with { Backup = StageStatus.Failed, Verification = StageStatus.Skipped };
        }

        if (WriteArtifact)
        {
            await using var output = new System.IO.FileStream(artifactPath, System.IO.FileMode.CreateNew,
                System.IO.FileAccess.Write, System.IO.FileShare.None);
            await output.WriteAsync("Synthetic backup fixture"u8.ToArray(), cancellationToken);
        }

        result = result with { Backup = StageStatus.Succeeded, SizeBytes = 1024 };
        await reportAsync(result);
        StageStatus verification = StageStatus.Skipped;
        if (job.Verify)
        {
            verification = FailVerification ? StageStatus.Failed : StageStatus.Succeeded;
        }

        return result with
        {
            Verification = verification,
            Message = "Simulated result; no SQL backup was performed."
        };
    }
}
