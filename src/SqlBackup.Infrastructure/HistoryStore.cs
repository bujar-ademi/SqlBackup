using Microsoft.Data.Sqlite;
using SqlBackup.Core;
using System.Text.Json;

namespace SqlBackup.Infrastructure;

public sealed class HistoryStore(ConfigurationStore store)
{
    public async Task<HistoryPage> QueryAsync(HistoryQuery query, CancellationToken cancellationToken)
    {
        HistoryValidation.Validate(query);

        await using var connection = new SqliteConnection(store.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Payload FROM Runs
            WHERE ($job IS NULL OR JobId = $job)
              AND ($status IS NULL OR Status = $status)
              AND ($type IS NULL OR COALESCE(json_extract(Payload, '$.Type'), 0) = $type)
              AND ($from IS NULL OR CreatedUtc >= $from)
              AND ($to IS NULL OR CreatedUtc < $to)
              AND ($database IS NULL OR EXISTS (
                  SELECT 1 FROM json_each(Runs.Payload, '$.Databases')
                  WHERE json_extract(value, '$.Database') = $database))
            ORDER BY CreatedUtc DESC, rowid DESC LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$job", (object?)query.JobId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", query.Status.HasValue ? (int)query.Status.Value : DBNull.Value);
        command.Parameters.AddWithValue("$type", query.Type.HasValue ? (int)query.Type.Value : DBNull.Value);
        command.Parameters.AddWithValue("$from", (object?)query.FromUtc?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)query.ToUtc?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$database", string.IsNullOrWhiteSpace(query.Database) ? DBNull.Value : query.Database.Trim());
        command.Parameters.AddWithValue("$limit", query.Limit + 1);
        command.Parameters.AddWithValue("$offset", query.Offset);
        var items = new List<RunSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var run = JsonSerializer.Deserialize<BackupRun>(reader.GetString(0))
                ?? throw new InvalidDataException("Invalid run history.");
            items.Add(RunPresentation.Summarize(run));
        }

        return new HistoryPage(items.Take(query.Limit).ToArray(), items.Count > query.Limit, query.Offset);
    }

    public Task<RunLogEntry[]> ReadLogAsync(Guid id, CancellationToken cancellationToken)
    {
        return RunEventStore.ReadAsync(store, id, cancellationToken);
    }

    public async Task<BackupRun> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(store.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM Runs WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        string? payload = await command.ExecuteScalarAsync(cancellationToken) as string;
        return payload is null ? throw new ArgumentException("Run history is no longer available.")
            : JsonSerializer.Deserialize<BackupRun>(payload) ?? throw new InvalidDataException("Invalid run history.");
    }
}
