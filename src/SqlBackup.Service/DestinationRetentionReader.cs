using SqlBackup.Core;
using SqlBackup.Infrastructure;

namespace SqlBackup.Service;

public sealed class DestinationRetentionReader(ConfigurationStore configuration, DestinationStorage storage,
    ConnectionSecrets secrets, IBackupHistoryInspector historyInspector)
{
    public async Task<CleanupDecision[]> ReadAsync(BackupJob job, BackupDestination destination,
        BackupRun[] history, HashSet<string> deleted, CancellationToken cancellationToken)
    {
        var artifacts = new List<RetentionArtifact>();
        bool uncertain = false;
        if (destination.Kind == DestinationKind.Folder)
        {
            var legacyReader = new RetentionEvidenceReader(secrets, historyInspector, new StorageSecrets(configuration));
            foreach (bool secondary in new[] { false, true })
            {
                var legacyEvidence = await legacyReader.CollectArtifactsAsync(destination.Location, secondary,
                    history, deleted, cancellationToken);
                artifacts.AddRange(legacyEvidence.Artifacts);
                uncertain = uncertain || legacyEvidence.Uncertain;
            }
        }

        foreach (var run in history.Where(item => item.Job.Destinations is not null))
        {
            foreach (var historicalDestination in run.Job.Destinations!.Where(item => SameLocation(item, destination)))
            {
                foreach (var result in run.Databases)
                {
                    if (result.ArtifactPath is null || result.Backup != StageStatus.Succeeded)
                    {
                        continue;
                    }

                    var transfer = result.DestinationTransfers?.SingleOrDefault(item => item.DestinationId == historicalDestination.Id);
                    if (transfer is null)
                    {
                        uncertain = true;
                        continue;
                    }

                    if (deleted.Contains(transfer.Path))
                    {
                        continue;
                    }

                    string expectedPath = StorageObjectPaths.PathFor(historicalDestination,
                        StorageObjectPaths.Key(run, Path.GetFileName(result.ArtifactPath), historicalDestination));
                    string? expectedSqlPath = null;
                    if (run.SqlOutputFolder is not null)
                    {
                        expectedSqlPath = BackupArtifactPaths.SqlPath(run, result.Database);
                    }

                    bool owned = expectedSqlPath == result.ArtifactPath && transfer.Path == expectedPath && transfer.Sha256 is not null
                        && transfer.Sha256 == result.Sha256 && transfer.SizeBytes.HasValue;
                    bool verified = owned && transfer.Status == StageStatus.Succeeded
                        && result.Verification == StageStatus.Succeeded
                        && result.HasSingleRecoveryFork == true && result.HeaderType == run.Type;
                    artifacts.Add(new RetentionArtifact(run.Id, result.Database, Location(destination), transfer.Path,
                        run.Type, run.CompletedUtc ?? run.CreatedUtc, result.HeaderCopyOnly ?? true,
                        result.DatabaseId, result.RecoveryForkId, result.BackupSetId, result.FirstLsn, result.LastLsn,
                        result.CheckpointLsn, result.DatabaseBackupLsn, transfer.Sha256, verified, run.Connection.Server));
                    if (!owned || transfer.Status != StageStatus.Succeeded)
                    {
                        uncertain = true;
                        continue;
                    }

                    try
                    {
                        var snapshots = await new DestinationSecrets(configuration).ReadRunAsync(run.Id, cancellationToken);
                        var access = snapshots.Single(item => item.Destination.Id == historicalDestination.Id);
                        DestinationSecrets.Validate(historicalDestination, access);
                        await storage.For(historicalDestination.Kind).VerifyAsync(access,
                            DestinationTransferService.Receipt(transfer), cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        uncertain = true;
                    }
                }
            }
        }

        uncertain = uncertain || await HasExternalHistoryAsync(job, artifacts, history, cancellationToken);
        var ownedRuns = history.Where(item => item.Job.Id == job.Id).Select(item => item.Id).ToHashSet();
        return RetentionPlanner.Plan(artifacts.ToArray(), destination.RetentionDays, DateTimeOffset.UtcNow)
            .Where(item => ownedRuns.Contains(item.Artifact.RunId))
            .Select(item => uncertain ? item with
            {
                Delete = false,
                Reason = "Retained: destination ownership, integrity, credentials or SQL backup-chain evidence is incomplete."
            } : item).ToArray();
    }

    public async Task DeleteAsync(BackupRun run, CleanupDecision decision, CancellationToken cancellationToken)
    {
        var transfer = run.Databases.SelectMany(item => item.DestinationTransfers ?? [])
            .Single(item => item.Path == decision.Artifact.Path);
        var destination = run.Job.Destinations!.Single(item => item.Id == transfer.DestinationId);
        var snapshots = await new DestinationSecrets(configuration).ReadRunAsync(run.Id, cancellationToken);
        var access = snapshots.Single(item => item.Destination.Id == destination.Id);
        DestinationSecrets.Validate(destination, access);
        await storage.For(destination.Kind).DeleteAsync(access, DestinationTransferService.Receipt(transfer), cancellationToken);
    }

    private async Task<bool> HasExternalHistoryAsync(BackupJob job, List<RetentionArtifact> artifacts,
        BackupRun[] history, CancellationToken cancellationToken)
    {
        try
        {
            var connection = await secrets.ReadSnapshotAsync(job.Id, cancellationToken);
            foreach (var group in artifacts.GroupBy(item => item.Database))
            {
                var serverHistory = history.Where(item => item.Connection.Server == connection.Configuration.Server).ToArray();
                if (group.Any(item => item.Server != connection.Configuration.Server)
                    || await historyInspector.HasExternalDependenciesAsync(connection, group.Key,
                        serverHistory.Min(item => item.CreatedUtc),
                        serverHistory.SelectMany(item => item.Databases).Where(item => item.Database == group.Key)
                            .Select(item => item.BackupSetId ?? string.Empty).ToArray(), cancellationToken))
                {
                    return true;
                }
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return true;
        }
    }

    private static bool SameLocation(BackupDestination first, BackupDestination second)
    {
        var comparison = first.Kind == DestinationKind.Folder ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return first.Kind == second.Kind && first.Prefix.Trim('/') == second.Prefix.Trim('/')
            && first.Location.TrimEnd('\\', '/').Equals(second.Location.TrimEnd('\\', '/'), comparison);
    }

    private static string Location(BackupDestination destination)
    {
        return destination.Kind == DestinationKind.Folder ? destination.Location
            : $"{destination.Kind}:{destination.Location.TrimEnd('/')}/{destination.Prefix.Trim('/')}";
    }
}
