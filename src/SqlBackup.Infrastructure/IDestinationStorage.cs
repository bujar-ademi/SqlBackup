namespace SqlBackup.Infrastructure;

public interface IDestinationStorage
{
    Task CheckAccessAsync(DestinationCredentialSnapshot access, CancellationToken cancellationToken);

    Task<StorageObjectReceipt> UploadAsync(DestinationCredentialSnapshot access, Stream source,
        string objectKey, string sha256, CancellationToken cancellationToken);

    Task VerifyAsync(DestinationCredentialSnapshot access, StorageObjectReceipt receipt, CancellationToken cancellationToken);

    Task ConfirmAsync(DestinationCredentialSnapshot access, StorageObjectReceipt receipt, CancellationToken cancellationToken);

    Task DeleteAsync(DestinationCredentialSnapshot access, StorageObjectReceipt receipt, CancellationToken cancellationToken);
}
