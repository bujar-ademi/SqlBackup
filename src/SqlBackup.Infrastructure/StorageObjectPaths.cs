using SqlBackup.Core;

namespace SqlBackup.Infrastructure;

public static class StorageObjectPaths
{
    public static string Key(BackupRun run, string fileName, BackupDestination destination)
    {
        if (destination.Kind == DestinationKind.Folder)
        {
            return fileName;
        }

        return $"{destination.Prefix.Trim('/')}/{run.Job.Id:N}/{run.Id:N}/{fileName}".TrimStart('/');
    }

    public static string PathFor(BackupDestination destination, string objectKey)
    {
        return destination.Kind switch
        {
            DestinationKind.Folder => Path.Combine(destination.Location, objectKey),
            DestinationKind.AzureBlob => destination.Location.TrimEnd('/') + "/" + objectKey,
            DestinationKind.AmazonS3 => $"s3://{destination.Location}/{objectKey}",
            _ => throw new ArgumentException("Unsupported destination type.")
        };
    }

    public static string ReadKey(BackupDestination destination, string path)
    {
        string root = destination.Kind == DestinationKind.AzureBlob
            ? destination.Location.TrimEnd('/') + "/"
            : $"s3://{destination.Location}/";
        if (!path.StartsWith(root, StringComparison.Ordinal))
        {
            throw new IOException("The recorded cloud object is outside its configured destination.");
        }

        return path[root.Length..];
    }
}
