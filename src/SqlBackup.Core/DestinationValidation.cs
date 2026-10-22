namespace SqlBackup.Core;

public static class DestinationValidation
{
    public static void ValidateAll(BackupJob job)
    {
        if (job.Destinations is null || job.Destinations.Length is < 1 or > 16)
        {
            throw new ArgumentException("Configure between one and sixteen backup destinations.");
        }

        var identities = new HashSet<Guid>();
        var locations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var destination in job.Destinations)
        {
            Validate(destination);
            string location = destination.Location.TrimEnd('\\', '/');
            if (destination.Kind == DestinationKind.Folder)
            {
                location = location.ToUpperInvariant();
            }

            string identity = $"{destination.Kind}/{location}/{destination.Prefix.Trim('/')}";
            if (!identities.Add(destination.Id) || !locations.Add(identity))
            {
                throw new ArgumentException("Destination identifiers and storage locations must be unique within the job.");
            }
        }

        JobValidation.ValidateDestination(job.TemporaryFolder ?? BackupLocations.DefaultTemporaryFolder);
        if (!string.IsNullOrWhiteSpace(job.Storage?.ServiceSourceFolder))
        {
            JobValidation.ValidateDestination(job.Storage.ServiceSourceFolder);
        }

        JobValidation.ValidateStorage(job.Destination, job.Storage);
        foreach (var destination in job.Destinations.Where(item => item.Kind == DestinationKind.Folder))
        {
            string fallback = (job.TemporaryFolder ?? BackupLocations.DefaultTemporaryFolder).TrimEnd('\\');
            string? source = job.Storage?.ServiceSourceFolder?.TrimEnd('\\');
            string final = destination.Location.TrimEnd('\\');
            if (final.Equals(fallback, StringComparison.OrdinalIgnoreCase)
                || final.Equals(source, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Temporary/source folders must differ from every final destination.");
            }
        }
    }

    public static void Validate(BackupDestination destination)
    {
        if (destination.Id == Guid.Empty || string.IsNullOrWhiteSpace(destination.Name) || destination.Name.Length > 100
            || !Enum.IsDefined(destination.Kind) || string.IsNullOrWhiteSpace(destination.Location)
            || destination.Location.Length > 512 || destination.RetentionDays is < 0 or > 36500)
        {
            throw new ArgumentException("Each destination needs an ID, name, supported type, location and retention from 0 to 36500 days.");
        }

        if (destination.Prefix.Length > 256 || destination.Prefix.Contains('\\')
            || destination.Prefix.Split('/').Any(segment => segment is "." or "..")
            || destination.Prefix.Any(char.IsControl) || destination.Prefix.Contains('?') || destination.Prefix.Contains('#'))
        {
            throw new ArgumentException("Use a cloud prefix without backslashes, query strings, control characters or relative segments.");
        }

        if (destination.UserName is { } userName && (string.IsNullOrWhiteSpace(userName) || userName.Length > 256))
        {
            throw new ArgumentException("Enter a supported user name or access key ID.");
        }

        switch (destination.Kind)
        {
            case DestinationKind.Folder:
                JobValidation.ValidateDestination(destination.Location);
                if (destination.UserName is not null && !destination.Location.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    throw new ArgumentException("Folder credentials require a UNC share.");
                }

                if (destination.Prefix.Length > 0 || destination.Region is not null)
                {
                    throw new ArgumentException("Folder destinations do not use a cloud prefix or region.");
                }

                break;
            case DestinationKind.AzureBlob:
                ValidateAzure(destination.Location);
                if (destination.UserName is not null || destination.Region is not null)
                {
                    throw new ArgumentException("Azure Blob uses a container URL and a separately protected SAS token.");
                }

                break;
            case DestinationKind.AmazonS3:
                bool validBucket = destination.Location.Length is >= 3 and <= 63
                    && destination.Location.All(character => char.IsAsciiLetterLower(character)
                        || char.IsAsciiDigit(character) || character == '-')
                    && char.IsAsciiLetterOrDigit(destination.Location[0])
                    && char.IsAsciiLetterOrDigit(destination.Location[^1]);
                if (!validBucket || string.IsNullOrWhiteSpace(destination.UserName)
                    || string.IsNullOrWhiteSpace(destination.Region) || destination.Region.Length > 40
                    || !destination.Region.All(character => char.IsAsciiLetterLower(character)
                        || char.IsAsciiDigit(character) || character == '-'))
                {
                    throw new ArgumentException("S3 requires a standard bucket name, AWS region and access key ID. Dotted and special-purpose buckets are not supported.");
                }

                break;
        }
    }

    private static void ValidateAzure(string location)
    {
        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || !uri.Host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Use an HTTPS Azure public-cloud container URL without a SAS query. Enter the SAS token in the secret field.");
        }

        string container = uri.AbsolutePath.Trim('/');
        if (container.Length is < 3 or > 63 || container.Contains('/') || container.Contains("--", StringComparison.Ordinal)
            || !container.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-')
            || !char.IsAsciiLetterOrDigit(container[0]) || !char.IsAsciiLetterOrDigit(container[^1]))
        {
            throw new ArgumentException("The Azure URL must identify an existing container, without a blob path.");
        }
    }
}
