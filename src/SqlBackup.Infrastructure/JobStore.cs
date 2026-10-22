using Microsoft.Data.Sqlite;
using SqlBackup.Core;
using System.Text.Json;

namespace SqlBackup.Infrastructure;

public sealed class JobStore(ConfigurationStore store)
{
    public Task<DestinationCredentialSnapshot[]> PrepareDestinationsAsync(BackupJob job,
        DestinationPassword[]? passwords, CancellationToken cancellationToken)
    {
        return new DestinationSecrets(store).PrepareAsync(job, passwords, cancellationToken);
    }

    public Task<StorageAccessSnapshot> PrepareStorageAsync(BackupJob job, StoragePasswords? passwords,
        CancellationToken cancellationToken)
    {
        return new StorageSecrets(store).PrepareAsync(job, passwords, cancellationToken);
    }

    public async Task<BackupJob[]> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(store.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM Jobs ORDER BY rowid LIMIT 100;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var jobs = new List<BackupJob>();
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(JsonSerializer.Deserialize<BackupJob>(reader.GetString(0))
                ?? throw new InvalidDataException("A saved job is invalid."));
        }

        return jobs.ToArray();
    }

    public async Task<BackupJob> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var jobs = await ListAsync(cancellationToken);
        return jobs.SingleOrDefault(job => job.Id == id)
            ?? throw new ArgumentException("The job no longer exists. Refresh the job list.");
    }

    public async Task SaveAsync(BackupJob job, CancellationToken cancellationToken, string? password = null,
        StoragePasswords? storagePasswords = null, DestinationPassword[]? destinationPasswords = null)
    {
        JobValidation.Validate(job);
        if (job.Schedule is not null)
        {
            ScheduleCalculator.Validate(job.Schedule);
        }
        var snapshot = await new ConnectionSecrets(store).PrepareAsync(job, password, cancellationToken);
        var storageAccess = await new StorageSecrets(store).PrepareAsync(job, storagePasswords, cancellationToken);
        var destinationAccess = await new DestinationSecrets(store).PrepareAsync(job, destinationPasswords, cancellationToken);
        await using var connection = new SqliteConnection(store.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (job.Revision == 0)
        {
            command.CommandText = "SELECT COUNT(*) FROM Jobs;";
            if ((long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L) >= 100)
            {
                throw new ArgumentException("This installation supports up to 100 jobs.");
            }

            command.CommandText = "INSERT OR IGNORE INTO Jobs (Id, Revision, Payload) VALUES ($id, $revision, $payload);";
        }
        else
        {
            command.CommandText = """
                UPDATE Jobs SET Revision = $revision, Payload = $payload
                WHERE Id = $id AND Revision = $expected;
                """;
            command.Parameters.AddWithValue("$expected", job.Revision);
        }

        var saved = job with { Revision = checked(job.Revision + 1), Connection = snapshot.Configuration };
        command.Parameters.AddWithValue("$id", job.Id.ToString());
        command.Parameters.AddWithValue("$revision", saved.Revision);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(saved));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new ArgumentException("The job changed or was deleted. Reload it before saving.");
        }

        command.CommandText = """
            INSERT INTO JobSecrets (JobId, ProtectedPassword) VALUES ($id, $password)
            ON CONFLICT(JobId) DO UPDATE SET ProtectedPassword = excluded.ProtectedPassword;
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", job.Id.ToString());
        command.Parameters.AddWithValue("$password", (object?)snapshot.ProtectedPassword ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = """
            INSERT INTO JobStorageSecrets (JobId, Payload) VALUES ($id, $payload)
            ON CONFLICT(JobId) DO UPDATE SET Payload = excluded.Payload;
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", job.Id.ToString());
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(storageAccess));
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = """
            INSERT INTO JobDestinationSecrets (JobId, Payload) VALUES ($id, $payload)
            ON CONFLICT(JobId) DO UPDATE SET Payload = excluded.Payload;
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", job.Id.ToString());
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(destinationAccess));
        await command.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
    }

    public async Task DeleteAsync(BackupJob job, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(store.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM Jobs WHERE Id = $id AND Revision = $revision;";
        command.Parameters.AddWithValue("$id", job.Id.ToString());
        command.Parameters.AddWithValue("$revision", job.Revision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new ArgumentException("The job changed or was deleted. Reload it before deleting.");
        }

        command.CommandText = "DELETE FROM JobSecrets WHERE JobId = $id;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "DELETE FROM JobStorageSecrets WHERE JobId = $id;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "DELETE FROM JobDestinationSecrets WHERE JobId = $id;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "DELETE FROM ScheduleState WHERE JobId = $id;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
    }
}
