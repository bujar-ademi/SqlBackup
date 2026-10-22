using SqlBackup.Core;
using SqlBackup.Infrastructure;

namespace SqlBackup.Service;

public sealed class RetentionEvidenceReader(ConnectionSecrets secrets, IBackupHistoryInspector historyInspector,
    StorageSecrets? storageSecrets = null)
{
    public async Task<CleanupDecision[]> ReadAsync(BackupJob job, string folder, int days, bool secondary,
        BackupRun[] history, HashSet<string> deleted, CancellationToken cancellationToken)
    {
        var (artifacts, uncertain) = await CollectArtifactsAsync(folder, secondary, history, deleted, cancellationToken);
        uncertain = uncertain || await HasExternalHistoryAsync(job, artifacts, history, cancellationToken);
        var ownedRuns = history.Where(item => item.Job.Id == job.Id).Select(item => item.Id).ToHashSet();
        return RetentionPlanner.Plan(artifacts, days, DateTimeOffset.UtcNow)
            .Where(item => ownedRuns.Contains(item.Artifact.RunId))
            .Select(decision => uncertain ? decision with
            {
                Delete = false,
                Reason = "Retained: location evidence is missing, changed, inaccessible or affected by external/purged SQL history."
            } : decision).ToArray();
    }

    public async Task<(RetentionArtifact[] Artifacts, bool Uncertain)> CollectArtifactsAsync(
        string folder, bool secondary, BackupRun[] history, HashSet<string> deleted, CancellationToken cancellationToken)
    {
        var artifacts = new List<RetentionArtifact>();
        bool uncertain = false;
        foreach (var run in history)
        {
            if (run.Job.Destinations is not null)
            {
                continue;
            }

            string historicalFolder = BackupLocations.PrimaryFolder(run.Job);
            if (secondary)
            {
                historicalFolder = run.Job.Storage?.SecondaryFolder ?? string.Empty;
            }

            if (!SameFolder(folder, historicalFolder))
            {
                continue;
            }

            foreach (var result in run.Databases)
            {
                if (result.ArtifactPath is null)
                {
                    continue;
                }

                string expectedSqlPath = BackupSql.CreateArtifactPath(run.Job, result.Database, run.Id, run.CreatedUtc, run.Type);
                if (!string.Equals(expectedSqlPath, result.ArtifactPath, StringComparison.OrdinalIgnoreCase))
                {
                    uncertain = true;
                    continue;
                }

                string path = Path.GetFullPath(Path.Combine(folder, Path.GetFileName(result.ArtifactPath)));
                if (deleted.Contains(path))
                {
                    continue;
                }

                var copy = result.Copies?.SingleOrDefault(item => SameFolder(item.Folder, folder));
                bool verified = result.Backup == StageStatus.Succeeded && result.Verification == StageStatus.Succeeded
                    && result.HasSingleRecoveryFork == true && result.HeaderType == run.Type;
                string? hash = result.Sha256;
                if (secondary || run.Job.TemporaryFolder is not null)
                {
                    verified = verified && copy?.Status == StageStatus.Succeeded
                        && string.Equals(copy.Path, path, StringComparison.OrdinalIgnoreCase);
                    hash = copy?.Sha256;
                }

                var artifact = new RetentionArtifact(run.Id, result.Database, folder, path, run.Type,
                    run.CompletedUtc ?? run.CreatedUtc, result.HeaderCopyOnly ?? true, result.DatabaseId, result.RecoveryForkId,
                    result.BackupSetId, result.FirstLsn, result.LastLsn, result.CheckpointLsn,
                    result.DatabaseBackupLsn, hash, verified, run.Connection.Server);
                artifacts.Add(artifact);
                try
                {
                    var access = storageSecrets is null ? new StorageAccessSnapshot()
                        : await storageSecrets.ReadRunAsync(run.Id, cancellationToken);
                    StorageSecrets.ValidateSnapshot(run.Job, access);
                    var credential = secondary ? access.Secondary
                        : run.Job.TemporaryFolder is null ? access.Source : access.Destination;
                    string actual = await ShareAccess.RunAsync(credential, () =>
                    {
                        ArtifactFiles.Resolve(folder, Path.GetFileName(path));
                        return ArtifactFiles.HashAsync(path, cancellationToken);
                    });
                    if (actual != hash)
                    {
                        uncertain = true;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or System.Security.Cryptography.CryptographicException)
                {
                    uncertain = true;
                }
            }
        }

        return (artifacts.ToArray(), uncertain);
    }

    private async Task<bool> HasExternalHistoryAsync(BackupJob job, RetentionArtifact[] artifacts,
        BackupRun[] history, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await secrets.ReadSnapshotAsync(job.Id, cancellationToken);
            foreach (var databaseGroup in artifacts.GroupBy(item => item.Database))
            {
                if (databaseGroup.Any(item => !item.Server.Equals(snapshot.Configuration.Server, StringComparison.OrdinalIgnoreCase))
                    || await historyInspector.HasExternalDependenciesAsync(snapshot, databaseGroup.Key,
                        history.Where(item => item.Connection.Server == snapshot.Configuration.Server
                            && item.Databases.Any(result => result.Database == databaseGroup.Key)).Min(item => item.CreatedUtc),
                        history.Where(item => item.Connection.Server == snapshot.Configuration.Server)
                            .SelectMany(item => item.Databases).Where(item => item.Database == databaseGroup.Key)
                            .Select(item => item.BackupSetId ?? string.Empty).ToArray(), cancellationToken))
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return true;
        }

        return false;
    }

    private static bool SameFolder(string first, string second)
    {
        return first.TrimEnd('\\').Equals(second.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }
}
