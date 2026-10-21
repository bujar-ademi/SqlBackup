namespace SqlBackup.Core;

public static class BackupLocations
{
    public static string DefaultTemporaryFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SqlBackup", "Temp");

    public static string NormalizeTemporaryFolder(string folder)
    {
        const string token = "%ProgramData%";
        string value = folder.Trim();
        if (value.StartsWith(token + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), value[(token.Length + 1)..]);
        }

        return value;
    }

    public static string SqlFolder(BackupJob job) => job.TemporaryFolder ?? job.Destination;

    public static string SourceFolder(BackupJob job) => job.Storage?.ServiceSourceFolder ?? SqlFolder(job);

    public static string PrimaryFolder(BackupJob job) => job.TemporaryFolder is null ? SourceFolder(job) : job.Destination;
}
