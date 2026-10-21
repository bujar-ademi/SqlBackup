using System.Security.Principal;
using SqlBackup.Core;

namespace SqlBackup.Infrastructure;

public sealed class StorageDiagnostics
{
    public async Task<DiagnosticItem[]> CheckAsync(BackupJob job, CancellationToken cancellationToken,
        StorageAccessSnapshot? access = null)
    {
        try
        {
            StorageSecrets.ValidateSnapshot(job, access ?? new StorageAccessSnapshot());
        }
        catch (IOException)
        {
            return [new DiagnosticItem("Storage credentials", StageStatus.Failed,
                "Protected network credentials are missing or do not match the configured folders/users. Re-enter and save the passwords.")];
        }

        var checks = new List<DiagnosticItem>
        {
            new("Service identity", StageStatus.Succeeded, WindowsIdentity.GetCurrent().Name)
        };
        string source = BackupLocations.SourceFolder(job);
        string sourceLabel = job.TemporaryFolder is null ? "Service source folder" : "Service temporary/source folder";
        checks.Add(await ProbeWithAccessAsync(sourceLabel, source, access?.Source, cancellationToken));
        if (job.TemporaryFolder is not null)
        {
            checks.Add(await ProbeWithAccessAsync("Final backup folder", job.Destination, access?.Destination, cancellationToken));
        }
        if (!string.IsNullOrWhiteSpace(job.Storage?.SecondaryFolder))
        {
            checks.Add(await ProbeWithAccessAsync("Secondary folder", job.Storage.SecondaryFolder, access?.Secondary, cancellationToken));
        }

        return checks.ToArray();
    }

    private static async Task<DiagnosticItem> ProbeWithAccessAsync(string name, string folder,
        ShareCredentialSnapshot? credential, CancellationToken cancellationToken)
    {
        try
        {
            return await ShareAccess.RunAsync(credential, () => ProbeAsync(name, folder, cancellationToken));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException)
        {
            return new DiagnosticItem(name, StageStatus.Failed, "Network credentials are unavailable or access failed. Re-enter the password and test again.");
        }
    }

    private static async Task<DiagnosticItem> ProbeAsync(string name, string folder, CancellationToken cancellationToken)
    {
        try
        {
            string probe = ArtifactFiles.Resolve(folder, "AbaSqlBackup-access-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.DeleteOnClose))
            {
                await stream.WriteAsync(new byte[] { 1 }, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Position = 0;
                byte[] read = new byte[1];
                if (await stream.ReadAsync(read, cancellationToken) != 1 || read[0] != 1)
                {
                    throw new IOException("Probe read did not match.");
                }
            }

            string capacity = "Free space cannot be inferred for this share.";
            string? root = Path.GetPathRoot(folder);
            if (root is { Length: 3 } && root[1] == ':')
            {
                var drive = new DriveInfo(root);
                capacity = $"Available volume space: {drive.AvailableFreeSpace / (1024 * 1024):N0} MiB.";
            }

            return new DiagnosticItem(name, StageStatus.Succeeded,
                "Service account created, wrote, read and removed its own test file. Existing backup ACLs may differ. " + capacity);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new DiagnosticItem(name, StageStatus.Failed,
                "Service account cannot confirm access. Check folder existence, UNC/share permissions, mapping and storage capacity.");
        }
    }
}
