using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using System.Net;
using System.Security.Cryptography;

namespace SqlBackup.Infrastructure;

public sealed class AmazonS3DestinationStorage : IDestinationStorage
{
    public async Task CheckAccessAsync(DestinationCredentialSnapshot access, CancellationToken cancellationToken)
    {
        using var client = CreateClient(access);
        await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = access.Destination.Location,
            Prefix = access.Destination.Prefix.Trim('/'),
            MaxKeys = 1
        }, cancellationToken);
    }

    public async Task ConfirmAsync(DestinationCredentialSnapshot access, StorageObjectReceipt receipt, CancellationToken cancellationToken)
    {
        using var client = CreateClient(access);
        var properties = await ReadMetadataAsync(client, access,
            StorageObjectPaths.ReadKey(access.Destination, receipt.Path), cancellationToken, receipt.VersionId);
        RequireMetadata(properties, receipt);
        if (receipt.EntityTag is not null && properties.ETag != receipt.EntityTag)
        {
            throw new IOException("S3 object identity changed after upload.");
        }
    }

    public async Task<StorageObjectReceipt> UploadAsync(DestinationCredentialSnapshot access, Stream source,
        string objectKey, string sha256, CancellationToken cancellationToken)
    {
        using var client = CreateClient(access);
        var receipt = new StorageObjectReceipt(StorageObjectPaths.PathFor(access.Destination, objectKey), source.Length, sha256);
        var existing = await ReadExistingAsync(client, access, objectKey, cancellationToken);
        if (existing is not null)
        {
            RequireMetadata(existing, receipt);
            receipt = receipt with { EntityTag = existing.ETag, VersionId = existing.VersionId };
            await VerifyAsync(access, receipt, cancellationToken);
            return receipt;
        }

        var initiate = new InitiateMultipartUploadRequest { BucketName = access.Destination.Location, Key = objectKey };
        initiate.Metadata.Add("sqlbackup-sha256", sha256);
        var started = await client.InitiateMultipartUploadAsync(initiate, cancellationToken);
        try
        {
            var parts = new List<PartETag>();
            long minimumPartSize = 64L * 1024 * 1024;
            long partSize = Math.Max(minimumPartSize, (source.Length + 9998) / 9999);
            if (partSize > 5L * 1024 * 1024 * 1024)
            {
                throw new IOException("The backup exceeds the supported S3 multipart size.");
            }

            long offset = 0;
            while (offset < source.Length)
            {
                long length = Math.Min(partSize, source.Length - offset);
                int partNumber = parts.Count + 1;
                using var segment = new ReadOnlySegmentStream(source, offset, length);
                var part = await client.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = access.Destination.Location,
                    Key = objectKey,
                    UploadId = started.UploadId,
                    PartNumber = partNumber,
                    PartSize = length,
                    InputStream = segment,
                    IsLastPart = offset + length == source.Length
                }, cancellationToken);
                parts.Add(new PartETag(partNumber, part.ETag));
                offset += length;
            }

            var completed = await client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = access.Destination.Location,
                Key = objectKey,
                UploadId = started.UploadId,
                PartETags = parts,
                IfNoneMatch = "*"
            }, cancellationToken);
            receipt = receipt with { EntityTag = completed.ETag, VersionId = completed.VersionId };
            var properties = await ReadMetadataAsync(client, access, objectKey, cancellationToken);
            RequireMetadata(properties, receipt);
            if (properties.ETag != receipt.EntityTag)
            {
                throw new IOException("S3 object changed before upload confirmation.");
            }

            return receipt;
        }
        catch (Exception failure)
        {
            try
            {
                using var abortTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                {
                    BucketName = access.Destination.Location,
                    Key = objectKey,
                    UploadId = started.UploadId
                }, abortTimeout.Token);
            }
            catch (Exception abortFailure) when (abortFailure is not OutOfMemoryException)
            {
                // Preserve the original failure/cancellation and expose incomplete-upload cleanup evidence.
                failure.Data["MultipartCleanupFailed"] = true;
            }

            throw;
        }
    }

    public async Task VerifyAsync(DestinationCredentialSnapshot access, StorageObjectReceipt receipt, CancellationToken cancellationToken)
    {
        using var client = CreateClient(access);
        string key = StorageObjectPaths.ReadKey(access.Destination, receipt.Path);
        var properties = await ReadMetadataAsync(client, access, key, cancellationToken, receipt.VersionId);
        RequireMetadata(properties, receipt);
        using var response = await client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = access.Destination.Location,
            Key = key,
            VersionId = receipt.VersionId,
            EtagToMatch = receipt.EntityTag ?? properties.ETag
        }, cancellationToken);
        string actual = Convert.ToHexString(await SHA256.HashDataAsync(response.ResponseStream, cancellationToken));
        if (actual != receipt.Sha256)
        {
            throw new IOException("S3 object integrity check failed.");
        }
    }

    public async Task DeleteAsync(DestinationCredentialSnapshot access, StorageObjectReceipt receipt, CancellationToken cancellationToken)
    {
        await VerifyAsync(access, receipt, cancellationToken);
        if (receipt.EntityTag is null)
        {
            throw new IOException("S3 deletion requires recorded object identity.");
        }

        using var client = CreateClient(access);
        await client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = access.Destination.Location,
            Key = StorageObjectPaths.ReadKey(access.Destination, receipt.Path),
            VersionId = receipt.VersionId,
            IfMatch = receipt.EntityTag
        }, cancellationToken);
    }

    private static AmazonS3Client CreateClient(DestinationCredentialSnapshot access)
    {
        SqlBackup.Core.DestinationValidation.Validate(access.Destination);
        DestinationSecrets.Validate(access.Destination, access);
        string secret = DestinationSecrets.Unprotect(access.ProtectedSecret)
            ?? throw new IOException("S3 credentials are unavailable.");
        string? token = DestinationSecrets.Unprotect(access.ProtectedSessionToken);
        AWSCredentials credentials;
        if (token is null)
        {
            credentials = new BasicAWSCredentials(access.Destination.UserName, secret);
        }
        else
        {
            credentials = new SessionAWSCredentials(access.Destination.UserName, secret, token);
        }

        return new AmazonS3Client(credentials, new AmazonS3Config
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(access.Destination.Region),
            MaxErrorRetry = 2
        });
    }

    private static Task<GetObjectMetadataResponse> ReadMetadataAsync(AmazonS3Client client,
        DestinationCredentialSnapshot access, string key, CancellationToken cancellationToken, string? versionId = null)
    {
        return client.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = access.Destination.Location,
            Key = key,
            VersionId = versionId
        }, cancellationToken);
    }

    private static async Task<GetObjectMetadataResponse?> ReadExistingAsync(AmazonS3Client client,
        DestinationCredentialSnapshot access, string key, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadMetadataAsync(client, access, key, cancellationToken);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static void RequireMetadata(GetObjectMetadataResponse properties, StorageObjectReceipt receipt)
    {
        if (properties.ContentLength != receipt.SizeBytes || properties.Metadata["sqlbackup-sha256"] != receipt.Sha256)
        {
            throw new IOException("S3 object size or ownership metadata does not match the backup.");
        }
    }
}
