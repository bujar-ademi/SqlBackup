using Microsoft.Data.Sqlite;
using SqlBackup.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlBackup.Infrastructure;

public sealed class ConnectionSecrets(ConfigurationStore store)
{
    public async Task<ConnectionSnapshot> ReadSnapshotAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await new JobStore(store).GetAsync(jobId, cancellationToken);
        return await PrepareAsync(job, null, cancellationToken);
    }

    public async Task<ConnectionSnapshot> PrepareAsync(BackupJob job, string? password, CancellationToken cancellationToken)
    {
        ConnectionValidation.Validate(job.Connection);
        var requested = job.Connection!;
        await using var connection = new SqliteConnection(store.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT j.Payload, s.ProtectedPassword FROM Jobs j
            LEFT JOIN JobSecrets s ON s.JobId = j.Id WHERE j.Id = $id;
            """;
        command.Parameters.AddWithValue("$id", job.Id.ToString());
        BackupJob? previous = null;
        byte[]? protectedPassword = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                previous = JsonSerializer.Deserialize<BackupJob>(reader.GetString(0));
                protectedPassword = reader.IsDBNull(1) ? null : (byte[])reader.GetValue(1);
            }
        }

        if (previous is not null && previous.Revision != job.Revision)
        {
            throw new ArgumentException("This job changed. Reload it before saving or inspecting its connection.");
        }

        if (previous?.Connection?.Server != requested.Server
            || previous?.Connection?.UserName != requested.UserName
            || previous?.Connection?.UseWindowsAuthentication != requested.UseWindowsAuthentication)
        {
            protectedPassword = null;
        }

        if (password?.Length > 1024)
        {
            throw new ArgumentException("The password exceeds the supported length.");
        }

        if (requested.UseWindowsAuthentication)
        {
            protectedPassword = null;
        }
        else if (!string.IsNullOrEmpty(password))
        {
            byte[] plaintext = Encoding.UTF8.GetBytes(password);
            try
            {
                protectedPassword = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        if (!requested.UseWindowsAuthentication && protectedPassword is null)
        {
            throw new ArgumentException("Enter the SQL password for this job. Passwords are never borrowed from another job.");
        }

        var metadata = requested with
        {
            HasPassword = protectedPassword is not null,
            UserName = requested.UseWindowsAuthentication ? null : requested.UserName
        };
        return new ConnectionSnapshot(metadata, protectedPassword);
    }

    public static string? UnprotectPassword(ConnectionSnapshot snapshot)
    {
        if (snapshot.Configuration.UseWindowsAuthentication)
        {
            return null;
        }

        if (snapshot.ProtectedPassword is null)
        {
            throw new ArgumentException("Re-enter and save the SQL password under the service identity.");
        }

        byte[] plaintext = ProtectedData.Unprotect(snapshot.ProtectedPassword, null, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
