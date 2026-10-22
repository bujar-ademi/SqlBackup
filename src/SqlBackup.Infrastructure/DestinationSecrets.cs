using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SqlBackup.Core;

namespace SqlBackup.Infrastructure;

public sealed class DestinationSecrets(ConfigurationStore store)
{
    public async Task<DestinationCredentialSnapshot[]> PrepareAsync(
        BackupJob job, DestinationPassword[]? passwords, CancellationToken cancellationToken)
    {
        if (job.Destinations is null)
        {
            return [];
        }

        if (passwords is not null && (passwords.Length > 16
            || passwords.Select(item => item.DestinationId).Distinct().Count() != passwords.Length
            || passwords.Any(item => !job.Destinations.Any(destination => destination.Id == item.DestinationId)
                || item.Secret?.Length > 8192 || item.SessionToken?.Length > 8192
                || (!string.IsNullOrEmpty(item.SessionToken) && string.IsNullOrEmpty(item.Secret)))))
        {
            throw new ArgumentException("Supply at most one credential entry per configured destination. Session-token changes require the secret access key again.");
        }

        var previous = await ReadAsync("JobDestinationSecrets", "JobId", job.Id, cancellationToken);
        var legacy = await new StorageSecrets(store).ReadJobAsync(job.Id, cancellationToken);
        var snapshots = new List<DestinationCredentialSnapshot>();
        foreach (var destination in job.Destinations)
        {
            var supplied = passwords?.SingleOrDefault(item => item.DestinationId == destination.Id);
            var existing = previous.SingleOrDefault(item => item.Destination.Id == destination.Id);
            bool sameIdentity = existing is not null && SameCredentialTarget(existing.Destination, destination);
            byte[]? secret = null;
            byte[]? sessionToken = null;
            bool requiresSecret = destination.Kind != DestinationKind.Folder || destination.UserName is not null;
            if (requiresSecret)
            {
                if (!string.IsNullOrEmpty(supplied?.Secret))
                {
                    secret = Protect(supplied.Secret);
                    sessionToken = Protect(supplied.SessionToken);
                }
                else if (sameIdentity)
                {
                    secret = existing!.ProtectedSecret;
                    sessionToken = existing.ProtectedSessionToken;
                }
                else if (destination.Kind == DestinationKind.Folder)
                {
                    // Conversion may reuse only this job's exact legacy folder/user credential.
                    foreach (var share in new[] { legacy.Destination, legacy.Secondary, legacy.Source })
                    {
                        if (share is not null && share.UserName == destination.UserName
                            && share.Folder.TrimEnd('\\').Equals(destination.Location.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        {
                            secret = share.ProtectedPassword;
                            break;
                        }
                    }
                }

                if (secret is null)
                {
                    throw new ArgumentException("Enter credentials for each new or changed destination. Credentials are never borrowed from another job.");
                }
            }

            snapshots.Add(new DestinationCredentialSnapshot(destination, secret, sessionToken));
        }

        return snapshots.ToArray();
    }

    public Task<DestinationCredentialSnapshot[]> ReadRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        return ReadAsync("RunDestinationSecrets", "RunId", runId, cancellationToken);
    }

    private async Task<DestinationCredentialSnapshot[]> ReadAsync(
        string table, string key, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(store.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Payload FROM {table} WHERE {key} = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        string? payload = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (payload is null)
        {
            return [];
        }

        return JsonSerializer.Deserialize<DestinationCredentialSnapshot[]>(payload)
            ?? throw new InvalidDataException("Invalid protected destination credentials.");
    }

    public static void Validate(BackupDestination destination, DestinationCredentialSnapshot snapshot)
    {
        bool requiresSecret = destination.Kind != DestinationKind.Folder || destination.UserName is not null;
        if (destination != snapshot.Destination || (requiresSecret && snapshot.ProtectedSecret is null))
        {
            throw new IOException("Protected destination credentials do not match this execution. Re-enter and save the credentials.");
        }
    }

    public static string? Unprotect(byte[]? ciphertext)
    {
        if (ciphertext is null)
        {
            return null;
        }

        byte[] plaintext = ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[]? Protect(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return null;
        }

        if (secret.Length > 8192)
        {
            throw new ArgumentException("Destination credentials exceed the supported length.");
        }

        byte[] plaintext = Encoding.UTF8.GetBytes(secret);
        try
        {
            return ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static bool SameCredentialTarget(BackupDestination previous, BackupDestination current)
    {
        return previous.Kind == current.Kind && previous.Location == current.Location
            && previous.UserName == current.UserName && previous.Region == current.Region;
    }
}
