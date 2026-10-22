using SqlBackup.Core;

namespace SqlBackup.Infrastructure;

public sealed class DestinationStorage(Func<DestinationKind, IDestinationStorage>? resolver = null)
{
    public IDestinationStorage For(DestinationKind kind)
    {
        if (resolver is not null)
        {
            return resolver(kind);
        }

        return kind switch
        {
            DestinationKind.Folder => new FolderDestinationStorage(),
            DestinationKind.AzureBlob => new AzureBlobDestinationStorage(),
            DestinationKind.AmazonS3 => new AmazonS3DestinationStorage(),
            _ => throw new ArgumentException("Unsupported destination type.")
        };
    }
}
