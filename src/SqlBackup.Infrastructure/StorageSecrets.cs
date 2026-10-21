using Microsoft.Data.Sqlite;
using SqlBackup.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlBackup.Infrastructure;

public sealed class StorageSecrets(ConfigurationStore store)
{
    public static void ValidateSnapshot(BackupJob job, StorageAccessSnapshot access)
    {
        ValidateCredential(job.Destination, job.Storage?.DestinationUserName, access.Destination);
        ValidateCredential(job.Storage?.SecondaryFolder, job.Storage?.SecondaryUserName, access.Secondary);
        ValidateCredential(BackupLocations.SourceFolder(job), job.Storage?.SourceUserName, access.Source);
    }

    private static void ValidateCredential(string? folder, string? userName, ShareCredentialSnapshot? credential)
    {
        bool matches = userName is null ? credential is null
            : credential is not null && credential.ProtectedPassword.Length > 0
                && credential.UserName == userName
                && string.Equals(credential.Folder.TrimEnd('\\'), folder?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        if (!matches)
        {
            throw new IOException("The protected network credentials do not match this job's folder/user. Re-enter and save the password.");
        }
    }

    public async Task<StorageAccessSnapshot> PrepareAsync(BackupJob job, StoragePasswords? passwords,
        CancellationToken cancellationToken)
    {
        var previous = await ReadJobAsync(job.Id, cancellationToken);
        return new StorageAccessSnapshot(
            Prepare(job.Destination, job.Storage?.DestinationUserName, passwords?.Destination, previous.Destination),
            Prepare(job.Storage?.SecondaryFolder, job.Storage?.SecondaryUserName, passwords?.Secondary, previous.Secondary),
            Prepare(BackupLocations.SourceFolder(job), job.Storage?.SourceUserName, passwords?.Source, previous.Source));
    }

    public async Task<StorageAccessSnapshot> ReadJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(store.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM JobStorageSecrets WHERE JobId = $id;";
        command.Parameters.AddWithValue("$id", jobId.ToString());
        string? payload = await command.ExecuteScalarAsync(cancellationToken) as string;
        return payload is null ? new StorageAccessSnapshot()
            : JsonSerializer.Deserialize<StorageAccessSnapshot>(payload) ?? throw new InvalidDataException("Invalid storage credentials.");
    }

    public async Task<StorageAccessSnapshot> ReadRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(store.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM RunStorageSecrets WHERE RunId = $id;";
        command.Parameters.AddWithValue("$id", runId.ToString());
        string? payload = await command.ExecuteScalarAsync(cancellationToken) as string;
        return payload is null ? new StorageAccessSnapshot()
            : JsonSerializer.Deserialize<StorageAccessSnapshot>(payload) ?? throw new InvalidDataException("Invalid run storage credentials.");
    }

    private static ShareCredentialSnapshot? Prepare(string? folder, string? userName, string? password,
        ShareCredentialSnapshot? previous)
    {
        if (userName is null)
        {
            return null;
        }

        if (folder?.StartsWith(@"\\", StringComparison.Ordinal) != true || string.IsNullOrWhiteSpace(userName)
            || userName.Length > 256 || password?.Length > 1024)
        {
            throw new ArgumentException("Network credentials require a UNC folder, user name and supported password length.");
        }

        if (!string.IsNullOrEmpty(password))
        {
            byte[] plaintext = Encoding.UTF8.GetBytes(password);
            try
            {
                return new ShareCredentialSnapshot(folder, userName,
                    ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        if (previous is not null && previous.Folder.TrimEnd('\\').Equals(folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            && previous.UserName == userName)
        {
            return previous;
        }

        throw new ArgumentException("Enter the network password for this folder/user. Credentials are never borrowed from another job or destination.");
    }
}
