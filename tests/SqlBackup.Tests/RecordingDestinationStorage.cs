using System.IO;
using System.Security.Cryptography;
using SqlBackup.Core;
using SqlBackup.Infrastructure;

namespace SqlBackup.Tests;

public sealed class RecordingDestinationStorage : IDestinationStorage
{
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
    public Dictionary<Guid, int> UploadCounts { get; } = [];
    public Dictionary<Guid, string?> ObservedSecrets { get; } = [];
    public HashSet<Guid> FailingDestinations { get; } = [];
    public Dictionary<Guid, int> TransientFailures { get; } = [];

    public Task CheckAccessAsync(DestinationCredentialSnapshot access, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DestinationSecrets.Validate(access.Destination, access);
        return Task.CompletedTask;
    }

    public async Task<StorageObjectReceipt> UploadAsync(DestinationCredentialSnapshot access, Stream source,
        string objectKey, string sha256, CancellationToken cancellationToken)
    {
        DestinationSecrets.Validate(access.Destination, access);
        UploadCounts[access.Destination.Id] = UploadCounts.GetValueOrDefault(access.Destination.Id) + 1;
        ObservedSecrets[access.Destination.Id] = DestinationSecrets.Unprotect(access.ProtectedSecret);
        if (TransientFailures.GetValueOrDefault(access.Destination.Id) > 0)
        {
            TransientFailures[access.Destination.Id]--;
            throw new IOException("Fictional transient network failure.");
        }

        if (FailingDestinations.Contains(access.Destination.Id))
        {
            throw new UnauthorizedAccessException("Fictional transfer failure.");
        }

        using var output = new MemoryStream();
        await source.CopyToAsync(output, cancellationToken);
        byte[] payload = output.ToArray();
        if (Convert.ToHexString(SHA256.HashData(payload)) != sha256)
        {
            throw new IOException("Fixture upload hash mismatch.");
        }

        string path = StorageObjectPaths.PathFor(access.Destination, objectKey);
        if (Objects.TryGetValue(path, out var existing) && !existing.SequenceEqual(payload))
        {
            throw new IOException("Fixture never overwrites an existing different object.");
        }

        Objects[path] = payload;
        return new StorageObjectReceipt(path, payload.Length, sha256, "fixture-etag", "fixture-version");
    }

    public Task ConfirmAsync(DestinationCredentialSnapshot access, StorageObjectReceipt receipt, CancellationToken cancellationToken)
    {
        return VerifyAsync(access, receipt, cancellationToken);
    }

    public Task VerifyAsync(DestinationCredentialSnapshot access, StorageObjectReceipt receipt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DestinationSecrets.Validate(access.Destination, access);
        if (!Objects.TryGetValue(receipt.Path, out var payload) || payload.LongLength != receipt.SizeBytes
            || Convert.ToHexString(SHA256.HashData(payload)) != receipt.Sha256)
        {
            throw new IOException("Fixture destination evidence changed or is unavailable.");
        }

        return Task.CompletedTask;
    }

    public async Task DeleteAsync(DestinationCredentialSnapshot access, StorageObjectReceipt receipt, CancellationToken cancellationToken)
    {
        await VerifyAsync(access, receipt, cancellationToken);
        Objects.Remove(receipt.Path);
    }
}
