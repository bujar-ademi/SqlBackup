using Microsoft.Data.Sqlite;
using SqlBackup.Core;
using System.Text.Json;

namespace SqlBackup.Infrastructure;

public sealed class RunStore(ConfigurationStore store)
{
    public Task<BackupRun> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        return new HistoryStore(store).GetAsync(id, cancellationToken);
    }

    public async Task AddAsync(BackupRun run, byte[]? protectedPassword, CancellationToken cancellationToken,
        StorageAccessSnapshot? storageAccess = null, DestinationCredentialSnapshot[]? destinationAccess = null)
    {
        await using var connection = new SqliteConnection(store.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Runs (Id, JobId, Status, CreatedUtc, Payload, ProtectedPassword, ScheduledKey)
            VALUES ($id, $job, $status, $created, $payload, $password, $key);
            """;
        command.Parameters.AddWithValue("$id", run.Id.ToString());
        command.Parameters.AddWithValue("$job", run.Job.Id.ToString());
        command.Parameters.AddWithValue("$status", (int)run.Status);
        command.Parameters.AddWithValue("$created", run.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(run));
        command.Parameters.AddWithValue("$password", (object?)protectedPassword ?? DBNull.Value);
        command.Parameters.AddWithValue("$key", (object?)run.OccurrenceKey ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "INSERT INTO RunStorageSecrets (RunId, Payload) VALUES ($id, $access);";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", run.Id.ToString());
        command.Parameters.AddWithValue("$access", JsonSerializer.Serialize(storageAccess ?? new StorageAccessSnapshot()));
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "INSERT INTO RunDestinationSecrets (RunId, Payload) VALUES ($id, $destinations);";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", run.Id.ToString());
        command.Parameters.AddWithValue("$destinations", JsonSerializer.Serialize(destinationAccess ?? []));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await RunEventStore.AppendAsync(connection, transaction, null, run, cancellationToken);
        transaction.Commit();
    }

    public async Task UpdateAsync(BackupRun run, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(store.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = "SELECT Payload FROM Runs WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", run.Id.ToString());
        string? previousPayload = await command.ExecuteScalarAsync(cancellationToken) as string;
        var previous = previousPayload is null ? null : JsonSerializer.Deserialize<BackupRun>(previousPayload);
        command.Parameters.Clear();
        command.CommandText = "UPDATE Runs SET Status = $status, Payload = $payload WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", run.Id.ToString());
        command.Parameters.AddWithValue("$status", (int)run.Status);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(run));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Run history could not be updated.");
        }

        await RunEventStore.AppendAsync(connection, transaction, previous, run, cancellationToken);
        var notification = RunPresentation.Notification(run);
        if (notification is not null)
        {
            await NotificationStore.InsertAsync(connection, transaction, notification, cancellationToken);
        }

        transaction.Commit();
    }

    public async Task<Guid?> FindOccurrenceAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(store.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM Runs WHERE ScheduledKey = $key;";
        command.Parameters.AddWithValue("$key", key);
        string? value = await command.ExecuteScalarAsync(cancellationToken) as string;
        return value is null ? null : Guid.Parse(value);
    }

    public async Task<BackupRun[]> ListAsync(CancellationToken cancellationToken, bool unfinishedOnly = false, bool all = false)
    {
        await using var connection = new SqliteConnection(store.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = unfinishedOnly
            ? "SELECT Payload FROM Runs WHERE Status IN (0, 1) ORDER BY CreatedUtc;"
            : "SELECT Payload FROM Runs ORDER BY CreatedUtc DESC, rowid DESC" + (all ? ";" : " LIMIT 20;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var runs = new List<BackupRun>();
        while (await reader.ReadAsync(cancellationToken))
        {
            runs.Add(JsonSerializer.Deserialize<BackupRun>(reader.GetString(0))
                ?? throw new InvalidDataException("A saved run is invalid."));
        }

        return runs.ToArray();
    }

    public async Task<ConnectionSnapshot> ReadConnectionAsync(BackupRun run, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(store.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ProtectedPassword FROM Runs WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", run.Id.ToString());
        byte[]? password = await command.ExecuteScalarAsync(cancellationToken) as byte[];
        return new ConnectionSnapshot(run.Connection, password);
    }

    public async Task<BackupRun[]> ReadOperationalHistoryAsync(BackupJob[] jobs, Guid? selectedJobId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(store.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var selectedRuns = new Dictionary<Guid, BackupRun>();
        foreach (var job in jobs)
        {
            await ReadLatestAsync(job, null);
            if (job.Id == selectedJobId)
            {
                foreach (string database in job.Databases)
                {
                    await ReadLatestAsync(job, database);
                }
            }
        }

        return selectedRuns.Values.OrderByDescending(run => run.CreatedUtc).ToArray();

        async Task ReadLatestAsync(BackupJob job, string? database)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Payload FROM Runs
                WHERE JobId = $job
                  AND json_extract(Payload, '$.Connection.Server') = $server COLLATE NOCASE
                  AND ($database IS NULL OR EXISTS (
                      SELECT 1 FROM json_each(Runs.Payload, '$.Databases')
                      WHERE json_extract(value, '$.Database') = $database
                        AND json_extract(value, '$.Backup') = $succeeded))
                ORDER BY CreatedUtc DESC, rowid DESC LIMIT 1;
                """;
            command.Parameters.AddWithValue("$job", job.Id.ToString());
            command.Parameters.AddWithValue("$server", (object?)job.Connection?.Server ?? DBNull.Value);
            command.Parameters.AddWithValue("$database", (object?)database ?? DBNull.Value);
            command.Parameters.AddWithValue("$succeeded", (int)StageStatus.Succeeded);
            string? payload = await command.ExecuteScalarAsync(cancellationToken) as string;
            if (payload is not null)
            {
                var run = JsonSerializer.Deserialize<BackupRun>(payload)
                    ?? throw new InvalidDataException("Invalid operational run history.");
                selectedRuns.TryAdd(run.Id, run);
            }
        }
    }

    public async Task ReconcileInterruptedAsync(CancellationToken cancellationToken)
    {
        foreach (var run in await ListAsync(cancellationToken, unfinishedOnly: true))
        {
            var databases = run.Databases.Select(result => result with
            {
                Backup = result.Backup == StageStatus.Running ? StageStatus.Unknown : result.Backup,
                Verification = result.Verification == StageStatus.Running ? StageStatus.Unknown : result.Verification,
                StagingCleanup = result.StagingCleanup == StageStatus.Running ? StageStatus.Unknown : result.StagingCleanup,
                DestinationTransfers = result.DestinationTransfers?.Select(transfer => transfer.Status is StageStatus.Running or StageStatus.Pending
                    ? transfer with { Status = StageStatus.Unknown, Message = "Service stopped during transfer. Remote completion has not been confirmed." }
                    : transfer).ToArray(),
                Copies = result.Copies?.Select(copy => copy.Status is StageStatus.Running or StageStatus.Pending
                    ? copy with { Status = StageStatus.Unknown, Message = "Service stopped during transfer. Partial output is not a completed copy." }
                    : copy).ToArray(),
                Message = result.Backup == StageStatus.Running || result.Verification == StageStatus.Running
                    ? "Service stopped; SQL outcome must be checked manually."
                    : result.Message
            }).ToArray();
            await UpdateAsync(run with
            {
                Status = RunStatus.Interrupted,
                CompletedUtc = DateTimeOffset.UtcNow,
                Databases = databases,
                Message = "Interrupted by service restart. Not retried. Check SQL Server before starting another backup."
            }, cancellationToken);
        }
    }
}
