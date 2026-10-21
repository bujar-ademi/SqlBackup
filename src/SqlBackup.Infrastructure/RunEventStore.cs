using Microsoft.Data.Sqlite;
using SqlBackup.Core;
using System.Text.Json;

namespace SqlBackup.Infrastructure;

public static class RunEventStore
{
    internal static async Task AppendAsync(SqliteConnection connection, SqliteTransaction transaction,
        BackupRun? previous, BackupRun current, CancellationToken cancellationToken)
    {
        foreach (var entry in RunLogProjection.Changes(previous, current, DateTimeOffset.UtcNow))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO RunEvents (RunId, Payload) VALUES ($run, $payload);";
            command.Parameters.AddWithValue("$run", current.Id.ToString());
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(entry));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public static async Task<RunLogEntry[]> ReadAsync(ConfigurationStore store, Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(store.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Bound IPC output even for malformed or unusually chatty legacy histories.
        command.CommandText = "SELECT Payload FROM RunEvents WHERE RunId = $run ORDER BY Id LIMIT 512;";
        command.Parameters.AddWithValue("$run", runId.ToString());
        var entries = new List<RunLogEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(JsonSerializer.Deserialize<RunLogEntry>(reader.GetString(0))
                ?? throw new InvalidDataException("Invalid run log evidence."));
        }

        return entries.ToArray();
    }
}
