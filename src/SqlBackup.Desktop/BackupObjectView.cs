using SqlBackup.Core;

namespace SqlBackup.Desktop;

public sealed record BackupObjectView(string Database, string Type, string Size, string Status, string? Path)
{
    public static BackupObjectView Create(BackupRun run, DatabaseBackupResult result)
    {
        string copies = result.Copies is { Length: > 0 }
            ? string.Join(", ", result.Copies.Select(item => item.Status.ToString()).Distinct()) : "Not configured";
        return new BackupObjectView(result.Database, run.Type.ToString(), DisplaySize.Format(result.SizeBytes),
            $"Backup {result.Backup} · Verify {result.Verification} · Copy {copies}",
            run.Job.TemporaryFolder is null ? result.ArtifactPath
                : result.Copies?.FirstOrDefault(copy => copy.Folder == run.Job.Destination)?.Path ?? result.ArtifactPath);
    }
}
