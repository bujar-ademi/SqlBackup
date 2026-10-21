using SqlBackup.Core;

namespace SqlBackup.Infrastructure;

public sealed class ArtifactTransfer
{
    public async Task<DatabaseBackupResult> ProcessAsync(BackupJob job, DatabaseBackupResult result,
        CancellationToken cancellationToken, StorageAccessSnapshot? access = null)
    {
        if ((job.Storage is null && job.TemporaryFolder is null)
            || result.Backup != StageStatus.Succeeded || result.ArtifactPath is null)
        {
            return result;
        }

        access ??= new StorageAccessSnapshot();
        string sourceFolder = BackupLocations.SourceFolder(job);
        string? sourceHash = null;
        var copies = new List<ArtifactCopy>();
        var targets = new List<(string Folder, ShareCredentialSnapshot? Credential)>();
        if (job.TemporaryFolder is not null)
        {
            targets.Add((job.Destination, access.Destination));
        }

        if (!string.IsNullOrWhiteSpace(job.Storage?.SecondaryFolder))
        {
            targets.Add((job.Storage.SecondaryFolder, access.Secondary));
        }

        try
        {
            StorageSecrets.ValidateSnapshot(job, access);
            return await ShareAccess.RunAsync(access.Source, async () =>
            {
                string source = ArtifactFiles.Resolve(sourceFolder, Path.GetFileName(result.ArtifactPath));
                sourceHash = await ArtifactFiles.HashAsync(source, cancellationToken);
                foreach (var target in targets)
                {
                    try
                    {
                        // Open the source with source credentials before switching to the destination identity.
                        await using var input = OpenSource(source);
                        copies.Add(await ShareAccess.RunAsync(target.Credential, () =>
                            CopyStreamAsync(input, Path.GetFileName(source), target.Folder, sourceHash, cancellationToken)));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                        or System.Security.Cryptography.CryptographicException)
                    {
                        copies.Add(FailedCopy(target.Folder, result.ArtifactPath));
                    }
                }

                return result with { Sha256 = sourceHash, Copies = copies.ToArray() };
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException)
        {
            return result with
            {
                Sha256 = sourceHash,
                Copies = targets.Select(target => FailedCopy(target.Folder, result.ArtifactPath)).ToArray(),
                Message = result.Message + " Service filesystem access failed; copying/cleanup cannot be confirmed."
            };
        }
    }

    private static ArtifactCopy FailedCopy(string folder, string source)
    {
        return new ArtifactCopy(folder, Path.Combine(folder, Path.GetFileName(source)), StageStatus.Failed,
            "Copy failed. Temporary backup is retained. Check source/share access, credentials, free space and existing files.");
    }

    public async Task<DatabaseBackupResult> CleanupStagingAsync(BackupJob job, DatabaseBackupResult result,
        CancellationToken cancellationToken, StorageAccessSnapshot? access = null)
    {
        if (!CanCleanupStaging(job, result))
        {
            return job.TemporaryFolder is null ? result : result with { StagingCleanup = StageStatus.Skipped };
        }

        try
        {
            StorageSecrets.ValidateSnapshot(job, access ?? new StorageAccessSnapshot());
            foreach (var copy in result.Copies!)
            {
                var credential = copy.Folder.Equals(job.Destination, StringComparison.OrdinalIgnoreCase)
                    ? access?.Destination : access?.Secondary;
                string hash = await ShareAccess.RunAsync(credential, () => ArtifactFiles.HashAsync(copy.Path, cancellationToken));
                if (hash != result.Sha256)
                {
                    throw new IOException("Final backup changed before temporary cleanup.");
                }
            }

            await ShareAccess.RunAsync(access?.Source, async () =>
            {
                string source = ArtifactFiles.Resolve(BackupLocations.SourceFolder(job), Path.GetFileName(result.ArtifactPath!));
                await ArtifactDeletion.DeleteVerifiedAsync(source, result.Sha256!, cancellationToken);
                return true;
            });
            return result with { StagingCleanup = StageStatus.Succeeded };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException)
        {
            return result with
            {
                StagingCleanup = StageStatus.Failed,
                Message = result.Message + " Final copies are complete, but temporary backup cleanup failed. Inspect the staging folder."
            };
        }
    }

    public static bool CanCleanupStaging(BackupJob job, DatabaseBackupResult result)
    {
        if (job.TemporaryFolder is null || result.Backup != StageStatus.Succeeded || result.ArtifactPath is null
            || (job.Verify && result.Verification != StageStatus.Succeeded) || result.Sha256 is null)
        {
            return false;
        }

        var required = new List<string> { job.Destination };
        if (!string.IsNullOrWhiteSpace(job.Storage?.SecondaryFolder))
        {
            required.Add(job.Storage.SecondaryFolder);
        }

        return required.All(folder => result.Copies?.Any(copy =>
            copy.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)
            && copy.Status == StageStatus.Succeeded && copy.Sha256 == result.Sha256
            && copy.Path.Equals(Path.Combine(folder, Path.GetFileName(result.ArtifactPath)), StringComparison.OrdinalIgnoreCase)) == true);
    }

    public async Task<ArtifactCopy> CopyAsync(string source, string folder, string expectedHash,
        CancellationToken cancellationToken)
    {
        ArtifactFiles.EnsureNoLinks(source);
        await using var input = OpenSource(source);
        return await CopyStreamAsync(input, Path.GetFileName(source), folder, expectedHash, cancellationToken);
    }

    private static FileStream OpenSource(string source)
    {
        return new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static async Task<ArtifactCopy> CopyStreamAsync(FileStream input, string fileName, string folder,
        string expectedHash, CancellationToken cancellationToken)
    {
        string destination = ArtifactFiles.Resolve(folder, fileName);
        string partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        // Partial output is never promoted on restart; existing targets are never overwritten.
        await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(true);
        }

        string copiedHash = await ArtifactFiles.HashAsync(partial, cancellationToken);
        if (copiedHash != expectedHash)
        {
            throw new IOException("Copy integrity check failed; partial output retained for inspection.");
        }

        ArtifactFiles.EnsureNoLinks(destination);
        File.Move(partial, destination, overwrite: false);
        return new ArtifactCopy(folder, destination, StageStatus.Succeeded,
            "Copy completed and SHA-256 matched the source.", copiedHash);
    }
}
