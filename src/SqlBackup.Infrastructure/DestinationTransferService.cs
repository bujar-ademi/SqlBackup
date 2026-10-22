using System.Security.Cryptography;
using SqlBackup.Core;

namespace SqlBackup.Infrastructure;

public sealed class DestinationTransferService(DestinationStorage storage)
{
    public async Task<DatabaseBackupResult> ProcessAsync(BackupRun run, DatabaseBackupResult result,
        DestinationCredentialSnapshot[] credentials, StorageAccessSnapshot sourceAccess,
        Func<DatabaseBackupResult, Task> reportAsync, CancellationToken cancellationToken)
    {
        if (result.Backup != StageStatus.Succeeded || result.ArtifactPath is null
            || (run.Job.Verify && result.Verification != StageStatus.Succeeded))
        {
            return result;
        }

        var destinations = run.Job.Destinations ?? throw new ArgumentException("This execution has no destination list.");
        string fileName = Path.GetFileName(result.ArtifactPath);
        string sourcePath = ArtifactFiles.Resolve(run.ServiceOutputFolder
            ?? throw new IOException("The execution source mapping is missing."), fileName);
        ShareCredentialSnapshot? sourceCredential = run.DirectDestinationId.HasValue ? null : sourceAccess.Source;
        await using var input = await ShareAccess.RunAsync(sourceCredential, () =>
            Task.FromResult(new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan)));
        if (input.Length == 0)
        {
            throw new IOException("The source backup is empty. It will not be uploaded or removed.");
        }

        string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
        if (result.Sha256 is not null && result.Sha256 != hash)
        {
            throw new IOException("The source backup changed. Transfers cannot be retried safely.");
        }

        var transfers = result.DestinationTransfers?.ToList() ?? destinations.Select(destination =>
            new DestinationTransfer(destination.Id, StageStatus.Pending,
                StorageObjectPaths.PathFor(destination, StorageObjectPaths.Key(run, fileName, destination)),
                "Waiting for transfer.")).ToList();
        result = result with { Sha256 = hash, DestinationTransfers = transfers.ToArray() };
        await reportAsync(result);
        foreach (var destination in destinations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = transfers.FindIndex(item => item.DestinationId == destination.Id);
            var transfer = transfers[index];
            if (transfer.Status == StageStatus.Succeeded)
            {
                try
                {
                    var priorAccess = credentials.Single(item => item.Destination.Id == destination.Id);
                    DestinationSecrets.Validate(destination, priorAccess);
                    await storage.For(destination.Kind).ConfirmAsync(priorAccess, Receipt(transfer), cancellationToken);
                    continue;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    transfer = transfer with { Status = StageStatus.Failed, Message = "The prior receipt could not be reconfirmed. Retrying from the retained source." };
                }
            }

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                transfer = transfer with
                {
                    Status = StageStatus.Running,
                    Attempts = transfer.Attempts + 1,
                    Message = $"Transferring to {destination.Name}."
                };
                transfers[index] = transfer;
                result = result with { DestinationTransfers = transfers.ToArray() };
                await reportAsync(result);
                try
                {
                    var access = credentials.SingleOrDefault(item => item.Destination.Id == destination.Id)
                        ?? throw new IOException("This run has no protected credentials for the destination.");
                    DestinationSecrets.Validate(destination, access);
                    StorageObjectReceipt receipt;
                    if (run.DirectDestinationId == destination.Id)
                    {
                        receipt = new StorageObjectReceipt(sourcePath, input.Length, hash);
                    }
                    else
                    {
                        input.Position = 0;
                        receipt = await storage.For(destination.Kind).UploadAsync(access, input,
                            StorageObjectPaths.Key(run, fileName, destination), hash, cancellationToken);
                    }

                    transfer = transfer with
                    {
                        Status = StageStatus.Succeeded,
                        Path = receipt.Path,
                        Sha256 = receipt.Sha256,
                        SizeBytes = receipt.SizeBytes,
                        EntityTag = receipt.EntityTag,
                        VersionId = receipt.VersionId,
                        Message = $"{destination.Name}: transfer confirmed with integrity checks."
                    };
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    transfer = transfer with { Status = StageStatus.Failed, Message = DestinationFailure.Describe(exception) };
                    transfers[index] = transfer;
                    result = result with { DestinationTransfers = transfers.ToArray() };
                    await reportAsync(result);
                    if (attempt < 3 && DestinationFailure.IsTransient(exception))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
                        continue;
                    }
                }

                transfers[index] = transfer;
                result = result with { DestinationTransfers = transfers.ToArray() };
                await reportAsync(result);
                break;
            }
        }

        return result;
    }

    public async Task<DatabaseBackupResult> CleanupAsync(BackupRun run, DatabaseBackupResult result,
        DestinationCredentialSnapshot[] credentials, StorageAccessSnapshot sourceAccess,
        Func<DatabaseBackupResult, Task> reportAsync, CancellationToken cancellationToken)
    {
        if (run.DirectDestinationId.HasValue)
        {
            return result;
        }

        bool allConfirmed = result.Backup == StageStatus.Succeeded && result.Sha256 is not null
            && (!run.Job.Verify || result.Verification == StageStatus.Succeeded)
            && result.DestinationTransfers?.Length == run.Job.Destinations?.Length
            && result.DestinationTransfers?.All(item => item.Status == StageStatus.Succeeded && item.Sha256 == result.Sha256) == true;
        if (!allConfirmed)
        {
            return result with { StagingCleanup = StageStatus.Skipped };
        }

        result = result with { StagingCleanup = StageStatus.Running };
        // All destination receipts have already been committed by the caller's report callback.
        await reportAsync(result);
        try
        {
            foreach (var transfer in result.DestinationTransfers!)
            {
                var access = credentials.Single(item => item.Destination.Id == transfer.DestinationId);
                await storage.For(access.Destination.Kind).ConfirmAsync(access, Receipt(transfer), cancellationToken);
            }

            await ShareAccess.RunAsync(sourceAccess.Source, async () =>
            {
                string source = ArtifactFiles.Resolve(run.ServiceOutputFolder!, Path.GetFileName(result.ArtifactPath!));
                await ArtifactDeletion.DeleteVerifiedAsync(source, result.Sha256!, cancellationToken);
                return true;
            });
            return result with { StagingCleanup = StageStatus.Succeeded };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return result with
            {
                StagingCleanup = StageStatus.Failed,
                Message = "Destinations are recorded, but temporary cleanup could not be confirmed. Inspect the source and destination access."
            };
        }
    }

    public static StorageObjectReceipt Receipt(DestinationTransfer transfer)
    {
        return new StorageObjectReceipt(transfer.Path,
            transfer.SizeBytes ?? throw new IOException("Destination size evidence is missing."),
            transfer.Sha256 ?? throw new IOException("Destination integrity evidence is missing."),
            transfer.EntityTag, transfer.VersionId);
    }
}
