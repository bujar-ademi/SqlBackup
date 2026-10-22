namespace SqlBackup.Core;

public static class JobValidation
{
    public static void Validate(BackupJob job)
    {
        if (job.Id == Guid.Empty || string.IsNullOrWhiteSpace(job.Name) || job.Name.Length > 100)
        {
            throw new ArgumentException("A job ID and name (maximum 100 characters) are required.");
        }

        if (job.Databases is null || job.Databases.Length is < 1 or > 16)
        {
            throw new ArgumentException("Select between one and sixteen databases per job.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string database in job.Databases)
        {
            if (string.IsNullOrWhiteSpace(database) || database.Length > 128 || !names.Add(database)
                || database.Equals("tempdb", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Database names must be valid, unique, and must not include tempdb.");
            }
        }

        if (job.Destinations is not null)
        {
            DestinationValidation.ValidateAll(job);
            return;
        }

        ValidateDestination(job.Destination);
        ValidateStorage(job.Destination, job.Storage);
        ValidateTemporaryFolder(job);
    }

    public static void ValidateTemporaryFolder(BackupJob job)
    {
        if (job.TemporaryFolder is not null)
        {
            ValidateDestination(job.TemporaryFolder);
            string source = BackupLocations.SourceFolder(job).TrimEnd('\\');
            if (source.Equals(job.Destination.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                || job.TemporaryFolder.TrimEnd('\\').Equals(job.Destination.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                || source.Equals(job.Storage?.SecondaryFolder?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Temporary/source folders must differ from final backup destinations.");
            }
        }
    }

    public static void ValidateStorage(string destination, StoragePolicy? policy)
    {
        if (policy is { } storage)
        {
            ValidateShareUser(destination, storage.DestinationUserName);
            ValidateShareUser(storage.SecondaryFolder, storage.SecondaryUserName);
            ValidateShareUser(storage.ServiceSourceFolder, storage.SourceUserName);
            if (storage.PrimaryRetentionDays is < 0 or > 36500 || storage.SecondaryRetentionDays is < 0 or > 36500)
            {
                throw new ArgumentException("Retention must be between 0 (keep all) and 36500 days.");
            }

            if (!string.IsNullOrWhiteSpace(storage.ServiceSourceFolder))
            {
                ValidateDestination(storage.ServiceSourceFolder);
            }

            if (!string.IsNullOrWhiteSpace(storage.SecondaryFolder))
            {
                ValidateDestination(storage.SecondaryFolder);
                string source = storage.ServiceSourceFolder ?? destination;
                if (source.TrimEnd('\\').Equals(storage.SecondaryFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                    || destination.TrimEnd('\\').Equals(storage.SecondaryFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("The secondary folder must differ from the source folder.");
                }
            }
            else if (storage.SecondaryRetentionDays > 0)
            {
                throw new ArgumentException("Configure a secondary folder before its retention policy.");
            }
        }
    }

    private static void ValidateShareUser(string? folder, string? userName)
    {
        if (userName is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(userName) || userName.Length > 256
            || userName.IndexOfAny(['\0', '\r', '\n']) >= 0 || folder?.StartsWith(@"\\", StringComparison.Ordinal) != true)
        {
            throw new ArgumentException("Explicit network credentials require a UNC folder and a valid user name.");
        }
    }

    public static void ValidateDestination(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || destination.Length > 512
            || destination.Contains('/')
            || destination.IndexOfAny(['\0', '\r', '\n', '*', '?', '"', '<', '>', '|']) >= 0)
        {
            throw new ArgumentException("Use an absolute Windows folder or UNC share path, without wildcards or parent traversal.");
        }

        bool drivePath = destination.Length >= 3 && char.IsAsciiLetter(destination[0])
            && destination[1] == ':' && destination[2] == '\\';
        bool uncPath = destination.StartsWith(@"\\", StringComparison.Ordinal)
            && !destination.StartsWith(@"\\?\", StringComparison.Ordinal)
            && !destination.StartsWith(@"\\.\", StringComparison.Ordinal)
            && destination[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Length >= 2;
        if ((!drivePath && !uncPath) || destination[2..].Contains(':'))
        {
            throw new ArgumentException("The destination must be an absolute drive folder or UNC share visible to SQL Server.");
        }

        foreach (string segment in destination.Split('\\'))
        {
            if (segment is "." or "..")
            {
                throw new ArgumentException("Relative path segments are not allowed.");
            }
        }
    }
}
