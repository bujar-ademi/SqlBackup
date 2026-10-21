using Microsoft.Data.Sqlite;
using SqlBackup.Core;
using System.Text.Json;

namespace SqlBackup.Infrastructure;

public sealed class CleanupAuditStore(ConfigurationStore store)
{
    public async Task AddAsync(CleanupAudit audit, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(store.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO CleanupAudit (Id, CreatedUtc, JobId, Payload) VALUES ($id, $created, $job, $payload);";
        command.Parameters.AddWithValue("$id", audit.Id.ToString());
        command.Parameters.AddWithValue("$created", audit.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$job", audit.JobId.ToString());
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(audit));
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (audit.Outcome == "Failed")
        {
            var notification = new NotificationMessage("cleanup/" + audit.Id.ToString("N"), audit.CreatedUtc,
                audit.JobId, null, "Warning", "Cleanup needs attention",
                "A cleanup deletion could not be confirmed. Review the cleanup audit before another attempt.");
            await NotificationStore.InsertAsync(connection, transaction, notification, cancellationToken);
        }

        transaction.Commit();
    }

    public async Task<CleanupAudit[]> ListAsync(Guid jobId, CancellationToken cancellationToken, bool all = false)
    {
        await using var connection = new SqliteConnection(store.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM CleanupAudit WHERE JobId = $job ORDER BY rowid DESC" + (all ? ";" : " LIMIT 100;");
        command.Parameters.AddWithValue("$job", jobId.ToString());
        var results = new List<CleanupAudit>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(JsonSerializer.Deserialize<CleanupAudit>(reader.GetString(0))
                ?? throw new InvalidDataException("Invalid cleanup audit."));
        }

        return results.ToArray();
    }
}
