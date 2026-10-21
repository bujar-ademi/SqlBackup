using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlBackup.Core;
using SqlBackup.Infrastructure;
using System.Threading.Channels;

namespace SqlBackup.Service;

public sealed class BackupCoordinator(
    ConfigurationStore configurationStore,
    JobStore jobs,
    RunStore runs,
    ConnectionSecrets secrets,
    ISqlBackupEngine engine,
    ILogger<BackupCoordinator> logger) : BackgroundService
{
    private readonly Channel<BackupRun> queue = Channel.CreateBounded<BackupRun>(16);
    private readonly SemaphoreSlim enqueueLock = new(1, 1);
    private readonly HashSet<Guid> activeJobs = [];
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsMaintenanceActive => File.Exists(Path.Combine(configurationStore.DirectoryPath, "setup-maintenance.json"));

    public async Task BeginMaintenanceAsync(CancellationToken cancellationToken)
    {
        await ready.Task.WaitAsync(cancellationToken);
        await enqueueLock.WaitAsync(cancellationToken);
        try
        {
            if (activeJobs.Count > 0)
            {
                throw new ArgumentException("A backup is queued or running. Wait for all jobs to finish before installing or uninstalling.");
            }

            await File.WriteAllTextAsync(Path.Combine(configurationStore.DirectoryPath, "setup-maintenance.json"),
                "{\"Maintenance\":true}", cancellationToken);
        }
        finally
        {
            enqueueLock.Release();
        }
    }

    public async Task CheckSetupReadinessAsync(CancellationToken cancellationToken)
    {
        await ready.Task.WaitAsync(cancellationToken);
        foreach (var job in await jobs.ListAsync(cancellationToken))
        {
            var snapshot = await secrets.ReadSnapshotAsync(job.Id, cancellationToken);
            _ = ConnectionSecrets.UnprotectPassword(snapshot);
            var access = await new StorageSecrets(configurationStore).PrepareAsync(job, null, cancellationToken);
            StorageSecrets.ValidateSnapshot(job, access);
            foreach (var credential in new[] { access.Source, access.Destination, access.Secondary })
            {
                if (credential is not null)
                {
                    byte[] plaintext = System.Security.Cryptography.ProtectedData.Unprotect(credential.ProtectedPassword,
                        null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }
    }

    public async Task<Guid> EnqueueAsync(
        Guid jobId,
        CancellationToken cancellationToken,
        int? expectedRevision = null,
        ConnectionConfiguration? expectedConnection = null,
        BackupType type = BackupType.Full,
        DateTimeOffset? scheduledForUtc = null)
    {
        await ready.Task.WaitAsync(cancellationToken);
        await enqueueLock.WaitAsync(cancellationToken);
        try
        {
            if (IsMaintenanceActive)
            {
                throw new ArgumentException("Installation maintenance is active. Finish setup or recover the pending upgrade before starting backups.");
            }

            string? occurrenceKey = scheduledForUtc.HasValue
                ? $"{jobId:N}/{expectedRevision}/{type}/{scheduledForUtc.Value:O}"
                : null;
            if (occurrenceKey is not null)
            {
                Guid? existing = await runs.FindOccurrenceAsync(occurrenceKey, cancellationToken);
                if (existing.HasValue)
                {
                    return existing.Value;
                }
            }

            if (activeJobs.Contains(jobId))
            {
                throw new ArgumentException("This job already has a queued or running execution.");
            }

            var job = await jobs.GetAsync(jobId, cancellationToken);
            if (expectedRevision.HasValue && job.Revision != expectedRevision.Value)
            {
                throw new ArgumentException("The saved job changed. Reload and review it before running.");
            }

            if (!job.Enabled)
            {
                throw new ArgumentException("Enable the job before running it.");
            }

            if (scheduledForUtc.HasValue && job.Schedule?.Enabled != true)
            {
                throw new ArgumentException("This schedule is no longer enabled.");
            }

            if (scheduledForUtc.HasValue && type == BackupType.Full)
            {
                job = job with { CopyOnly = false };
            }

            JobValidation.Validate(job);
            var connection = await secrets.ReadSnapshotAsync(job.Id, cancellationToken);
            if (expectedConnection is not null && connection.Configuration != expectedConnection)
            {
                throw new ArgumentException("This job's SQL connection changed. Reload and review the server before running.");
            }

            var run = new BackupRun(Guid.NewGuid(), job, connection.Configuration, DateTimeOffset.UtcNow,
                RunStatus.Queued,
                job.Databases.Select(database => new DatabaseBackupResult(database,
                    StageStatus.Pending, job.Verify ? StageStatus.Pending : StageStatus.Skipped)).ToArray(),
                $"Queued for {type} backup.", Type: type, ScheduledForUtc: scheduledForUtc, OccurrenceKey: occurrenceKey);

            // One reader serializes all executions, preventing database overlap across jobs in this service.
            if (activeJobs.Count >= 16)
            {
                throw new ArgumentException("The execution queue is full. Wait for an active job to finish.");
            }

            var storageAccess = await new StorageSecrets(configurationStore).PrepareAsync(job, null, cancellationToken);
            await runs.AddAsync(run, connection.ProtectedPassword, cancellationToken, storageAccess);
            activeJobs.Add(jobId);
            if (!queue.Writer.TryWrite(run))
            {
                activeJobs.Remove(jobId);
                await runs.UpdateAsync(run with
                {
                    Status = RunStatus.Interrupted,
                    CompletedUtc = DateTimeOffset.UtcNow,
                    Message = "The service stopped before execution could begin."
                }, CancellationToken.None);
                throw new ArgumentException("The service is stopping; the run was not started.");
            }

            return run.Id;
        }
        finally
        {
            enqueueLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await configurationStore.InitializeAsync(stoppingToken);
            await runs.ReconcileInterruptedAsync(stoppingToken);
            ready.TrySetResult();
            await foreach (var run in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ExecutionGate.Instance.WaitAsync(stoppingToken);
                    try
                    {
                        await ExecuteRunAsync(run, stoppingToken);
                    }
                    finally
                    {
                        ExecutionGate.Instance.Release();
                    }
                }
                finally
                {
                    await enqueueLock.WaitAsync(CancellationToken.None);
                    activeJobs.Remove(run.Job.Id);
                    enqueueLock.Release();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Backup worker stopped. EventCode={EventCode}", "BackupWorkerStopped");
        }
        catch (Exception exception)
        {
            ready.TrySetException(new InvalidOperationException("Backup history initialization failed."));
            logger.LogCritical("Backup worker failed. EventCode={EventCode} ErrorType={ErrorType}",
                "BackupWorkerFailed", exception.GetType().Name);
            throw;
        }
        finally
        {
            queue.Writer.TryComplete();
        }
    }

    private async Task ExecuteRunAsync(BackupRun queued, CancellationToken cancellationToken)
    {
        var run = queued with { Status = RunStatus.Running, StartedUtc = DateTimeOffset.UtcNow, Message = "Checking SQL prerequisites." };
        await runs.UpdateAsync(run, cancellationToken);
        try
        {
            var connection = await runs.ReadConnectionAsync(run, cancellationToken);
            var storageAccess = await new StorageSecrets(configurationStore).ReadRunAsync(run.Id, cancellationToken);
            ServerInspection inspection;
            while (true)
            {
                run = run with { PreflightAttempts = run.PreflightAttempts + 1 };
                await runs.UpdateAsync(run, cancellationToken);
                try
                {
                    inspection = await engine.InspectAsync(connection, cancellationToken);
                    break;
                }
                catch (Exception exception) when (run.PreflightAttempts < 3 && PreflightRetry.IsTransient(exception))
                {
                    logger.LogWarning("Retrying read-only SQL inspection. EventCode={EventCode}", "PreflightRetry");
                    await Task.Delay(TimeSpan.FromSeconds(run.PreflightAttempts), cancellationToken);
                }
            }
            run = run with { Message = $"SQL inspection complete · SQL {inspection.Version} · {inspection.Edition}." };
            await runs.UpdateAsync(run, cancellationToken);
            if (run.Job.Compression && !inspection.SupportsCompression)
            {
                throw new ArgumentException("This SQL Server edition does not support backup compression. Disable compression for this job.");
            }

            foreach (string database in run.Job.Databases)
            {
                var info = inspection.Databases.SingleOrDefault(item => item.Name == database);
                if (info is null || (run.Type == BackupType.Log ? !info.CanBackUpLog : !info.CanBackUp))
                {
                    throw new ArgumentException("A selected database is unavailable or lacks BACKUP DATABASE permission. Inspect the connection and database selection.");
                }
            }

            for (int index = 0; index < run.Job.Databases.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string database = run.Job.Databases[index];
                string path = BackupSql.CreateArtifactPath(run.Job, database, run.Id, run.CreatedUtc, run.Type);
                var result = await engine.ExecuteAsync(connection, run.Job, database, path,
                    async progress =>
                    {
                        run.Databases[index] = progress;
                        run = run with { Message = "Executing database backup stages." };
                        await runs.UpdateAsync(run, cancellationToken);
                    }, cancellationToken, run.Type);
                run.Databases[index] = result;
                await runs.UpdateAsync(run, cancellationToken);
                if (result.Backup == StageStatus.Succeeded)
                {
                    var destinations = new List<string>();
                    if (run.Job.TemporaryFolder is not null)
                    {
                        destinations.Add(run.Job.Destination);
                    }

                    if (run.Job.Storage?.SecondaryFolder is { Length: > 0 } secondaryFolder)
                    {
                        destinations.Add(secondaryFolder);
                    }

                    result = result with
                    {
                        Copies = destinations.Select(folder => new ArtifactCopy(folder,
                            Path.Combine(folder, Path.GetFileName(path)), StageStatus.Running, "Copying to final destination.")).ToArray()
                    };
                    run.Databases[index] = result;
                    await runs.UpdateAsync(run, cancellationToken);
                }

                var transfer = new ArtifactTransfer();
                result = await transfer.ProcessAsync(run.Job, result, cancellationToken, storageAccess);
                run.Databases[index] = result;
                await runs.UpdateAsync(run, cancellationToken);
                // Final copy evidence is durable BEFORE any temporary artifact may be removed.
                if (ArtifactTransfer.CanCleanupStaging(run.Job, result))
                {
                    result = result with { StagingCleanup = StageStatus.Running };
                    run.Databases[index] = result;
                    await runs.UpdateAsync(run, cancellationToken);
                }

                result = await transfer.CleanupStagingAsync(run.Job, result, cancellationToken, storageAccess);
                run.Databases[index] = result;
                await runs.UpdateAsync(run, cancellationToken);
            }

            bool failed = run.Databases.Any(result => result.Backup != StageStatus.Succeeded
                || (run.Job.Verify && result.Verification != StageStatus.Succeeded)
                || result.Copies?.Any(copy => copy.Status != StageStatus.Succeeded) == true
                || result.StagingCleanup == StageStatus.Failed);
            run = run with
            {
                Status = failed ? RunStatus.Failed : RunStatus.Succeeded,
                CompletedUtc = DateTimeOffset.UtcNow,
                Message = failed ? "One or more backup, verification or copy stages failed. Inspect database results."
                    : $"All {run.Type} backup stages completed. Restore has not been tested."
            };
        }
        catch (OperationCanceledException)
        {
            run = MarkInterrupted(run);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Backup run failed. EventCode={EventCode} ErrorType={ErrorType}",
                "BackupRunFailed", exception.GetType().Name);
            var databases = run.Databases.Select(result => result with
            {
                Backup = result.Backup == StageStatus.Running ? StageStatus.Unknown : result.Backup,
                Verification = result.Verification == StageStatus.Running ? StageStatus.Unknown : result.Verification,
                StagingCleanup = result.StagingCleanup == StageStatus.Running ? StageStatus.Unknown : result.StagingCleanup,
                Copies = result.Copies?.Select(copy => copy.Status == StageStatus.Running
                    ? copy with { Status = StageStatus.Unknown, Message = "Transfer interrupted; inspect partial output." }
                    : copy).ToArray()
            }).ToArray();
            run = run with
            {
                Status = RunStatus.Failed,
                CompletedUtc = DateTimeOffset.UtcNow,
                Databases = databases,
                Message = SqlFailure.Describe(exception)
            };
        }

        // Persist the last known outcomes even if shutdown has been requested.
        await runs.UpdateAsync(run, CancellationToken.None);
    }

    private static BackupRun MarkInterrupted(BackupRun run)
    {
        var databases = run.Databases.Select(result => result with
        {
            Backup = result.Backup == StageStatus.Running ? StageStatus.Unknown : result.Backup,
            Verification = result.Verification == StageStatus.Running ? StageStatus.Unknown : result.Verification,
            StagingCleanup = result.StagingCleanup == StageStatus.Running ? StageStatus.Unknown : result.StagingCleanup,
            Copies = result.Copies?.Select(copy => copy.Status == StageStatus.Running
                ? copy with { Status = StageStatus.Unknown, Message = "Transfer interrupted; inspect partial output." }
                : copy).ToArray()
        }).ToArray();
        return run with
        {
            Status = RunStatus.Interrupted,
            CompletedUtc = DateTimeOffset.UtcNow,
            Databases = databases,
            Message = "Service stopping. Completed stages are retained; uncertain SQL outcomes need manual inspection."
        };
    }
}
