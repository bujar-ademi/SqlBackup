using Microsoft.Data.Sqlite;
using SqlBackup.Core;
using System.Text.Json;

namespace SqlBackup.Infrastructure;

public static class JobConnectionMigration
{
    public static async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Metadata, ProtectedPassword FROM Configuration WHERE Id = 1;";
        ConnectionConfiguration? legacy = null;
        byte[]? password = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                legacy = JsonSerializer.Deserialize<ConnectionConfiguration>(reader.GetString(0));
                password = reader.IsDBNull(1) ? null : (byte[])reader.GetValue(1);
            }
        }

        if (legacy is null)
        {
            return;
        }

        var jobs = new List<BackupJob>();
        command.CommandText = "SELECT Payload FROM Jobs;";
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var job = JsonSerializer.Deserialize<BackupJob>(reader.GetString(0))
                    ?? throw new InvalidDataException("A legacy job is invalid.");
                if (job.Connection is null)
                {
                    jobs.Add(job);
                }
            }
        }

        foreach (var job in jobs)
        {
            var migrated = job with
            {
                Revision = checked(job.Revision + 1),
                Connection = legacy with { HasPassword = !legacy.UseWindowsAuthentication && password is not null }
            };
            command.CommandText = "UPDATE Jobs SET Revision = $revision, Payload = $payload WHERE Id = $id;";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", job.Id.ToString());
            command.Parameters.AddWithValue("$revision", migrated.Revision);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(migrated));
            await command.ExecuteNonQueryAsync(cancellationToken);
            command.CommandText = "INSERT INTO JobSecrets (JobId, ProtectedPassword) VALUES ($id, $password);";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", job.Id.ToString());
            command.Parameters.AddWithValue("$password", legacy.UseWindowsAuthentication ? DBNull.Value : (object?)password ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
