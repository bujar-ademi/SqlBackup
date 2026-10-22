using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using System.Security.Cryptography;

namespace SqlBackup.Infrastructure;

public sealed class AzureBlobDestinationStorage : IDestinationStorage
{
    public async Task CheckAccessAsync(DestinationCredentialSnapshot access, CancellationToken cancellationToken)
    {
        var blob = CreateBlob(access, "access-check");
        await blob.GetParentBlobContainerClient().GetPropertiesAsync(cancellationToken: cancellationToken);
    }

    public async Task ConfirmAsync(DestinationCredentialSnapshot access, StorageObjectReceipt receipt, CancellationToken cancellationToken)
    {
        var blob = CreateBlob(access, StorageObjectPaths.ReadKey(access.Destination, receipt.Path));
        if (receipt.VersionId is not null)
        {
            blob = blob.WithVersion(receipt.VersionId);
        }

        var properties = await blob.GetPropertiesAsync(Conditions(receipt), cancellationToken);
        RequireMetadata(properties.Value, receipt);
    }

    public async Task<StorageObjectReceipt> UploadAsync(DestinationCredentialSnapshot access, Stream source,
        string objectKey, string sha256, CancellationToken cancellationToken)
    {
        var blob = CreateBlob(access, objectKey);
        var receipt = new StorageObjectReceipt(StorageObjectPaths.PathFor(access.Destination, objectKey), source.Length, sha256);
        if (await blob.ExistsAsync(cancellationToken))
        {
            await VerifyAsync(access, receipt, cancellationToken);
            var existing = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
            return receipt with { EntityTag = existing.Value.ETag.ToString(), VersionId = existing.Value.VersionId };
        }

        var options = new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            Metadata = new Dictionary<string, string> { ["sqlbackup_sha256"] = sha256 },
            TransferOptions = new StorageTransferOptions { MaximumConcurrency = 2, MaximumTransferSize = 16 * 1024 * 1024 },
            TransferValidation = new UploadTransferValidationOptions { ChecksumAlgorithm = StorageChecksumAlgorithm.Auto }
        };
        var uploaded = await blob.UploadAsync(source, options, cancellationToken);
        var properties = await blob.GetPropertiesAsync(new BlobRequestConditions { IfMatch = uploaded.Value.ETag }, cancellationToken);
        RequireMetadata(properties.Value, receipt);
        return receipt with { EntityTag = uploaded.Value.ETag.ToString(), VersionId = uploaded.Value.VersionId };
    }

    public async Task VerifyAsync(DestinationCredentialSnapshot access, StorageObjectReceipt receipt, CancellationToken cancellationToken)
    {
        var blob = CreateBlob(access, StorageObjectPaths.ReadKey(access.Destination, receipt.Path));
        var conditions = Conditions(receipt);
        var properties = await blob.GetPropertiesAsync(conditions, cancellationToken);
        RequireMetadata(properties.Value, receipt);
        // Metadata alone is not integrity evidence for a pre-existing object or cleanup.
        var download = await blob.DownloadStreamingAsync(new BlobDownloadOptions
        {
            Conditions = new BlobRequestConditions { IfMatch = properties.Value.ETag }
        }, cancellationToken);
        await using var content = download.Value.Content;
        string actual = Convert.ToHexString(await SHA256.HashDataAsync(content, cancellationToken));
        if (actual != receipt.Sha256)
        {
            throw new IOException("Azure blob integrity check failed.");
        }
    }

    public async Task DeleteAsync(DestinationCredentialSnapshot access, StorageObjectReceipt receipt, CancellationToken cancellationToken)
    {
        await VerifyAsync(access, receipt, cancellationToken);
        if (receipt.EntityTag is null)
        {
            throw new IOException("Azure deletion requires recorded object identity.");
        }

        var blob = CreateBlob(access, StorageObjectPaths.ReadKey(access.Destination, receipt.Path));
        if (receipt.VersionId is not null)
        {
            blob = blob.WithVersion(receipt.VersionId);
        }

        await blob.DeleteAsync(conditions: Conditions(receipt), cancellationToken: cancellationToken);
    }

    private static BlobClient CreateBlob(DestinationCredentialSnapshot access, string objectKey)
    {
        DestinationValidationGuard(access);
        string sas = DestinationSecrets.Unprotect(access.ProtectedSecret)
            ?? throw new IOException("Azure SAS credentials are unavailable.");
        var options = new BlobClientOptions();
        options.Retry.MaxRetries = 2;
        var container = new BlobContainerClient(new Uri(access.Destination.Location), new AzureSasCredential(sas.TrimStart('?')), options);
        return container.GetBlobClient(objectKey);
    }

    private static void DestinationValidationGuard(DestinationCredentialSnapshot access)
    {
        SqlBackup.Core.DestinationValidation.Validate(access.Destination);
        DestinationSecrets.Validate(access.Destination, access);
    }

    private static BlobRequestConditions Conditions(StorageObjectReceipt receipt)
    {
        var conditions = new BlobRequestConditions();
        if (receipt.EntityTag is not null)
        {
            conditions.IfMatch = new ETag(receipt.EntityTag);
        }

        return conditions;
    }

    private static void RequireMetadata(BlobProperties properties, StorageObjectReceipt receipt)
    {
        if (properties.ContentLength != receipt.SizeBytes
            || !properties.Metadata.TryGetValue("sqlbackup_sha256", out string? hash) || hash != receipt.Sha256)
        {
            throw new IOException("Azure blob size or ownership metadata does not match the backup.");
        }
    }
}
