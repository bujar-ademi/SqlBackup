namespace SqlBackup.Infrastructure;

public sealed class FolderDestinationStorage : IDestinationStorage
{
    public async Task CheckAccessAsync(DestinationCredentialSnapshot access, CancellationToken cancellationToken)
    {
        DestinationSecrets.Validate(access.Destination, access);
        var job = new SqlBackup.Core.BackupJob(Guid.NewGuid(), "Destination access check", [], access.Destination.Location,
            Storage: new SqlBackup.Core.StoragePolicy(SourceUserName: access.Destination.UserName));
        var checks = await new StorageDiagnostics().CheckAsync(job, cancellationToken,
            new StorageAccessSnapshot(Source: Credential(access)));
        if (checks.Any(item => item.Status == SqlBackup.Core.StageStatus.Failed))
        {
            throw new IOException("Folder access check failed. Check credentials and filesystem permissions.");
        }
    }

    public Task ConfirmAsync(DestinationCredentialSnapshot access, StorageObjectReceipt receipt, CancellationToken cancellationToken)
    {
        return VerifyAsync(access, receipt, cancellationToken);
    }

    public async Task<StorageObjectReceipt> UploadAsync(DestinationCredentialSnapshot access, Stream source,
        string objectKey, string sha256, CancellationToken cancellationToken)
    {
        return await ShareAccess.RunAsync(Credential(access), async () =>
        {
            string destination = ArtifactFiles.Resolve(access.Destination.Location, objectKey);
            if (File.Exists(destination))
            {
                var existing = new StorageObjectReceipt(destination, source.Length, sha256);
                await VerifyFileAsync(existing, cancellationToken);
                return existing;
            }

            string partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(true);
            }

            var receipt = new StorageObjectReceipt(partial, source.Length, sha256);
            await VerifyFileAsync(receipt, cancellationToken);
            ArtifactFiles.EnsureNoLinks(destination);
            File.Move(partial, destination, overwrite: false);
            return receipt with { Path = destination };
        });
    }

    public async Task VerifyAsync(DestinationCredentialSnapshot access, StorageObjectReceipt receipt, CancellationToken cancellationToken)
    {
        await ShareAccess.RunAsync(Credential(access), async () =>
        {
            RequireManagedPath(access, receipt.Path);
            await VerifyFileAsync(receipt, cancellationToken);
            return true;
        });
    }

    public async Task DeleteAsync(DestinationCredentialSnapshot access, StorageObjectReceipt receipt, CancellationToken cancellationToken)
    {
        await ShareAccess.RunAsync(Credential(access), async () =>
        {
            RequireManagedPath(access, receipt.Path);
            await ArtifactDeletion.DeleteVerifiedAsync(receipt.Path, receipt.Sha256, cancellationToken);
            return true;
        });
    }

    private static ShareCredentialSnapshot? Credential(DestinationCredentialSnapshot access)
    {
        if (access.Destination.UserName is null)
        {
            return null;
        }

        return new ShareCredentialSnapshot(access.Destination.Location, access.Destination.UserName,
            access.ProtectedSecret ?? throw new IOException("Folder credentials are unavailable."));
    }

    private static void RequireManagedPath(DestinationCredentialSnapshot access, string path)
    {
        string expected = ArtifactFiles.Resolve(access.Destination.Location, Path.GetFileName(path));
        if (!expected.Equals(path, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The artifact is outside its configured folder.");
        }
    }

    private static async Task VerifyFileAsync(StorageObjectReceipt receipt, CancellationToken cancellationToken)
    {
        if (new FileInfo(receipt.Path).Length != receipt.SizeBytes
            || await ArtifactFiles.HashAsync(receipt.Path, cancellationToken) != receipt.Sha256)
        {
            throw new IOException("Destination content does not match the recorded backup. It will not be overwritten or deleted.");
        }
    }
}
